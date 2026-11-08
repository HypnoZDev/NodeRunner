using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NodeRunner.Editor.Online;

// A byte stream to one other computer: direct (TCP) or through the relay.
internal abstract class Link : IDisposable
{
    public abstract string Address { get; }

    // Throws IOException (EndOfStreamException when the other side closed normally).
    public abstract ValueTask ReadExactlyAsync(Memory<byte> buffer);

    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data);

    // Everything was written: the other side reads the end after it.
    public abstract ValueTask EndAsync();

    public abstract void Dispose();
}

internal sealed class TcpLink : Link
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        Address = client.Client.RemoteEndPoint is System.Net.IPEndPoint remote
            ? (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString()
            : "?";
    }

    public override string Address { get; }

    public override async ValueTask ReadExactlyAsync(Memory<byte> buffer)
    {
        try
        {
            await _stream.ReadExactlyAsync(buffer);
        }
        catch (SocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            await _stream.WriteAsync(data);
        }
        catch (SocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public override async ValueTask EndAsync()
    {
        await _stream.FlushAsync();
        try
        {
            _client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public override void Dispose() => _client.Dispose();
}

// The relay closed the link; Code is the relay's close code (see relay/src/index.js).
internal sealed class RelayClosedException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;

    // The session is over for good: no point trying again.
    public bool IsFinal => Code is Relay.RoomGone or Relay.Ended or Relay.Full;
}

internal static class Relay
{
    public const string Protocol = "1";
    public const int HostLost = 4001;
    public const int Ended = 4002;
    public const int HostAway = 4003;
    public const int RoomGone = 4004;
    public const int Replaced = 4005;
    public const int ClosedByHost = 4006;
    public const int TooBig = 4009;
    public const int Full = 4029;

    // Bytes per relay message (the relay refuses more than 1 MiB).
    public const int Chunk = 128 * 1024;

    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(50);

    public static string Describe(WebSocketCloseStatus? status, string? description) => (int?)status switch
    {
        RoomGone => L("aucune session avec ce code (vérifiez le code, ou la session est terminée)"),
        Ended => L("l'hôte a terminé la session"),
        HostAway or HostLost => L("l'hôte s'est déconnecté"),
        Full => L("session complète"),
        Replaced => L("connexion remplacée"),
        ClosedByHost => L("fermée par l'hôte"),
        TooBig => L("message trop gros pour le relais"),
        _ => string.IsNullOrEmpty(description) ? L("connexion au relais fermée") : description,
    };

    public static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancel)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // pings sent as messages (answered by the relay itself)
        socket.Options.SetRequestHeader("User-Agent", "NodeRunner");
        try
        {
            await socket.ConnectAsync(uri, cancel);
            return socket;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            socket.Dispose();
            throw new IOException(L($"relais injoignable ({ex.Message})"), ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static Uri HostUri(string relay, string? code = null, string? token = null)
    {
        string query = code is null ? $"?v={Protocol}" : $"?v={Protocol}&code={Uri.EscapeDataString(code)}&token={Uri.EscapeDataString(token ?? "")}";
        return new Uri(relay.TrimEnd('/') + "/host" + query);
    }

    public static Uri JoinUri(string relay, string code) => new(relay.TrimEnd('/') + "/join/" + Uri.EscapeDataString(code) + $"?v={Protocol}");
}

// One WebSocket to the relay, with one sender at a time and a keep-alive. Messages are read by the owner.
internal sealed class RelaySocket : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private long _lastReceived = Environment.TickCount64;

    public RelaySocket(ClientWebSocket socket)
    {
        _socket = socket;
        _ = Task.Run(KeepAlive);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type = WebSocketMessageType.Binary)
    {
        await _sending.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(data, type, true, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            throw new IOException(L("connexion au relais perdue"), ex);
        }
        finally
        {
            _sending.Release();
        }
    }

    // One whole message (binary or text); throws RelayClosedException when the relay closes the link.
    public async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                throw new IOException(L("connexion au relais perdue"), ex);
            }
            Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var status = _socket.CloseStatus;
                throw new RelayClosedException((int?)status ?? 1006, Relay.Describe(status, _socket.CloseStatusDescription));
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > Connection.MaxFrame)
                throw new InvalidDataException(L("Message invalide."));
            if (result.EndOfMessage)
                return (result.MessageType, message.ToArray());
        }
    }

    private async Task KeepAlive()
    {
        var ping = Encoding.ASCII.GetBytes("ping");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(Relay.PingEvery, _stop.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastReceived) > Relay.DeadAfter.TotalMilliseconds)
                {
                    _socket.Abort(); // the reader fails: link lost
                    return;
                }
                await SendAsync(ping, WebSocketMessageType.Text).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    // Normal end: the relay knows it was on purpose.
    public async Task CloseAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _sending.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
            // The relay's answer (read by the owner's loop) before the socket is let go.
            while (_socket.State == WebSocketState.CloseSent && !timeout.IsCancellationRequested)
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Abort();
        _socket.Dispose();
    }
}

