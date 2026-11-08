using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NodeRunner.Editor.Online;

// What a session works on: this computer's map and its history.
public interface IOnlineDocument
{
    EditCodecs Codecs { get; }
    EditHistory History { get; }

    // Null while the map is loading.
    EditContext? Context { get; }

    // Guest: opens the project received from the host and starts loading its map (Ready once loaded).
    void OpenSnapshot(SessionSnapshot snapshot);

    // The map is loaded (after OpenSnapshot).
    bool IsReady { get; }

    // Same text on two computers = same game files and same project, before any edit of the session.
    string Fingerprint { get; }

    // A change of another participant applied here (or one's own undo / redo, done by the host).
    void Applied(IEdit edit, string author, SessionAction action);

    void Notify(string text, bool warning = false);
}

// The host's project when the session started (its files, zipped), which every guest starts from.
public sealed record SessionSnapshot(string ProjectName, byte[] ProjectZip, string Fingerprint);

public enum SessionRole { Host, Guest }

public enum SessionAction { Edit, Undo, Redo }

public sealed record JoinRequest(int Id, string Name, string Address);

// A session where several computers edit the same map. The host decides the order of every change: it applies them,
// then sends them to the guests, who apply them in the same order (EditCodecs). A guest applies its own change at once
// and sends it with the number of changes it had seen; if another change got in first, the host refuses it and sends
// the guest the whole session again (the project at the start, then every change). Undo and redo go through the host
// too: they undo the session's last change, whoever made it.
public sealed class OnlineSession : IDisposable
{
    public const int ProtocolVersion = 2;
    public const int DefaultPort = 47800;
    public const int MaxParticipants = 5;

    // A guest whose link dropped tries again this long (the relay keeps the room a minute for a host that dropped).
    public static readonly TimeSpan ReconnectFor = TimeSpan.FromSeconds(90);

    private readonly IOnlineDocument _doc;
    private readonly ConcurrentQueue<(Connection Connection, MessageType Type, byte[] Data)> _incoming = new();
    private readonly List<Connection> _connections = [];
    private readonly List<(string Author, byte[] Message)> _log = [];
    private readonly List<(string Author, byte[] Message)> _redoLog = [];
    private readonly List<JoinRequest> _joinRequests = [];
    private TcpListener? _listener;
    private RelayHost? _relay;
    private string? _relayUrl;
    private string? _code;
    private string? _failure;
    private readonly Dictionary<string, string> _rejoinKeys = [];
    private string _rejoinKey = "";
    private bool _reconnecting;
    private DateTime _reconnectUntil;
    private DateTime _nextAttempt;
    private string _address = "";
    private Connection? _host;
    private SessionSnapshot? _snapshot;
    private object? _library;
    private int _opCount;
    private int _pending;
    private bool _loading;
    private bool _diverged;
    private bool _applying;
    private int _nextId;

    private OnlineSession(IOnlineDocument doc, SessionRole role, string name, string appVersion)
    {
        _doc = doc;
        Role = role;
        Name = name;
        AppVersion = appVersion;
        Participants = [name];
        doc.History.Added += OnAdded;
        doc.History.Undone += OnUndone;
        doc.History.Redone += OnRedone;
    }

    public SessionRole Role { get; }
    public string Name { get; private set; }
    public string AppVersion { get; }
    public int Port { get; private set; }
    public IReadOnlyList<string> Participants { get; private set; }
    public IReadOnlyList<JoinRequest> JoinRequests => _joinRequests;
    public string Status { get; private set; } = "";
    public string? EndReason { get; private set; }
    public bool IsOver => EndReason is not null;

    // Guest: the host's map is loading, or the link is being restored (no change can be made).
    public bool IsLoading => _loading || _reconnecting || (Role == SessionRole.Guest && _snapshot is null);

    // Through the relay (code NRS-XXXX) rather than a direct address.
    public bool ViaRelay => _relayUrl is not null;

    // The session's code (host: null until the relay gave it).
    public string? Code => _relay?.Code ?? _code;

    // The link dropped and is being restored on its own.
    public bool IsReconnecting => _reconnecting || (Role == SessionRole.Host && _relay is { IsOnline: false });

    // Number of changes of the session (all participants), undo and redo included.
    public int ChangeCount => _opCount;

    // --- Starting ---

