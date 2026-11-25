using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodeRunner.Editor;

// A player seen by the live link: where they stand and where the game camera looks (GTA V world space, degrees for
// the game's own rotations).
public sealed record GameLinkPlayer(int Id, string Name, Vector3 Position, float Heading, Vector3 CameraPosition, Vector3 CameraRotation)
{
    // Editor camera angles (radians) from the game camera: the game's yaw 0 looks north and turns left.
    public float CameraYaw => (CameraRotation.Z + 90f) * MathF.PI / 180f;

    public float CameraPitch => CameraRotation.X * MathF.PI / 180f;
}

// The editor side of the live link: a tiny HTTP endpoint on the loopback only, polled by the noderunner-link resource
// of the local FiveM server. Each poll brings the players' state and takes the waiting commands back.
public sealed class GameLinkHost : IDisposable
{
    public const int DefaultPort = 47160;
    public const string Route = "/link";
    private const int MaxRequest = 1 << 20;
    private static readonly TimeSpan ConnectedFor = TimeSpan.FromSeconds(2.5);

    private readonly ConcurrentQueue<JsonObject> _commands = new();
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private IReadOnlyList<GameLinkPlayer> _players = [];
    private DateTime _lastPoll = DateTime.MinValue;

    public int Port { get; private set; }

    public string? Error { get; private set; }

    public bool IsRunning => _listener is not null;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
                return DateTime.UtcNow - _lastPoll < ConnectedFor;
        }
    }

    public IReadOnlyList<GameLinkPlayer> Players
    {
        get
        {
            lock (_lock)
                return IsConnectedUnlocked() ? _players : [];
        }
    }

    private bool IsConnectedUnlocked() => DateTime.UtcNow - _lastPoll < ConnectedFor;

    public bool Start(int port = DefaultPort)
    {
        if (_listener is not null)
            return true;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Error = null;
            _stop = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(listener, _stop.Token));
            return true;
        }
        catch (SocketException e)
        {
            Error = e.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    // Puts the player (on foot or in their vehicle) on the ground under a point, facing a heading in degrees.
    public void Teleport(int player, Vector3 position, float heading) =>
        _commands.Enqueue(new JsonObject
        {
            ["type"] = "teleport",
            ["player"] = player,
            ["x"] = position.X,
            ["y"] = position.Y,
            ["z"] = position.Z,
            ["h"] = heading,
        });

    // The game's heading (degrees, 0 = north, turning left) from an editor yaw (radians, 0 = east).
    public static float HeadingFromYaw(float yaw)
    {
        float degrees = yaw * 180f / MathF.PI - 90f;
        degrees %= 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => Serve(client), stop);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 3000;
                var stream = client.GetStream();
                var (status, body) = await Answer(stream);
                byte[] content = Encoding.UTF8.GetBytes(body);
                string head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(content);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The resource polls again in a moment.
            }
        }
    }

    private async Task<(string Status, string Body)> Answer(NetworkStream stream)
    {
        var request = await ReadRequest(stream);
        if (request is null)
            return ("400 Bad Request", "{}");
        var (line, headers, body) = request.Value;
        // Browsers always send Origin: a web page must never reach the link.
        if (headers.ContainsKey("origin"))
            return ("403 Forbidden", "{}");
        if (!line.StartsWith("POST " + Route + " ", StringComparison.Ordinal))
            return ("404 Not Found", "{}");

        var players = ReadPlayers(body);
        lock (_lock)
        {
            _players = players;
            _lastPoll = DateTime.UtcNow;
        }
        var commands = new JsonArray();
        while (commands.Count < 64 && _commands.TryDequeue(out var command))
            commands.Add(command);
        return ("200 OK", new JsonObject { ["v"] = GameLinkResource.Version, ["commands"] = commands }.ToJsonString());
    }

    private static async Task<(string Line, Dictionary<string, string> Headers, string Body)?> ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var data = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0 || data.Length + read > MaxRequest)
                return null;
            data.Write(buffer, 0, read);
            headerEnd = IndexOfBlankLine(data.GetBuffer(), (int)data.Length);
        }
        string head = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
        string[] lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string header in lines.Skip(1))
        {
            int colon = header.IndexOf(':');
            if (colon > 0)
                headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out string? text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
        if (length < 0 || length > MaxRequest)
            return null;
        int bodyStart = headerEnd + 4;
        while (data.Length - bodyStart < length)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0)
                return null;
            data.Write(buffer, 0, read);
        }
        string body = Encoding.UTF8.GetString(data.GetBuffer(), bodyStart, length);
        return (lines[0], headers, body);
    }

    private static int IndexOfBlankLine(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }

    // {"players": [{"id", "name", "x", "y", "z", "h", "cx", "cy", "cz", "rx", "ry", "rz"}, ...]}; anything odd is skipped.
    public static IReadOnlyList<GameLinkPlayer> ReadPlayers(string body)
    {
        var players = new List<GameLinkPlayer>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("players", out var list) || list.ValueKind != JsonValueKind.Array)
                return players;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                players.Add(new GameLinkPlayer(
                    (int)Number(item, "id"),
                    item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "",
                    new Vector3(Number(item, "x"), Number(item, "y"), Number(item, "z")),
                    Number(item, "h"),
                    new Vector3(Number(item, "cx"), Number(item, "cy"), Number(item, "cz")),
                    new Vector3(Number(item, "rx"), Number(item, "ry"), Number(item, "rz"))));
            }
        }
        catch (JsonException)
        {
        }
        return players;
    }

    private static float Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number) ? (float)number : 0f;
}