// A guest's link to the host through the relay: the socket's messages are the stream's bytes.
internal sealed class RelayGuestLink(RelaySocket socket) : Link
{
    private byte[] _current = [];
    private int _offset;

    public override string Address => L("relais");

    public override async ValueTask ReadExactlyAsync(Memory<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (_offset == _current.Length)
            {
                RelayClosedException closed;
                try
                {
                    var (type, data) = await socket.ReceiveAsync().ConfigureAwait(false);
                    if (type == WebSocketMessageType.Binary)
                    {
                        _current = data;
                        _offset = 0;
                    }
                    continue;
                }
                catch (RelayClosedException ex)
                {
                    closed = ex;
                }
                throw closed.Code is 1000 or Relay.ClosedByHost
                    ? new EndOfStreamException(closed.Message)
                    : closed;
            }
            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            buffer = buffer[count..];
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
    {
        for (int i = 0; i < data.Length; i += Relay.Chunk)
            await socket.SendAsync(data.Slice(i, Math.Min(Relay.Chunk, data.Length - i))).ConfigureAwait(false);
    }

    public override async ValueTask EndAsync() => await socket.CloseAsync().ConfigureAwait(false);

    public override void Dispose() => socket.Dispose();
}

// The host's end of the relay: one socket for every guest, each guest seen as its own link. When the socket drops,
// it connects again with the same code for a minute (the guests come back on their own).
internal sealed class RelayHost : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(55);

    private readonly string _relay;
    private readonly Action<Link> _arrived;
    private readonly Dictionary<uint, GuestLink> _guests = [];
    private RelaySocket? _socket;
    private string? _token;
    private volatile bool _stopped;

    private RelayHost(string relay, Action<Link> arrived)
    {
        _relay = relay;
        _arrived = arrived;
    }

    public string? Code { get; private set; }

    // False while connecting again after a drop.
    public bool IsOnline { get; private set; }

    // Set once the relay cannot be reached any more (the session is over).
    public string? Failure { get; private set; }

    public static async Task<RelayHost> OpenAsync(string relay, Action<Link> arrived, CancellationToken cancel)
    {
        var host = new RelayHost(relay, arrived);
        await host.ConnectAsync(cancel).ConfigureAwait(false);
        _ = Task.Run(host.Run);
        return host;
    }

