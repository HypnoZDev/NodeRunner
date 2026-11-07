namespace NodeRunner.Editor.Localization;

// English versions of the interface texts, keyed by the French text written in the code ({0}, {1}... = values).
// One file per area of the interface.
public static partial class English
{
    public static readonly IReadOnlyDictionary<string, string> Texts = Build();

    private static Dictionary<string, string> Build()
    {
        var texts = new Dictionary<string, string>(4096, StringComparer.Ordinal);
        foreach (var list in new[] { Common, Main, Settings })
            foreach (var (french, english) in list)
                texts[french] = english;
        return texts;
    }
}
