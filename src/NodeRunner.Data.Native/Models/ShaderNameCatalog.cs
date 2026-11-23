using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Models;

// Names of the shaders, of their presets (.sps) and of their parameters, read from the player's own game files: the
// models only keep hashes, and the XML exports need the names (Sollumz finds its shaders by them).
public sealed class ShaderNameCatalog
{
    private readonly Dictionary<uint, string> _names = new();

    public int Count => _names.Count;

    public string? this[uint hash] => _names.GetValueOrDefault(hash);

    public static ShaderNameCatalog Read(IGameFileSystem files)
    {
        var catalog = new ShaderNameCatalog();
        foreach (string path in files.EnumerateFiles(".sps"))
        {
            string file = Path.GetFileName(path).ToLowerInvariant();
            catalog.Add(file);
            catalog.Add(Path.GetFileNameWithoutExtension(file));
        }
        foreach (string path in files.EnumerateFiles(".fxc"))
        {
            // One set of compiled shaders is enough: the others hold the same names.
            if (!path.Contains(@"\win32_40_final\", StringComparison.OrdinalIgnoreCase))
                continue;
            catalog.Add(Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
            if (files.ReadFile(path) is { } data)
                catalog.AddStrings(data);
        }
        return catalog;
    }

    public void Add(string name) => _names.TryAdd(Joaat.Hash(name), name);

    // The names stored in a compiled shader: each one a length byte, then the characters and a terminating zero.
    // Every identifier found that way is kept, parameter names among them.
    private void AddStrings(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 2 < data.Length; i++)
        {
            int length = data[i];
            if (length < 3 || length > 64 || i + length >= data.Length || data[i + length] != 0)
                continue;
            var text = data.Slice(i + 1, length - 1);
            if (!IsIdentifier(text))
                continue;
            Add(System.Text.Encoding.ASCII.GetString(text));
            i += length;
        }
    }

    private static bool IsIdentifier(ReadOnlySpan<byte> text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter((char)text[0]) || text[0] == '_'))
            return false;
        foreach (byte b in text)
        {
            if (!(char.IsAsciiLetterOrDigit((char)b) || b == '_'))
                return false;
        }
        return true;
    }
}
