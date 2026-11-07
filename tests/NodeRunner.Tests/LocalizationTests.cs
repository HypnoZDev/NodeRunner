using System.Text;
using NodeRunner.Editor.Localization;

namespace NodeRunner.Tests;

public class LocalizationTests
{
    [Fact]
    public void EveryInterfaceTextHasAnEnglishVersion()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, key) in SourceKeys())
            if (!Loc.HasEnglish(key))
                missing.Add($"{key}    ({Path.GetFileName(file)})");
        string list = string.Join("\n", missing);
        string? dump = Environment.GetEnvironmentVariable("NODERUNNER_MISSING_TEXTS");
        if (!string.IsNullOrEmpty(dump))
            File.WriteAllText(dump, list, Encoding.UTF8);
        Assert.True(missing.Count == 0, $"{missing.Count} texts without English:\n{list}");
    }

    [Fact]
    public void InterpolatedTextsKeepTheirValues()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = Language.English;
            int count = 3;
            Assert.Equal("Every 3 min", Loc.L($"Toutes les {count} min"));
            Assert.Equal("Save##save_1", Loc.L($"Enregistrer##save_{1}"));
            Assert.Equal("xyz_unknown 3", Loc.L($"xyz_unknown {count}"));
            Loc.Current = Language.French;
            Assert.Equal("Toutes les 3 min", Loc.L($"Toutes les {count} min"));
        }
        finally
        {
            Loc.Current = previous;
        }
    }

    // --- L("...") and L($"...") calls in the sources ---

    private static IEnumerable<(string File, string Key)> SourceKeys()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NodeRunner.sln")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("NodeRunner.sln not found");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains("Localization"))
                continue;
            var keys = new List<string>();
            var scanner = new Scanner(File.ReadAllText(file), keys);
            scanner.Code(stopAtBrace: false);
            foreach (string key in keys)
                yield return (file, key);
        }
    }

    private sealed class Scanner(string text, List<string> keys)
    {
        private int _i;

        // Walks code until the end (or the '}' closing an interpolation hole), collecting L(...) keys.
        public void Code(bool stopAtBrace)
        {
            int depth = 0;
            while (_i < text.Length)
            {
                char c = text[_i];
                if (c == '/' && Peek(1) == '/')
                {
                    while (_i < text.Length && text[_i] != '\n')
                        _i++;
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    int end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? text.Length : end + 2;
                }
                else if (c == '\'')
                {
                    _i++;
                    while (_i < text.Length && text[_i] != '\'')
                        _i += text[_i] == '\\' ? 2 : 1;
                    _i++;
                }
                else if (IsStringStart())
                {
                    String(out _);
                }
                else if (stopAtBrace && depth == 0 && (c == '}' || c == ':' || c == ','))
                {
                    if (c != '}')
                        while (_i < text.Length && text[_i] != '}')
                            _i++;
                    return;
                }
                else if (c is '(' or '[' or '{')
                {
                    depth++;
                    _i++;
                }
                else if (c is ')' or ']' or '}')
                {
                    depth--;
                    _i++;
                }
                else if (c == 'L' && Peek(1) == '(' && (_i == 0 || !(char.IsLetterOrDigit(text[_i - 1]) || text[_i - 1] is '_' or '.')))
                {
                    _i += 2;
                    depth++; // Its ')' is read as code.
                    while (char.IsWhiteSpace(text[_i]))
                        _i++;
                    if (!IsStringStart())
                        continue;
                    String(out string? key);
                    while (char.IsWhiteSpace(text[_i]))
                        _i++;
                    if (key is not null && text[_i] == ')' && key.Length > 0)
                        keys.Add(key);
                }
                else
                {
                    _i++;
                }
            }
        }

        private char Peek(int offset) => _i + offset < text.Length ? text[_i + offset] : '\0';

        private bool IsStringStart()
        {
            int j = _i;
            while (j < text.Length && text[j] is '$' or '@')
                j++;
            return j < text.Length && text[j] == '"' && (j == _i || text[_i] is '$' or '@');
        }

        // Reads a string literal; key = its text with {0}, {1}... in place of the values (null for verbatim / raw).
        private void String(out string? key)
        {
            bool interpolated = false, verbatim = false;
            while (text[_i] is '$' or '@')
            {
                interpolated |= text[_i] == '$';
                verbatim |= text[_i] == '@';
                _i++;
            }
            if (Peek(1) == '"' && Peek(2) == '"')
            {
                int quotes = 0;
                while (text[_i] == '"')
                {
                    quotes++;
                    _i++;
                }
                int end = text.IndexOf(new string('"', quotes), _i, StringComparison.Ordinal);
                _i = end < 0 ? text.Length : end + quotes;
                key = null;
                return;
            }
            _i++;
            var result = new StringBuilder();
            int holes = 0;
            while (_i < text.Length)
            {
                char c = text[_i];
                if (c == '"')
                {
                    if (verbatim && Peek(1) == '"')
                    {
                        result.Append('"');
                        _i += 2;
                        continue;
                    }
                    _i++;
                    break;
                }
                if (c == '\\' && !verbatim)
                {
                    char e = Peek(1);
                    result.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => e });
                    _i += 2;
                    continue;
                }
                if (interpolated && c == '{')
                {
                    if (Peek(1) == '{')
                    {
                        result.Append('{');
                        _i += 2;
                        continue;
                    }
                    _i++;
                    Code(stopAtBrace: true);
                    _i++;
                    result.Append('{').Append(holes++).Append('}');
                    continue;
                }
                if (interpolated && c == '}' && Peek(1) == '}')
                {
                    result.Append('}');
                    _i += 2;
                    continue;
                }
                result.Append(c);
                _i++;
            }
            key = result.ToString();
        }
    }
}
