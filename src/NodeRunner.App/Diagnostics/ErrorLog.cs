namespace NodeRunner.App.Diagnostics;

// Errors met while the program runs, from any thread, shown in the "Errors" window and in the report users send.
internal static class ErrorLog
{
    public sealed record Entry(DateTime Time, string Where, string Message, string? Details);

    private const int MaxEntries = 300;
    private static readonly object Lock = new();
    private static readonly List<Entry> Items = [];

    public static int Count
    {
        get { lock (Lock) return Items.Count; }
    }

    // Entries the user has not seen in the window yet.
    public static int Unseen { get; private set; }

    public static void Add(string where, string message, Exception? exception = null)
    {
        lock (Lock)
        {
            // The same error repeated (one model read every frame...) is kept once.
            if (Items.Any(e => e.Where == where && e.Message == message))
                return;
            if (Items.Count >= MaxEntries)
                Items.RemoveAt(0);
            Items.Add(new Entry(DateTime.Now, where, message, exception?.ToString()));
            Unseen++;
        }
    }

    public static void Add(string where, Exception exception) => Add(where, exception.Message, exception);

    public static IReadOnlyList<Entry> Snapshot()
    {
        lock (Lock) return [.. Items];
    }

    public static void MarkSeen() => Unseen = 0;

    public static void Clear()
    {
        lock (Lock)
        {
            Items.Clear();
            Unseen = 0;
        }
    }
}