    // Opens a session on this computer (the current map must be the project's files, with no change yet).
    public static OnlineSession Host(IOnlineDocument doc, string name, string appVersion, SessionSnapshot snapshot, int port = DefaultPort)
    {
        var session = new OnlineSession(doc, SessionRole.Host, name, appVersion) { _snapshot = snapshot, _library = doc.Context?.Library };
        Exception? last = null;
        for (int p = port; p < port + 10; p++)
        {
            var listener = new TcpListener(IPAddress.IPv6Any, p);
            try
            {
                listener.Server.DualMode = true;
                listener.Start();
                session._listener = listener;
                session.Port = p;
                break;
            }
            catch (SocketException ex)
            {
                listener.Stop();
                last = ex;
            }
        }
        if (session._listener is null)
        {
            session.Dispose();
            throw new IOException(L($"Aucun port libre ({port} à {port + 9}) : {last?.Message}"));
        }
        session.Status = L($"Session ouverte (port {session.Port})");
        _ = Task.Run(session.AcceptLoop);
        return session;
    }

    // Opens a session through the relay (wss://...): the relay gives its code (Code), guests join with it.
    public static OnlineSession HostOnRelay(IOnlineDocument doc, string name, string appVersion, SessionSnapshot snapshot, string relayUrl)
    {
        var session = new OnlineSession(doc, SessionRole.Host, name, appVersion)
        {
            _snapshot = snapshot,
            _library = doc.Context?.Library,
            _relayUrl = relayUrl,
            Status = L("Connexion au relais..."),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var relay = await RelayHost.OpenAsync(relayUrl, session.Arrived, timeout.Token);
                if (session.IsOver)
                {
                    await relay.CloseAsync();
                    return;
                }
                session._relay = relay;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
            {
                session._failure = ex is OperationCanceledException ? L("le relais ne répond pas") : ex.Message;
            }
        });
        return session;
    }

    // A guest arrived through the relay (background thread).
    private void Arrived(Link link)
    {
        var connection = new Connection(link, Interlocked.Increment(ref _nextId), Received, Closed);
        lock (_connections)
            _connections.Add(connection);
    }

    // "NRS-AB12", "nrs ab12" or "AB12" = a session code (written NRS-AB12); anything else is an address.
    public static string? ParseCode(string text)
    {
        string compact = new([.. text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
        if (compact.StartsWith("NRS", StringComparison.Ordinal) && compact.Length == 7)
            compact = compact[3..];
        else if (compact.Length != 4 || text.Contains('.') || text.Contains(':'))
            return null;
        return compact.All(c => c is >= 'A' and <= 'Z' and not 'I' and not 'O' or >= '2' and <= '9') ? "NRS-" + compact : null;
    }

    // Joins the session with a code (through relayUrl) or at an address ("192.168.1.20" or "192.168.1.20:47800").
    public static OnlineSession Join(IOnlineDocument doc, string name, string appVersion, string address, string? relayUrl = null)
    {
        var session = new OnlineSession(doc, SessionRole.Guest, name, appVersion) { _address = address.Trim() };
        if (relayUrl is not null && ParseCode(address) is { } code)
        {
            session._relayUrl = relayUrl;
            session._code = code;
        }
        session.Status = session._code is { } c ? L($"Connexion à la session {c}...") : L($"Connexion à {address}...");
        session.Connect();
        return session;
    }

    // Guest: opens the link to the host and says hello (again after a drop: the host then takes us back at once).
    private void Connect()
    {
        string name = Name, appVersion = AppVersion, key = _rejoinKey, signature = _doc.Codecs.Signature;
        string? relay = _relayUrl, code = _code;
        string address = _address;
        _ = Task.Run(async () =>
        {
            Link link;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (relay is not null)
                {
                    link = new RelayGuestLink(new RelaySocket(await Relay.ConnectAsync(Relay.JoinUri(relay, code!), timeout.Token)));
                }
                else
                {
                    var (host, port) = ParseAddress(address);
                    var client = new TcpClient(AddressFamily.InterNetworkV6) { Client = { DualMode = true } };
                    try
                    {
                        await client.ConnectAsync(host, port, timeout.Token);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                    link = new TcpLink(client);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException or IOException)
            {
                _incoming.Enqueue((null!, MessageType.Closed, System.Text.Encoding.UTF8.GetBytes(
                    ex is OperationCanceledException ? L("l'hôte ne répond pas") : ex.Message)));
                return;
            }
            var connection = new Connection(link, 0, Received, Closed);
            _host = connection;
            connection.Send(MessageType.Hello, Payload.Build(w =>
            {
                w.Write(ProtocolVersion);
                w.Write(appVersion);
                w.Write(signature);
                w.Write(name);
                w.Write(key);
            }));
        });
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        if (IPEndPoint.TryParse(address, out var endPoint) && (endPoint.Port != 0 || !address.Contains(':') || address.Count(c => c == ':') > 1))
            return (endPoint.Address.ToString(), endPoint.Port == 0 ? DefaultPort : endPoint.Port);
        int colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out int port) && port is > 0 and < 65536)
            return (address[..colon], port);
        return (address, DefaultPort);
    }

    // The addresses others on the same network can join (IPv4 of this computer).
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return
            [
                .. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => a.Address.ToString())
                    .Distinct(),
            ];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    private async Task AcceptLoop()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var connection = new Connection(new TcpLink(client), Interlocked.Increment(ref _nextId), Received, Closed);
            lock (_connections)
                _connections.Add(connection);
        }
    }

    private void Received(Connection connection, MessageType type, byte[] data) => _incoming.Enqueue((connection, type, data));

    private void Closed(Connection connection, string reason) =>
        _incoming.Enqueue((connection, MessageType.Closed, System.Text.Encoding.UTF8.GetBytes(reason)));

    // --- Every frame, on the interface's thread ---

    public void Update()
    {
        if (IsOver)
            return;
        if ((_failure ?? _relay?.Failure) is { } failure)
        {
            End(L($"Relais : {failure}. Session terminée."));
            return;
        }
        if (_reconnecting && _host is null && DateTime.UtcNow >= _nextAttempt)
        {
            _nextAttempt = DateTime.MaxValue;
            Connect();
        }
        if ((Role == SessionRole.Host || (_library is not null && !_loading)) && _doc.Context?.Library != _library)
        {
            End(L("La carte a été rechargée : session terminée."));
            return;
        }
        if (_loading)
        {
            if (!_doc.IsReady)
                return;
            FinishLoading();
            if (IsOver)
                return;
        }
        while (!IsOver && !_loading && _incoming.TryDequeue(out var message))
        {
            try
            {
                if (Role == SessionRole.Host)
                    HandleAsHost(message.Connection, message.Type, message.Data);
                else
                    HandleAsGuest(message.Connection, message.Type, message.Data);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException
                or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                if (Role == SessionRole.Host)
                    Drop(message.Connection, L($"message illisible ({ex.Message})"));
                else
                    End(L($"Désynchronisé de l'hôte ({ex.Message}) : rejoignez à nouveau la session."));
            }
        }
    }

    // --- Host ---

    private Connection[] Guests()
    {
        lock (_connections)
            return [.. _connections.Where(c => c.Accepted)];
    }

    private void Broadcast(MessageType type, byte[] data, Connection? except = null)
    {
        foreach (var guest in Guests())
        {
            if (guest != except)
                guest.Send(type, data);
        }
    }

    private void HandleAsHost(Connection connection, MessageType type, byte[] data)
    {
        if (type == MessageType.Closed)
        {
            Drop(connection, null);
            return;
        }
        using var r = Payload.Reader(data);
        if (!connection.Accepted)
        {
            if (type == MessageType.Hello && connection.Name.Length == 0)
                OnHello(connection, r);
            return;
        }
        switch (type)
        {
            case MessageType.Edit:
                OnGuestEdit(connection, r.ReadInt32(), r.ReadBytes());
                break;
            case MessageType.UndoRequest:
            case MessageType.RedoRequest:
                bool undo = type == MessageType.UndoRequest;
                if (r.ReadInt32() != _opCount || !(undo ? _doc.History.CanUndo : _doc.History.CanRedo))
                    return;
                var edit = Run(() => undo ? _doc.History.Undo() : _doc.History.Redo());
                if (edit is null)
                    return;
                MoveLast(undo ? _log : _redoLog, undo ? _redoLog : _log);
                _opCount++;
                Broadcast(undo ? MessageType.Undo : MessageType.Redo, Payload.Build(w => w.Write(connection.Name)));
                _doc.Applied(edit, connection.Name, undo ? SessionAction.Undo : SessionAction.Redo);
                break;
            case MessageType.Bye:
                _rejoinKeys.Remove(connection.RejoinKey);
                Drop(connection, null);
                break;
        }
    }

    private void OnHello(Connection connection, BinaryReader r)
    {
        int protocol = r.ReadInt32();
        string appVersion = r.ReadString();
        string signature = r.ReadString();
        string name = CleanName(r.ReadString());
        string key = protocol == ProtocolVersion ? r.ReadString() : "";
        string? refusal =
            protocol != ProtocolVersion || appVersion != AppVersion || signature != _doc.Codecs.Signature
                ? L($"Versions différentes : l'hôte a NodeRunner {AppVersion}, vous avez {appVersion}. Installez la même version.")
            : Guests().Length + 1 + _joinRequests.Count >= MaxParticipants ? L($"Session complète ({MaxParticipants} personnes maximum).")
            : null;
        // Back after a drop: taken in again without asking (its older link is let go).
        if (key.Length > 0 && _rejoinKeys.TryGetValue(key, out string? back) && protocol == ProtocolVersion
            && appVersion == AppVersion && signature == _doc.Codecs.Signature)
        {
            foreach (var old in Guests().Where(g => g.RejoinKey == key))
                Drop(old, null, quiet: true);
            connection.Name = back;
            connection.RejoinKey = key;
            connection.Accepted = true;
            _doc.History.ClearRedo();
            _redoLog.Clear();
            SendWelcome(connection);
            UpdateParticipants();
            _doc.Notify(L($"{back} s'est reconnecté."));
            return;
        }
        if (refusal is not null)
        {
            connection.Send(MessageType.Refused, Payload.Build(w => w.Write(refusal)));
            connection.CloseAfterSending();
            return;
        }
        var taken = Participants.Concat(_joinRequests.Select(j => j.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string unique = name;
        for (int i = 2; taken.Contains(unique); i++)
            unique = $"{name} ({i})";
        connection.Name = unique;
        _joinRequests.Add(new JoinRequest(connection.Id, unique, connection.Address));
        _doc.Notify(L($"{unique} veut rejoindre la session."));
    }

    public void Accept(JoinRequest request)
    {
        _joinRequests.Remove(request);
        var connection = Find(request.Id);
        if (connection is null || Role != SessionRole.Host)
            return;
        connection.Accepted = true;
        connection.RejoinKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        _rejoinKeys[connection.RejoinKey] = connection.Name;
        // The changes undone before are dropped: a guest could not redo them.
        _doc.History.ClearRedo();
        _redoLog.Clear();
        SendWelcome(connection);
        UpdateParticipants();
        _doc.Notify(L($"{request.Name} a rejoint la session."));
    }

    public void Refuse(JoinRequest request)
    {
        _joinRequests.Remove(request);
        if (Find(request.Id) is { } connection)
        {
            connection.Send(MessageType.Refused, Payload.Build(w => w.Write(L("L'hôte a refusé votre demande."))));
            connection.CloseAfterSending();
        }
    }

    private Connection? Find(int id)
    {
        lock (_connections)
            return _connections.Find(c => c.Id == id);
    }

    private void SendWelcome(Connection connection)
    {
        var snapshot = _snapshot!;
        connection.Send(MessageType.Welcome, Payload.Build(w =>
        {
            w.Write(connection.Name);
            w.Write(connection.RejoinKey);
            w.Write(snapshot.ProjectName);
            w.Write(snapshot.Fingerprint);
            w.WriteBytes(snapshot.ProjectZip);
            w.Write(_log.Count);
            foreach (var (author, message) in _log)
            {
                w.Write(author);
                w.WriteBytes(message);
            }
        }));
    }

    private void OnGuestEdit(Connection connection, int seen, byte[] message)
    {
        if (seen != _opCount)
        {
            Resync(connection, L("une autre modification est passée avant"));
            return;
        }
        IEdit edit;
        try
        {
            edit = Run(() => ApplyMessage(message));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or EndOfStreamException)
        {
            Resync(connection, ex.Message);
            return;
        }
        _log.Add((connection.Name, message));
        _redoLog.Clear();
        _opCount++;
        connection.Send(MessageType.Ack, []);
        Broadcast(MessageType.Edit, Payload.Build(w =>
        {
            w.Write(connection.Name);
            w.WriteBytes(message);
        }), except: connection);
        _doc.Applied(edit, connection.Name, SessionAction.Edit);
    }

    private void Resync(Connection connection, string reason)
    {
        connection.Send(MessageType.Reject, Payload.Build(w => w.Write(reason)));
        SendWelcome(connection);
    }

    private void Drop(Connection connection, string? reason, bool quiet = false)
    {
        lock (_connections)
            _connections.Remove(connection);
        _joinRequests.RemoveAll(j => j.Id == connection.Id);
        if (reason is not null)
            connection.Send(MessageType.Bye, Payload.Build(w => w.Write(reason)));
        connection.CloseAfterSending();
        if (connection.Accepted)
        {
            connection.Accepted = false;
            UpdateParticipants();
            if (!quiet)
                _doc.Notify(ViaRelay && _rejoinKeys.ContainsKey(connection.RejoinKey)
                    ? L($"{connection.Name} a perdu la connexion (il peut revenir).")
                    : L($"{connection.Name} a quitté la session."));
        }
    }

    private void UpdateParticipants()
    {
        Participants = [Name, .. Guests().Select(g => g.Name)];
        var names = Participants;
        Broadcast(MessageType.Participants, Payload.Build(w =>
        {
            w.Write(names.Count);
            foreach (var n in names)
                w.Write(n);
        }));
    }

    // --- Guest ---

    private void HandleAsGuest(Connection? connection, MessageType type, byte[] data)
    {
        if (connection is not null && connection != _host)
            return; // an older link
        using var r = Payload.Reader(data);
        switch (type)
        {
            case MessageType.Closed:
                string why = System.Text.Encoding.UTF8.GetString(data);
                _host = null;
                // Through the relay, a dropped link is restored on its own for a while.
                if (ViaRelay && _rejoinKey.Length > 0 && connection is not { IsFinal: true })
                {
                    if (!_reconnecting)
                    {
                        _reconnecting = true;
                        _reconnectUntil = DateTime.UtcNow + ReconnectFor;
                        _doc.Notify(L("Connexion avec l'hôte perdue : reconnexion..."), warning: true);
                    }
                    if (DateTime.UtcNow < _reconnectUntil)
                    {
                        Status = L("Reconnexion à l'hôte...");
                        _nextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                        break;
                    }
                }
                End(_snapshot is null ? L($"Impossible de rejoindre : {why}.")
                    : connection is { IsFinal: true } ? L($"Session terminée ({why}).")
                    : L("Connexion avec l'hôte perdue."));
                break;
            case MessageType.Refused:
            case MessageType.Bye:
                End(r.ReadString());
                break;
            case MessageType.Welcome:
                OnWelcome(r);
                break;
            case MessageType.Reject:
                _diverged = true;
                _doc.Notify(L($"Synchronisation avec l'hôte ({r.ReadString()})..."), warning: true);
                break;
            case MessageType.Ack:
                _pending--;
                _opCount++;
                break;
            case MessageType.Edit:
                string author = r.ReadString();
                var message = r.ReadBytes();
                if (Diverged())
                    return;
                var edit = Run(() => ApplyMessage(message));
                _opCount++;
                _doc.Applied(edit, author, SessionAction.Edit);
                break;
            case MessageType.Undo:
            case MessageType.Redo:
                string by = r.ReadString();
                if (Diverged())
                    return;
                bool undo = type == MessageType.Undo;
                var done = Run(() => undo ? _doc.History.Undo() : _doc.History.Redo());
                _opCount++;
                if (done is not null)
                    _doc.Applied(done, by, undo ? SessionAction.Undo : SessionAction.Redo);
                break;
            case MessageType.Participants:
                int count = r.ReadInt32();
                Participants = [.. Enumerable.Range(0, count).Select(_ => r.ReadString())];
                break;
        }
    }

    // A change of the host while one of ours is not confirmed: ours will be refused, the session sent again.
    private bool Diverged()
    {
        if (_pending > 0)
            _diverged = true;
        return _diverged;
    }

    private List<(string Author, byte[] Message)> _welcomeLog = [];

    private void OnWelcome(BinaryReader r)
    {
        Name = r.ReadString();
        _rejoinKey = r.ReadString();
        if (_reconnecting)
        {
            _reconnecting = false;
            _doc.Notify(L("Reconnecté à l'hôte : rechargement de la session..."));
        }
        string project = r.ReadString();
        string fingerprint = r.ReadString();
        var zip = r.ReadBytes();
        int count = r.ReadInt32();
        _welcomeLog = [.. Enumerable.Range(0, count).Select(_ => (r.ReadString(), r.ReadBytes()))];
        _snapshot = new SessionSnapshot(project, zip, fingerprint);
        _loading = true;
        _pending = 0;
        Status = L("Chargement de la carte de l'hôte...");
        _doc.OpenSnapshot(_snapshot);
    }

    private void FinishLoading()
    {
        _loading = false;
        if (_doc.Fingerprint != _snapshot!.Fingerprint)
        {
            End(L($"Vos fichiers du jeu ne sont pas les mêmes que ceux de l'hôte ({_doc.Fingerprint} / {_snapshot.Fingerprint}) : même version de GTA V et mêmes mods nécessaires."));
            return;
        }
        try
        {
            foreach (var (_, message) in _welcomeLog)
                Run(() => ApplyMessage(message));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or EndOfStreamException)
        {
            End(L($"Impossible de reprendre les modifications de l'hôte : {ex.Message}"));
            return;
        }
        _opCount = _welcomeLog.Count;
        _welcomeLog = [];
        _diverged = false;
        _library = _doc.Context?.Library;
        Status = L("Connecté");
    }

    // Guest: asks the host to undo (or redo) the session's last change; false if it cannot now.
    public bool RequestUndo(bool undo = true)
    {
        if (Role != SessionRole.Guest || _host is null || IsLoading || _pending > 0 || _diverged)
            return false;
        _host.Send(undo ? MessageType.UndoRequest : MessageType.RedoRequest, Payload.Build(w => w.Write(_opCount)));
        return true;
    }

    // --- Changes made on this computer ---

    private T Run<T>(Func<T> action)
    {
        _applying = true;
        try
        {
            return action();
        }
        finally
        {
            _applying = false;
        }
    }

    private IEdit ApplyMessage(byte[] message)
    {
        var context = _doc.Context ?? throw new InvalidOperationException(L("La carte n'est pas chargée."));
        var edit = _doc.Codecs.Apply(message, context);
        _doc.History.Add(edit);
        return edit;
    }

    private void OnAdded(IEdit edit)
    {
        if (_applying || IsOver)
            return;
        if (Role == SessionRole.Guest && (IsLoading || _diverged))
            return; // Lost with the reload that follows.
        byte[] message;
        try
        {
            message = _doc.Codecs.Encode(edit, _doc.Context!.Library);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _doc.Notify(L($"Modification non partagée : {ex.Message}"), warning: true);
            return;
        }
        if (Role == SessionRole.Host)
        {
            _log.Add((Name, message));
            _redoLog.Clear();
            _opCount++;
            Broadcast(MessageType.Edit, Payload.Build(w =>
            {
                w.Write(Name);
                w.WriteBytes(message);
            }));
        }
        else
        {
            int seen = _opCount + _pending;
            _pending++;
            _host?.Send(MessageType.Edit, Payload.Build(w =>
            {
                w.Write(seen);
                w.WriteBytes(message);
            }));
        }
    }

    private void OnUndone(IEdit edit) => OnUndoRedo(undo: true);

    private void OnRedone(IEdit edit) => OnUndoRedo(undo: false);

    private void OnUndoRedo(bool undo)
    {
        if (_applying || IsOver || Role != SessionRole.Host)
            return;
        MoveLast(undo ? _log : _redoLog, undo ? _redoLog : _log);
        _opCount++;
        Broadcast(undo ? MessageType.Undo : MessageType.Redo, Payload.Build(w => w.Write(Name)));
    }

    private static void MoveLast(List<(string, byte[])> from, List<(string, byte[])> to)
    {
        if (from.Count == 0)
            return;
        to.Add(from[^1]);
        from.RemoveAt(from.Count - 1);
    }

    private static string CleanName(string name)
    {
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (name.Length > 24)
            name = name[..24];
        return name.Length == 0 ? L("Invité") : name;
    }

    // --- Ending ---

    public void End(string reason)
    {
        if (IsOver)
            return;
        EndReason = reason;
        Status = reason;
        _doc.History.Added -= OnAdded;
        _doc.History.Undone -= OnUndone;
        _doc.History.Redone -= OnRedone;
        _listener?.Stop();
        _listener = null;
        var bye = Payload.Build(w => w.Write(Role == SessionRole.Host ? L("L'hôte a terminé la session.") : L("Au revoir.")));
        Connection[] all;
        lock (_connections)
            all = [.. _connections];
        foreach (var connection in all.Append(_host).OfType<Connection>())
        {
            connection.Send(MessageType.Bye, bye);
            connection.CloseAfterSending();
        }
        if (_relay is { } relay)
        {
            // Time for the goodbyes to leave, then the relay closes the room.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await relay.CloseAsync();
            });
        }
    }

    // Cuts the link as a network failure would (tests).
    internal void CutLink()
    {
        if (Role == SessionRole.Guest)
            _host?.Dispose();
        else
            _relay?.Cut();
    }

    public void Dispose() => End(L("Session fermée."));
}
