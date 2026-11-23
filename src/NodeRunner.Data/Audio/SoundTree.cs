using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Audio;

// A sound of the sounds.dat54.rel files: the sounds it plays, and for a sound that plays a wave, its bank (the hash of
// "folder/name" of the .awc file) and the wave's name hash.
// A speech sound instead names a voice (the hash of its .awc file's name) and a context: it plays one of the tracks
// "<context>_01", "<context>_02"... of that file.
public readonly record struct SoundNode(byte Type, uint[] Children, uint Bank, uint Wave)
{
    public uint Voice { get; init; }
    public string? Context { get; init; }
}

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
    private readonly Dictionary<uint, List<string>> _voices;
    private readonly Func<string, IEnumerable<uint>>? _trackIds;
    private readonly Dictionary<string, HashSet<uint>> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WaveRef, string> _waveNames = new();

    private SoundTree(Dictionary<uint, SoundNode> sounds, Dictionary<uint, string> banks, Dictionary<uint, List<string>> voices,
        Func<string, IEnumerable<uint>>? trackIds, IReadOnlyList<string> errors)
    {
        _sounds = sounds;
        _banks = banks;
        _voices = voices;
        _trackIds = trackIds;
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    public int Count => _sounds.Count;

    public bool Contains(uint sound) => _sounds.ContainsKey(sound);

    // Path of the .awc file of a bank, or null.
    public string? BankPath(uint bank) => _banks.GetValueOrDefault(bank);

    // Name of a speech wave ("spooky_plea_01"), or null.
    public string? WaveName(WaveRef wave)
    {
        lock (_waveNames)
            return _waveNames.GetValueOrDefault(wave);
    }

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
            foreach (var speech in SpeechWaves(node))
            {
                if (!found.Add(speech))
                    continue;
                if (waves.Count == max)
                {
                    more = true;
                    break;
                }
                waves.Add(speech);
            }
            if (more)
                break;
            for (int i = node.Children.Length - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }
        return (waves, more);
    }

    // The variations of a speech sound present in its voice's files: <context>_01, _02... up to the first missing one.
    private IEnumerable<WaveRef> SpeechWaves(SoundNode node)
    {
        if (node.Context is not { Length: > 0 } context || _trackIds is null || !_voices.TryGetValue(node.Voice, out var paths))
            yield break;
        foreach (var path in paths)
        {
            var tracks = Tracks(path);
            for (int variation = 1; variation < 100; variation++)
            {
                string name = $"{context}_{variation:00}".ToLowerInvariant();
                uint wave = Joaat.Hash(name);
                if (!tracks.Contains(wave & 0x1FFFFFFF))
                    break;
                var reference = new WaveRef(BankHash(path), wave);
                lock (_waveNames)
                    _waveNames[reference] = name;
                yield return reference;
            }
        }
    }

    private HashSet<uint> Tracks(string path)
    {
        lock (_tracks)
            if (_tracks.TryGetValue(path, out var known))
                return known;
        HashSet<uint> read;
        try
        {
            read = [.. _trackIds!(path)];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            read = [];
        }
        lock (_tracks)
            _tracks[path] = read;
        return read;
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

    // trackIds: the track ids of a sound bank (.awc), to find the variations of the speech sounds; null leaves them out.
    public static SoundTree Load(GameContent content, ISoundTreeReader reader, Func<string, IEnumerable<uint>>? trackIds = null)
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
        var voiceNames = sounds.Values.Where(s => s.Context is not null).Select(s => s.Voice).ToHashSet();
        var banks = new Dictionary<uint, string>();
        var voices = new Dictionary<uint, List<string>>();
        foreach (var path in content.EnumerateAllVersions(".awc"))
        {
            banks[BankHash(path)] = path;
            uint voice = Joaat.Hash(Path.GetFileNameWithoutExtension(path.Replace('/', '\\')).ToLowerInvariant());
            if (voiceNames.Contains(voice))
            {
                if (!voices.TryGetValue(voice, out var list))
                    voices[voice] = list = [];
                list.Add(path);
            }
        }
        return new SoundTree(sounds, banks, voices, trackIds, errors);
    }
}
