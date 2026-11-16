using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;

namespace NodeRunner.Editor.Online;

internal enum MessageType : byte
{
    Closed = 0, // Not sent: the connection ended.
    Hello = 1,
    Refused = 2,
    Welcome = 3,
    Edit = 4,
    Undo = 5,
    Redo = 6,
    Ack = 7,
    Reject = 8,
    Participants = 9,
    Bye = 10,
    UndoRequest = 11,
    RedoRequest = 12,
    Presence = 13,
    Camera = 14,
}

// One link to another computer: frames of [length][type][data], read and written on background tasks. Messages read
// go to received (from a background thread); closed is called once.
internal sealed class Connection : IDisposable
{
    public const int MaxFrame = 1 << 30;

    // Someone the host has not accepted yet only says hello: a short message.
    public const int MaxFrameBeforeAccepted = 64 * 1024;

    private const int ReadStep = 1 << 20;

    private readonly Link _link;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Connection, MessageType, byte[]> _received;
    private readonly Action<Connection, string> _closed;
    private readonly bool _unknown;
    private volatile bool _accepted;
    private volatile bool _heard;
    private int _isClosed;

    // helloWithin: the other side is not known yet (host's end): it must say hello within this time, with short
    // messages until it is accepted. Null for the link to the host.
    public Connection(Link link, int id, Action<Connection, MessageType, byte[]> received, Action<Connection, string> closed,
        TimeSpan? helloWithin = null)
    {
        _link = link;
        Id = id;
        Address = link.Address;
        _received = received;
        _closed = closed;
        _unknown = helloWithin is not null;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
        if (helloWithin is { } wait)
        {
            _ = Task.Delay(wait).ContinueWith(_ =>
            {
                if (!_heard)
                    Close(L("connexion fermée"));
            }, TaskScheduler.Default);
        }
    }

    public int Id { get; }
    public string Address { get; }
    public string Name { get; set; } = "";
    public bool Accepted { get => _accepted; set => _accepted = value; }

    // Given by the host when accepted: the guest comes back with it after a drop.
    public string RejoinKey { get; set; } = "";

    // Set when the link was closed by the relay: its close code.
    public int? RelayCode { get; private set; }

    // The other side is gone for good (the relay knows the session is over).
    public bool IsFinal { get; private set; }

    public void Send(MessageType type, byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length + 1);
        frame[4] = (byte)type;
        payload.CopyTo(frame, 5);
        // The other side no longer takes anything (frozen, very slow link): closed rather than piling up memory; it can
        // come back and is sent the project again.
        if (Interlocked.Add(ref _queued, frame.Length) > MaxQueued)
        {
            Close(L("l'autre ordinateur ne reçoit plus les messages"));
            return;
        }
        if (!_outgoing.Writer.TryWrite(frame))
            Interlocked.Add(ref _queued, -frame.Length);
    }

    // A whole project plus the edits made meanwhile.
    private const long MaxQueued = MaxFrame + (256L << 20);
    private long _queued;

    // Sends what is queued, then closes.
    public void CloseAfterSending() => _outgoing.Writer.TryComplete();

    private async Task ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                await _link.ReadExactlyAsync(header);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 1 || length > (_unknown && !_accepted ? MaxFrameBeforeAccepted : MaxFrame))
                    throw new InvalidDataException(L("Message invalide."));
                var body = await ReadBody(length);
                _heard = true;
                _received(this, (MessageType)body[0], body[1..]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            if (ex is RelayClosedException relay)
            {
                RelayCode = relay.Code;
                IsFinal = relay.IsFinal;
            }
            Close(ex is EndOfStreamException ? L("connexion fermée") : ex.Message);
        }
    }

    // Memory is taken as the bytes arrive, not on the announced size alone.
    private async Task<byte[]> ReadBody(int length)
    {
        var body = new byte[Math.Min(length, ReadStep)];
        int read = 0;
        while (true)
        {
            await _link.ReadExactlyAsync(body.AsMemory(read));
            read = body.Length;
            if (read == length)
                return body;
            Array.Resize(ref body, (int)Math.Min(length, Math.Max((long)read * 2, read + (long)ReadStep)));
        }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync())
            {
                await _link.WriteAsync(frame);
                Interlocked.Add(ref _queued, -frame.Length);
            }
            await _link.EndAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(ex.Message);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0)
            return;
        _outgoing.Writer.TryComplete();
        _link.Dispose();
        _closed(this, reason);
    }

    public void Dispose() => Close(L("fermée"));
}

// Building and reading message data.
internal static class Payload
{
    public static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            write(writer);
        return stream.ToArray();
    }

    public static BinaryReader Reader(byte[] data) => new(new MemoryStream(data, writable: false), Encoding.UTF8);

    public static void WriteBytes(this BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public static byte[] ReadBytes(this BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > Connection.MaxFrame)
            throw new InvalidDataException(L("Invalid size."));
        return reader.ReadBytes(length);
    }
}