    private async Task ConnectAsync(CancellationToken cancel)
    {
        var socket = new RelaySocket(await Relay.ConnectAsync(Relay.HostUri(_relay, Code, _token), cancel).ConfigureAwait(false));
        try
        {
            var (type, data) = await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(15), cancel).ConfigureAwait(false);
            using var json = JsonDocument.Parse(type == WebSocketMessageType.Text ? data : throw new InvalidDataException(L("Réponse du relais illisible.")));
            Code = json.RootElement.GetProperty("code").GetString();
            _token = json.RootElement.GetProperty("token").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or TimeoutException)
        {
            socket.Dispose();
            throw new IOException(L("Réponse du relais illisible."), ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _socket = socket;
        IsOnline = true;
    }

    private async Task Run()
    {
        while (!_stopped)
        {
            Exception? lost = null;
            try
            {
                await ReadLoop(_socket!).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                lost = ex;
            }
            IsOnline = false;
            _socket?.Dispose();
            CloseGuests(L("connexion de l'hôte au relais perdue"));
            if (_stopped)
                return;
            if (lost is RelayClosedException { Code: Relay.RoomGone or Relay.Replaced } final)
            {
                Failure = final.Message;
                return;
            }
            var deadline = DateTime.UtcNow + Grace;
            var wait = TimeSpan.FromSeconds(1);
            while (!_stopped)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await ConnectAsync(timeout.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    if (ex is RelayClosedException { Code: Relay.RoomGone } || DateTime.UtcNow + wait > deadline)
                    {
                        Failure = L("connexion au relais perdue");
                        return;
                    }
                }
                await Task.Delay(wait).ConfigureAwait(false);
                wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 8));
            }
        }
    }

    private async Task ReadLoop(RelaySocket socket)
    {
        while (!_stopped)
        {
            var (type, data) = await socket.ReceiveAsync().ConfigureAwait(false);
            if (type != WebSocketMessageType.Binary || data.Length < 5)
                continue;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data);
            byte kind = data[4];
            GuestLink? guest;
            lock (_guests)
                _guests.TryGetValue(id, out guest);
            switch (kind)
            {
                case 0:
                    guest?.Push(data[5..]);
                    break;
                case 1:
                    if (guest is null)
                    {
                        guest = new GuestLink(this, id);
                        lock (_guests)
                            _guests[id] = guest;
                        _arrived(guest);
                    }
                    break;
                case 2:
                    guest?.Finish(null);
                    lock (_guests)
                        _guests.Remove(id);
                    break;
            }
        }
    }

    private void CloseGuests(string reason)
    {
        GuestLink[] all;
        lock (_guests)
        {
            all = [.. _guests.Values];
            _guests.Clear();
        }
        foreach (var guest in all)
            guest.Finish(reason);
    }

    private async Task SendAsync(uint id, byte kind, ReadOnlyMemory<byte> data)
    {
        var socket = _socket;
        if (socket is null || !IsOnline)
            throw new IOException(L("connexion au relais perdue"));
        var message = new byte[5 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, id);
        message[4] = kind;
        data.CopyTo(message.AsMemory(5));
        await socket.SendAsync(message).ConfigureAwait(false);
    }

    internal void Cut() => _socket?.Dispose();

    public async Task CloseAsync()
    {
        _stopped = true;
        if (_socket is { } socket)
        {
            await socket.CloseAsync().ConfigureAwait(false);
            socket.Dispose();
        }
        CloseGuests(L("session terminée"));
    }

    public void Dispose()
    {
        _stopped = true;
        _socket?.Dispose();
        CloseGuests(L("session terminée"));
    }

    private sealed class GuestLink(RelayHost relay, uint id) : Link
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private byte[] _current = [];
        private int _offset;
        private string? _lost;
        private int _ended;

        public override string Address => L("relais");

        public void Push(byte[] data) => _incoming.Writer.TryWrite(data);

        // reason null: the guest left normally.
        public void Finish(string? reason)
        {
            _lost = reason;
            _incoming.Writer.TryComplete();
        }

        public override async ValueTask ReadExactlyAsync(Memory<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                if (_offset == _current.Length)
                {
                    if (!await _incoming.Reader.WaitToReadAsync().ConfigureAwait(false))
                        throw _lost is null ? new EndOfStreamException() : new IOException(_lost);
                    _incoming.Reader.TryRead(out var next);
                    _current = next ?? [];
                    _offset = 0;
                    continue;
                }
                int count = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                buffer = buffer[count..];
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
        {
            for (int i = 0; i < data.Length; i += Relay.Chunk)
                await relay.SendAsync(id, 0, data.Slice(i, Math.Min(Relay.Chunk, data.Length - i))).ConfigureAwait(false);
        }

        public override async ValueTask EndAsync()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                await relay.SendAsync(id, 2, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0 && relay.IsOnline)
                _ = relay.SendAsync(id, 2, ReadOnlyMemory<byte>.Empty).ContinueWith(_ => { }, TaskScheduler.Default);
            Finish(null);
            lock (relay._guests)
                relay._guests.Remove(id);
        }
    }
}
