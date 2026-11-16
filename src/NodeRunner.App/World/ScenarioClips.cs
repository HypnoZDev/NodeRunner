using System.Diagnostics;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.App.World;

// The clips the peds and animals of the scenario points play in a loop, read in the background (the tables at first
// use, then each clip dictionary when a point needs it) and kept for the next points.
internal sealed class ScenarioClips
{
    private const int MaxDictionaries = 48;
    private static readonly uint BaseClip = Joaat.Hash("base");
    // Humans on a point whose type has no animation of its own (standing guards...) just stand there.
    private static readonly uint StandingType = Joaat.Hash("WORLD_HUMAN_HANG_OUT_STREET");

    private sealed record Tables(ScenarioAnimations Animations, Dictionary<uint, string> Files);

    private sealed class Entry(Task<IReadOnlyDictionary<uint, AnimationClip>> clips)
    {
        public readonly Task<IReadOnlyDictionary<uint, AnimationClip>> Clips = clips;
        public long LastUsed;
    }

    private GameContent? _for;
    private Task<Tables>? _tables;
    private readonly Dictionary<uint, Entry> _dictionaries = new();
    private readonly Dictionary<(uint Type, bool Male, bool Animal, int Seed), (uint Dictionary, uint Clip)?> _choices = new();
    private long _tick;

    private static readonly long Start = Stopwatch.GetTimestamp();

    // Seconds since the start, the clock every animation runs on.
    public static float Now => (float)Stopwatch.GetElapsedTime(Start).TotalSeconds;

    // The clip of a ped (by model name) on a point of this type; seed picks among the type's variants. Null while it
    // is being read, or when there is none.
    public AnimationClip? Get(WorldLoader world, uint typeHash, string pedName, int seed)
    {
        if (world.Content is not { } content)
            return null;
        if (!ReferenceEquals(_for, content))
        {
            _for = content;
            _dictionaries.Clear();
            _choices.Clear();
            _tables = Task.Run(() => LoadTables(content));
        }
        if (_tables is not { IsCompletedSuccessfully: true } loaded)
            return null;
        var (animations, files) = loaded.Result;

        bool animal = pedName.StartsWith("a_c_", StringComparison.OrdinalIgnoreCase);
        bool male = !pedName.Contains("_f_", StringComparison.OrdinalIgnoreCase);
        // Asked again at each frame until the clip is read: the choice is made once.
        var key = (typeHash, male, animal, seed);
        if (!_choices.TryGetValue(key, out var choice))
            _choices[key] = choice = animations.Choose(typeHash, male, seed) ?? (animal ? null : animations.Choose(StandingType, male, seed));
        if (choice is not { } chosen || !files.TryGetValue(chosen.Dictionary, out var path))
            return null;
        if (_choices.Count > 4096)
            _choices.Clear();

        if (!_dictionaries.TryGetValue(chosen.Dictionary, out var entry))
        {
            if (_dictionaries.Count >= MaxDictionaries)
            {
                var oldest = _dictionaries.Where(p => p.Value.Clips.IsCompleted).MinBy(p => p.Value.LastUsed);
                if (oldest.Value is not null)
                    _dictionaries.Remove(oldest.Key);
            }
            _dictionaries[chosen.Dictionary] = entry = new Entry(Task.Run(() => ReadClips(content, path)));
        }
        entry.LastUsed = ++_tick;
        if (entry.Clips is not { IsCompletedSuccessfully: true } read)
            return null;
        var clips = read.Result;
        // A clip set naming another clip than the dictionary holds: its "base" one, or the first.
        var clip = clips.GetValueOrDefault(chosen.Clip) ?? clips.GetValueOrDefault(BaseClip)
            ?? clips.Values.OrderBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
        if (clip is not null)
            _files.AddOrUpdate(clip, Path.GetFileNameWithoutExtension(path));
        return clip;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnimationClip, string> _files = new();

    // Name of the .ycd a clip given by Get comes from, and of the clip ("amb@world_human_smoking@male@male_a@base / base").
    public string Describe(AnimationClip clip)
    {
        string name = clip.Name is { } full ? Path.GetFileNameWithoutExtension(full.Replace('\\', '/').Split('/')[^1]) : $"{clip.NameHash:X8}";
        return _files.TryGetValue(clip, out var file) ? $"{file} / {name}" : name;
    }

    private static Tables LoadTables(GameContent content)
    {
        var animations = ScenarioAnimations.Load(content, new ClipSetReader());
        var files = new Dictionary<uint, string>();
        foreach (var path in content.EnumerateFiles(".ycd"))
            files.TryAdd(Joaat.Hash(Path.GetFileNameWithoutExtension(path)), path);
        return new Tables(animations, files);
    }

    private static IReadOnlyDictionary<uint, AnimationClip> ReadClips(GameContent content, string path)
    {
        try
        {
            if (content.ReadFile(path) is { } data && content.GetResourceHeader(path) is { } header)
                return ClipDictionaryReader.Read(data, header);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Left in its default pose.
        }
        return new Dictionary<uint, AnimationClip>();
    }

    // Matrices of the bones of a model playing a clip at a time, phase shifting it (seconds) so that neighbours
    // do not move together. False when the model does not bend.
    public static bool Pose(ModelData? data, AnimationClip? clip, float phase, ref Matrix4x4[]? pose)
    {
        if (clip is null || data?.Skeleton is not { } skeleton)
            return false;
        if (pose is null || pose.Length != skeleton.Count)
            pose = new Matrix4x4[skeleton.Count];
        SkeletonAnimator.Pose(skeleton, clip, Now + phase, pose);
        return true;
    }
}
