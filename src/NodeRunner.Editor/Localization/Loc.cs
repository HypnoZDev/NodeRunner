using System.Runtime.CompilerServices;
using System.Text;

namespace NodeRunner.Editor.Localization;

public enum Language
{
    French,
    English,
}

// Interface texts are written in French in the code; L(...) gives them in the chosen language.
// L($"{n} objets") looks up "{0} objets". Missing whole text: each literal piece is looked up alone
// ("objets"), and a "##id" suffix is kept as it is. Nothing found: the French text.
public static class Loc
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    public static Language Current { get; set; } = Language.French;

    public static bool IsFrench => Current == Language.French;

    // Numbers in English texts: 0.5 rather than 0,5.
    public static IFormatProvider? Numbers => IsFrench ? null : System.Globalization.CultureInfo.InvariantCulture;

    public static string L(string french) => IsFrench || french.Length == 0 ? french : Template(french) ?? french;

    public static string L([InterpolatedStringHandlerArgument] ref LocHandler text) => text.ToStringAndClear();

    // True when the text (a key, with {0}, {1}... for the values) has an English version.
    public static bool HasEnglish(string key) => FindTemplate(key) is not null;

    internal static string? Template(string key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
            var template = FindTemplate(key);
            if (Cache.Count > 20000)
                Cache.Clear(); // Keys with values baked in (rare): keep the cache bounded.
            Cache[key] = template;
            return template;
        }
    }

    private static string? FindTemplate(string key)
    {
        if (English.Texts.TryGetValue(key, out var whole))
            return whole;
        int id = key.IndexOf("##", StringComparison.Ordinal);
        if (id >= 0)
        {
            if (id == 0)
                return key;
            return FindTemplate(key[..id]) is { } label ? label + key[id..] : null;
        }

        // Piece by piece, between the values: "{0}  Enregistrer" -> "{0}  Save".
        var result = new StringBuilder(key.Length + 8);
        int start = 0;
        while (start <= key.Length)
        {
            int hole = NextHole(key, start, out int holeEnd);
            int end = hole < 0 ? key.Length : hole;
            string piece = key[start..end];
            if (!TranslatePiece(piece, result))
                return null;
            if (hole < 0)
                break;
            result.Append(key, hole, holeEnd - hole);
            start = holeEnd;
        }
        return result.ToString();
    }

    // A literal piece: its words looked up without the spaces and punctuation around them.
    private static bool TranslatePiece(string piece, StringBuilder result)
    {
        if (!NeedsWords(piece))
        {
            result.Append(piece);
            return true;
        }
        int first = 0;
        while (first < piece.Length && !char.IsLetterOrDigit(piece[first]))
            first++;
        int last = piece.Length;
        while (last > first && !char.IsLetterOrDigit(piece[last - 1]) && piece[last - 1] is not ')' and not '.')
            last--;
        string core = piece[first..last];
        if (!English.Texts.TryGetValue(core, out var english))
        {
            // "Supprimer..." / "Supprimer :" -> try without the trailing dots or colon.
            string trimmed = core.TrimEnd('.', ' ');
            if (trimmed.Length == core.Length || !English.Texts.TryGetValue(trimmed, out english))
                return false;
            english += core[trimmed.Length..];
        }
        result.Append(piece, 0, first).Append(english).Append(piece, last, piece.Length - last);
        return true;
    }

    // Pieces without words (digits, symbols, codes like "GTA V" or "YMAP") need no translation.
    private static bool NeedsWords(string text)
    {
        for (int i = 1; i < text.Length; i++)
            if (char.IsLower(text[i]) && char.IsLower(text[i - 1]))
                return true;
        return false;
    }

    private static int NextHole(string key, int from, out int end)
    {
        for (int i = from; i < key.Length - 2; i++)
        {
            if (key[i] != '{' || !char.IsAsciiDigit(key[i + 1]))
                continue;
            int j = i + 1;
            while (j < key.Length && char.IsAsciiDigit(key[j]))
                j++;
            if (j < key.Length && key[j] == '}')
            {
                end = j + 1;
                return i;
            }
        }
        end = -1;
        return -1;
    }

    internal static string Fill(string template, List<string> values)
    {
        var result = new StringBuilder(template.Length + 32);
        int start = 0;
        while (true)
        {
            int hole = NextHole(template, start, out int end);
            if (hole < 0)
                break;
            result.Append(template, start, hole - start);
            int index = int.Parse(template.AsSpan(hole + 1, end - hole - 2));
            if (index < values.Count)
                result.Append(values[index]);
            start = end;
        }
        return result.Append(template, start, template.Length - start).ToString();
    }
}

[InterpolatedStringHandler]
public ref struct LocHandler
{
    private DefaultInterpolatedStringHandler _french;
    private readonly StringBuilder? _key;
    private readonly List<string>? _values;

    public LocHandler(int literalLength, int formattedCount)
    {
        _french = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
        if (!Loc.IsFrench)
        {
            _key = new StringBuilder(literalLength + formattedCount * 3);
            _values = new List<string>(formattedCount);
        }
    }

    public void AppendLiteral(string value)
    {
        _french.AppendLiteral(value);
        _key?.Append(value);
    }

    public void AppendFormatted<T>(T value, int alignment = 0, string? format = null)
    {
        if (_key is null)
        {
            _french.AppendFormatted(value, alignment, format);
            return;
        }
        var one = new DefaultInterpolatedStringHandler(0, 1, System.Globalization.CultureInfo.InvariantCulture);
        one.AppendFormatted(value, alignment, format);
        Add(one.ToStringAndClear());
    }

    public void AppendFormatted(string? value, int alignment = 0, string? format = null)
    {
        if (_key is null)
            _french.AppendFormatted(value, alignment, format);
        else
            Add(alignment == 0 ? value ?? "" : alignment > 0 ? (value ?? "").PadLeft(alignment) : (value ?? "").PadRight(-alignment));
    }

    private void Add(string text)
    {
        _french.AppendLiteral(text);
        _key!.Append('{').Append(_values!.Count).Append('}');
        _values.Add(text);
    }

    public string ToStringAndClear()
    {
        string french = _french.ToStringAndClear();
        if (_key is null)
            return french;
        return Loc.Template(_key.ToString()) is { } template ? Loc.Fill(template, _values!) : french;
    }
}
