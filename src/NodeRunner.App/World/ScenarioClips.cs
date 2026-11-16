using System.Diagnostics;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Scenarios;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A clip a ped plays, and the object it holds (model name, null for none) in its right hand unless LeftHand.
internal sealed record ScenarioClip(AnimationClip Clip, string? Prop, bool LeftHand);

// The clips the peds and animals of the scenario points play in a loop, read in the background (the tables at first
// use, then each clip dictionary when a point needs it) and kept for the next points.
internal sealed class ScenarioClips : IDisposable
{
    private const int MaxDictionaries = 48;
    private static readonly uint BaseClip = Joaat.Hash("base");
    // Humans on a point whose type has no animation of its own (standing guards...) just stand there.
    private static readonly uint StandingType = Joaat.Hash("WORLD_HUMAN_HANG_OUT_STREET");

    private sealed record Tables(ScenarioAnimations Animations, Dictionary<uint, string> Files);

    private const int MaxProps = 24;
    // Bones the game puts held objects on (prop helpers of the right and left hands).
    private const ushort RightHandProp = 28422;
    private const ushort LeftHandProp = 60309;

    private sealed class Prop(PreviewModel model)
    {
        public readonly PreviewModel Model = model;
        public long LastUsed;
    }

    private readonly Dictionary<string, Prop> _props = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry(Task<IReadOnlyDictionary<uint, AnimationClip>> clips)
    {
        public readonly Task<IReadOnlyDictionary<uint, AnimationClip>> Clips = clips;
        public long LastUsed;
    }

    private GameContent? _for;
    private Task<Tables>? _tables;
    private readonly Dictionary<uint, Entry> _dictionaries = new();
    private readonly Dictionary<(uint Type, bool Male, bool Animal, int Seed), ScenarioClipChoice?> _choices = new();
    private long _tick;

    private static readonly long Start = Stopwatch.GetTimestamp();

    // Seconds since the start, the clock every animation runs on.
    public static float Now => (float)Stopwatch.GetElapsedTime(Start).TotalSeconds;

    // The clip of a ped (by model name) on a point of this type, and the object it holds; seed picks among the type's
    // variants. Null while it is being read, or when there is none.
    public ScenarioClip? Get(WorldLoader world, uint typeHash, string pedName, int seed)
    {
        if (world.Content is not { } content)
            return null;
        if (!ReferenceEquals(_for, content))
        {
            _for = content;
            _dictionaries.Clear();
            _choices.Clear();
            foreach (var prop in _props.Values)
                prop.Model.Dispose();
            _props.Clear();
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
        if (clip is null)
            return null;
        _files.AddOrUpdate(clip, Path.GetFileNameWithoutExtension(path));
        return new ScenarioClip(clip, chosen.Prop, chosen.LeftHand);
    }

    // The model of a held object, read in the background at first use (null meanwhile, or when not found).
    public GpuModel? PropModel(WorldLoader world, string name)
    {
        if (!_props.TryGetValue(name, out var prop))
        {
            if (Locate(world, name) is not { } item)
                return null;
            if (_props.Count >= MaxProps)
            {
                // Not one drawn this frame: its model may be on the way to the screen.
                var oldest = _props.Where(p => !p.Value.Model.IsLoading && p.Value.LastUsed < _frameStart).MinBy(p => p.Value.LastUsed);
                if (oldest.Value is not null)
                {
                    oldest.Value.Model.Dispose();
                    _props.Remove(oldest.Key);
                }
            }
            _props[name] = prop = new Prop(new PreviewModel());
            prop.Model.Open(item, world);
        }
        prop.LastUsed = ++_tick;
        return prop.Model.Model;
    }

    // Puts the objects read meanwhile on the graphics card; once a frame, before the peds ask for theirs.
    public void Update(ModelRenderer renderer)
    {
        _frameStart = ++_tick;
        foreach (var prop in _props.Values)
            prop.Model.Update(renderer);
    }

    private long _frameStart;

    // Its type in the map files when listed (texture dictionary), else its drawable file named after it.
    private static ViewerItem? Locate(WorldLoader world, string name)
    {
        uint hash = Joaat.Hash(name);
        if (world.Models is not { } models)
            return null;
        if (world.Maps?.GetArchetype(hash) is { } archetype && models.Locate(archetype) is { } location)
            return new ViewerItem(name, ViewerKind.Object, location);
        return (models.DrawablePath(hash) ?? models.FragmentPath(hash)) is { } path
            ? new ViewerItem(name, ViewerKind.Object, new ModelLocation(path, hash, hash)) : null;
    }

    // Where an object held in a hand goes, in the model: the hand's prop bone, as the clip moves it. Null when the
    // skeleton has none.
    public static Matrix4x4? Hand(ModelData? data, Matrix4x4[]? pose, bool left)
    {
        if (data?.Skeleton is not { } skeleton || pose is null || pose.Length != skeleton.Count)
            return null;
        int bone = skeleton.IndexOf(left ? LeftHandProp : RightHandProp);
        return bone < 0 ? null : skeleton.BindPose[bone] * pose[bone];
    }

    private const ushort LeftFoot = 14201;
    private const ushort RightFoot = 52301;

    // Whether a pose has the feet well above where a standing ped has them (sitting on a seat, a wall, the ground).
    public static bool Seated(ModelData? data, Matrix4x4[]? pose)
    {
        if (data?.Skeleton is not { } skeleton || pose is null || pose.Length != skeleton.Count)
            return false;
        int root = skeleton.IndexOf(0), left = skeleton.IndexOf(LeftFoot), right = skeleton.IndexOf(RightFoot);
        if (root < 0 || left < 0 || right < 0)
            return false;
        float Z(int bone) => (skeleton.BindPose[bone] * pose[bone]).Translation.Z;
        // Standing: feet about 0.93 m below the root; sitting on a chair 0.43 m, on a wall 0.34 m.
        return MathF.Min(Z(left), Z(right)) > Z(root) - 0.65f;
    }

    public void Dispose()
    {
        foreach (var prop in _props.Values)
            prop.Model.Dispose();
        _props.Clear();
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
