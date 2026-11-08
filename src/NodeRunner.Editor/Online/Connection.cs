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
}

// One link to another computer: frames of [length][type][data], read and written on background tasks. Messages read
// go to received (from a background thread); closed is called once.
internal sealed class Connection : IDisposable
{
    public const int MaxFrame = 1 << 30;

    private readonly Link _link;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Connection, MessageType, byte[]> _received;
    private readonly Action<Connection, string> _closed;
    private int _isClosed;

    public Connection(Link link, int id, Action<Connection, MessageType, byte[]> received, Action<Connection, string> closed)
    {
        _link = link;
        Id = id;
        Address = link.Address;
        _received = received;
        _closed = closed;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public int Id { get; }
    public string Address { get; }
    public string Name { get; set; } = "";
    public bool Accepted { get; set; }

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
        _outgoing.Writer.TryWrite(frame);
    }

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
                if (length < 1 || length > MaxFrame)
                    throw new InvalidDataException(L("Message invalide."));
                var body = new byte[length];
                await _link.ReadExactlyAsync(body);
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

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync())
                await _link.WriteAsync(frame);
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
