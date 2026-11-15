using System.Text;
using System.Text.RegularExpressions;

namespace NodeRunner.Editor;

// Adds or removes a pack in the game's list of packs (update.rpf\common\data\dlclist.xml), keeping the rest of the file
// as it is (layout, byte order mark).
public static partial class DlcList
{
    public const string PathInUpdate = @"common\data\dlclist.xml";

    public static IReadOnlyList<string> Entries(byte[] file) =>
        ItemPattern().Matches(Decode(file).Text).Select(m => m.Groups[1].Value.Trim()).ToList();

    public static bool Contains(byte[] file, string name) => Find(Decode(file).Text, name) is not null;

    // null if the pack is already listed.
    public static byte[]? Add(byte[] file, string name)
    {
        var (text, bom) = Decode(file);
        if (Find(text, name) is not null)
            return null;
        int end = text.LastIndexOf("</Paths>", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            throw new InvalidDataException("dlclist.xml : liste <Paths> introuvable.");
        string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        // Placed after the last item, with its indentation.
        int lastItem = text.LastIndexOf("</Item>", end, StringComparison.OrdinalIgnoreCase);
        string line = $"<Item>{SoloPack.ListEntry(name)}</Item>";
        if (lastItem >= 0)
        {
            int lineStart = text.LastIndexOf('\n', lastItem) + 1;
            string indent = new(text[lineStart..lastItem].TakeWhile(c => c is ' ' or '\t').ToArray());
            int insert = lastItem + "</Item>".Length;
            text = text[..insert] + newLine + indent + line + text[insert..];
        }
        else
            text = text[..end] + "\t" + line + newLine + text[end..];
        return Encode(text, bom);
    }

    // null if the pack is not listed.
    public static byte[]? Remove(byte[] file, string name)
    {
        var (text, bom) = Decode(file);
        if (Find(text, name) is not { } match)
            return null;
        int start = match.Index;
        int end = match.Index + match.Length;
        // The whole line goes when the item is alone on it.
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (string.IsNullOrWhiteSpace(text[lineStart..start]))
        {
            int lineEnd = text.IndexOf('\n', end);
            if (lineEnd >= 0 && string.IsNullOrWhiteSpace(text[end..lineEnd]))
            {
                start = lineStart;
                end = lineEnd + 1;
            }
        }
        return Encode(text[..start] + text[end..], bom);
    }

    private static Match? Find(string text, string name) =>
        ItemPattern().Matches(text).FirstOrDefault(m =>
            m.Groups[1].Value.Trim().TrimEnd('/').Equals(SoloPack.ListEntry(name).TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private static (string Text, bool Bom) Decode(byte[] file)
    {
        bool bom = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF;
        return (Encoding.UTF8.GetString(file, bom ? 3 : 0, file.Length - (bom ? 3 : 0)), bom);
    }

    private static byte[] Encode(string text, bool bom) => new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    [GeneratedRegex(@"<Item>([^<]*)</Item>", RegexOptions.IgnoreCase)]
    private static partial Regex ItemPattern();
}
