using System.Numerics;
using NodeRunner.Data.Cutscenes;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Cutscenes;
using NodeRunner.Data.Native.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A game cutscene played on the map, at its place: its peds, props, vehicles and weapons, each moved and bent by its
// clip of the camera cut playing. The cutscene file, the models and the clips of each cut are read in the background.
internal sealed class CutscenePlayer : IDisposable
{
    private const int KeptCuts = 4;

    private sealed class Actor(CutsceneObject source, (uint, int) key, ViewerItem item)
    {
        public readonly CutsceneObject Source = source;
        public readonly (uint Name, int Copy) Key = key;
        public readonly ViewerItem Item = item;
        public readonly PreviewModel Model = new();
        public Matrix4x4[]? Pose;
        public bool Posed;
        public bool Shown;
        public Matrix4x4 World;
        // Times its animation is switched on or off (events 22 / 23), in order.
        public readonly List<(float Time, bool On)> Switches = [];
    }

    private GameContent? _content;
    private Task<Cutscene>? _loading;
    private Task<Dictionary<uint, string>>? _pedNames;
    private Task<Dictionary<string, string>>? _clipFiles;
    private readonly Dictionary<int, Task<Dictionary<(uint, int), AnimationClip>>> _cuts = new();
    private readonly List<Actor> _actors = [];
    private bool _actorsMade;

