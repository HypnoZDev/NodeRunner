using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Audio;

// A sound of the sounds.dat54.rel files: the sounds it plays, and for a sound that plays a wave, its bank (the hash of
// "folder/name" of the .awc file) and the wave's name hash.
public readonly record struct SoundNode(byte Type, uint[] Children, uint Bank, uint Wave);

// A wave of a sound bank (.awc): its track is the one whose id is the low 29 bits of Wave.
public readonly record struct WaveRef(uint Bank, uint Wave)
{
    public uint TrackId => Wave & 0x1FFFFFFF;
}

public interface ISoundTreeReader
{
    // The sounds of a sounds file (.dat54.rel), by name hash.
    IEnumerable<(uint Name, SoundNode Sound)> Read(byte[] data);
}

// Every sound of the game (later files overriding earlier ones by name), and where its sound banks are.
public sealed class SoundTree
{
    private readonly Dictionary<uint, SoundNode> _sounds;
    private readonly Dictionary<uint, string> _banks;

    private SoundTree(Dictionary<uint, SoundNode> sounds, Dictionary<uint, string> banks, IReadOnlyList<string> errors)
    {
        _sounds = sounds;
        _banks = banks;
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    public int Count => _sounds.Count;

    public bool Contains(uint sound) => _sounds.ContainsKey(sound);

    // Path of the .awc file of a bank, or null.
    public string? BankPath(uint bank) => _banks.GetValueOrDefault(bank);

    // The waves a sound can play (itself or through the sounds it plays), at most max, in order; More: some left out.
    public (IReadOnlyList<WaveRef> Waves, bool More) Waves(uint sound, int max)
    {
        var waves = new List<WaveRef>();
        var seen = new HashSet<uint>();
        var found = new HashSet<WaveRef>();
        bool more = false;
        var stack = new Stack<uint>();
        stack.Push(sound);
        while (stack.Count > 0)
        {
            uint current = stack.Pop();
            if (!seen.Add(current) || !_sounds.TryGetValue(current, out var node))
                continue;
            if (node.Wave != 0 && _banks.ContainsKey(node.Bank) && found.Add(new WaveRef(node.Bank, node.Wave)))
            {
                if (waves.Count == max)
                {
                    more = true;
                    break;
                }
                waves.Add(new WaveRef(node.Bank, node.Wave));
            }
            for (int i = node.Children.Length - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
        return (waves, more);
    }

    // The hash sounds use for the bank of this .awc file: "folder/name", the folder being the archive or the directory
    // holding it (RESIDENT.rpf\doors.awc -> "resident/doors").
    public static uint BankHash(string path)
    {
        string normalized = path.Replace('/', '\\');
        string name = Path.GetFileNameWithoutExtension(normalized);
        string folder = Path.GetFileName(Path.GetDirectoryName(normalized) ?? "");
        if (folder.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            folder = folder[..^4];
        return Joaat.Hash($"{folder}/{name}".ToLowerInvariant());
    }

    public static SoundTree Load(GameContent content, ISoundTreeReader reader)
    {
        var sounds = new Dictionary<uint, SoundNode>();
        var errors = new List<string>();
        foreach (var path in content.EnumerateAllVersions(".dat54.rel"))
        {
            try
            {
                if (content.ReadFile(path) is not { } data)
                    continue;
                foreach (var (name, sound) in reader.Read(data))
                    sounds[name] = sound;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }
        var banks = new Dictionary<uint, string>();
        foreach (var path in content.EnumerateAllVersions(".awc"))
            banks[BankHash(path)] = path;
        return new SoundTree(sounds, banks, errors);
    }
}