    public Cutscene? Cutscene { get; private set; }
    public string? Path { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading => _loading is not null || Cutscene is not null && !_actorsMade;

    // The time shown (seconds from the start), set by the timeline.
    public float Time { get; set; }

    public int ActorCount => _actors.Count;
    public int ShownCount => _actors.Count(a => a.Shown);

    // Set when a cutscene was just read, for the camera to go to it once.
    public bool JustLoaded { get; set; }

    public void Open(WorldLoader world, string path)
    {
        Close();
        if (world.Content is not { } content)
            return;
        _content = content;
        Path = path;
        Time = 0f;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        _loading = Task.Run(() => CutsceneReader.Read(content.ReadFile(path) ?? throw new InvalidDataException(L("Fichier illisible.")), name));
        _clipFiles ??= Task.Run(() => content.EnumerateFiles(".ycd")
            .GroupBy(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
    }

    public void Close()
    {
        foreach (var actor in _actors)
            actor.Model.Dispose();
        _actors.Clear();
        _cuts.Clear();
        _actorsMade = false;
        _loading = null;
        Cutscene = null;
        Path = null;
        Error = null;
    }

    // Once a frame: takes what was read, puts the models on the graphics card, places the objects at Time.
    public void Update(WorldLoader world, ModelRenderer renderer)
    {
        if (!ReferenceEquals(world.Content, _content) && _content is not null)
        {
            // Another map loaded: its files are not the same.
            Close();
            _content = null;
            _clipFiles = null;
            _pedNames = null;
            return;
        }
        if (_loading is { IsCompleted: true } loading)
        {
            _loading = null;
            if (loading.IsCompletedSuccessfully)
            {
                Cutscene = loading.Result;
                JustLoaded = true;
            }
            else
                Error = loading.Exception?.InnerException?.Message ?? L("Lecture impossible.");
        }
        if (Cutscene is not { } cutscene || world.Models is not { } models)
            return;
        if (!_actorsMade)
        {
            _pedNames ??= Task.Run(() => models.PedNames().ToDictionary(n => Joaat.Hash(n), n => n));
            if (!_pedNames.IsCompletedSuccessfully)
                return;
            MakeActors(world, cutscene, _pedNames.Result);
            _actorsMade = true;
        }

        foreach (var actor in _actors)
            actor.Model.Update(renderer);

        var (cut, start) = cutscene.CutAt(Time);
        var clips = Clips(cutscene, cut);
        Clips(cutscene, cut + 1); // The next cut, read ahead.
        foreach (var actor in _actors)
            Place(actor, clips, Time - start, cutscene.SceneTransform);
    }

    private void Place(Actor actor, Dictionary<(uint, int), AnimationClip>? clips, float local, Matrix4x4 scene)
    {
        actor.Shown = false;
        actor.Posed = false;
        if (clips is null || !IsOn(actor, Time) || !clips.TryGetValue(actor.Key, out var clip) || actor.Model.Model is null)
            return;
        float time = Math.Clamp(local, 0f, Math.Max(clip.Duration * 0.9999f, 0f));
        var (position, rotation) = clip.Root(time, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation);
        actor.World = Matrix4x4.CreateFromQuaternion(rotation ?? Quaternion.Identity)
                      * Matrix4x4.CreateTranslation(position ?? Vector3.Zero) * scene;
        if (actor.Model.Data?.Skeleton is { } skeleton)
        {
            if (actor.Pose is null || actor.Pose.Length != skeleton.Count)
                actor.Pose = new Matrix4x4[skeleton.Count];
            SkeletonAnimator.Pose(skeleton, clip, time, actor.Pose);
            actor.Posed = true;
        }
        actor.Shown = true;
    }

    // Objects without switches are always there; the others from their first "on".
    private static bool IsOn(Actor actor, float time)
    {
        if (actor.Switches.Count == 0)
            return true;
        bool on = false;
        foreach (var (at, value) in actor.Switches)
        {
            if (at > time)
                break;
            on = value;
        }
        return on;
    }

    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection)
    {
        if (_actors.Count == 0)
            return;
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            foreach (var actor in _actors)
            {
                if (!actor.Shown || actor.Model.Model is not { } model)
                    continue;
                renderer.BonePose = actor.Posed && model.IsSkinned ? actor.Pose : null;
                renderer.Draw(model, actor.World, viewProjection, Vector3.One);
            }
        }
        renderer.BonePose = null;
    }

    // The clips of a camera cut by object, read in the background (null meanwhile or when the cut has none).
    private Dictionary<(uint, int), AnimationClip>? Clips(Cutscene cutscene, int cut)
    {
        if (cut > cutscene.CameraCuts.Count || _content is not { } content || _clipFiles is not { IsCompletedSuccessfully: true } files)
            return null;
        if (!_cuts.TryGetValue(cut, out var task))
        {
            if (_cuts.Count >= KeptCuts)
            {
                // The cut furthest from this one.
                int far = _cuts.Keys.Where(k => _cuts[k].IsCompleted).OrderByDescending(k => Math.Abs(k - cut)).FirstOrDefault(-1);
                if (far >= 0)
                    _cuts.Remove(far);
            }
            string? path = files.Result.GetValueOrDefault($"{cutscene.Name}-{cut}");
            _cuts[cut] = task = Task.Run(() => ReadClips(content, path, cut));
        }
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static Dictionary<(uint, int), AnimationClip> ReadClips(GameContent content, string? path, int cut)
    {
        var clips = new Dictionary<(uint, int), AnimationClip>();
        if (path is null)
            return clips;
        try
        {
            if (content.ReadFile(path) is { } data && content.GetResourceHeader(path) is { } header)
            {
                foreach (var clip in ClipDictionaryReader.Read(data, header).Values)
                {
                    if (clip.Name is { } name)
                        clips[Cutscene.ClipKey(name, cut)] = clip;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The objects of this cut stay hidden.
        }
        return clips;
    }

    private void MakeActors(WorldLoader world, Cutscene cutscene, Dictionary<uint, string> pedNames)
    {
        var keys = cutscene.ClipKeys();
        var byId = new Dictionary<int, Actor>();
        foreach (var source in cutscene.Objects)
        {
            if (source.Kind is not (CutsceneObjectKind.Ped or CutsceneObjectKind.Prop or CutsceneObjectKind.Vehicle or CutsceneObjectKind.Weapon)
                || !keys.TryGetValue(source.Id, out var key) || ItemFor(world, source, pedNames) is not { } item)
                continue;
            var actor = new Actor(source, key, item);
            actor.Model.Open(item, world);
            _actors.Add(actor);
            byId[source.Id] = actor;
        }
        foreach (var e in cutscene.Events)
        {
            if (e.Kind is not (CutsceneEventKind.EnableAnimation or CutsceneEventKind.DisableAnimation) || e.Args is null)
                continue;
            foreach (int id in e.Args.ObjectIds)
            {
                if (byId.TryGetValue(id, out var actor))
                    actor.Switches.Add((e.Time, e.Kind == CutsceneEventKind.EnableAnimation));
            }
        }
    }

    // The model of an object: a ped by its name, a vehicle by its fragment, a prop or weapon by its type on the map
    // (texture dictionary) or its drawable / fragment file.
    private static ViewerItem? ItemFor(WorldLoader world, CutsceneObject source, Dictionary<uint, string> pedNames)
    {
        var models = world.Models!;
        uint hash = source.Model;
        string name = world.Names?.Describe(hash) ?? $"0x{hash:X8}";
        switch (source.Kind)
        {
            case CutsceneObjectKind.Ped:
                return pedNames.TryGetValue(hash, out var ped) ? ViewerCatalog.Ped(models, hash, ped, null) : null;
            case CutsceneObjectKind.Vehicle:
                return models.FragmentPath(hash) is { } vehicle ? new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(vehicle, hash, hash)) : null;
            default:
                if (world.Maps?.GetArchetype(hash) is { } archetype && models.Locate(archetype) is { } location)
                    return new ViewerItem(name, ViewerKind.Object, location);
                return (models.DrawablePath(hash) ?? models.FragmentPath(hash)) is { } path
                    ? new ViewerItem(name, ViewerKind.Object, new ModelLocation(path, hash, hash)) : null;
        }
    }

    public void Dispose() => Close();
}
