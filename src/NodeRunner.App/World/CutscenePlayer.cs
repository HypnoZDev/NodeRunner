using System.Numerics;
using NodeRunner.Data.Cutscenes;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
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
    private const float InteriorReach = 150f;

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
        // Times its model is loaded or unloaded (events 6 / 7), in order.
        public readonly List<(float Time, bool On)> Switches = [];
        // Clothes put on along the cutscene (event 34), in order, and those of the model shown or being read.
        public readonly List<(float Time, int Component, int Drawable, int Texture)> Clothes = [];
        public string Dressed = "";
        // How many of Clothes the model shown or being read wears (-1: none yet).
        public int DressedCount = -1;
    }

    private GameContent? _content;
    private Task<Cutscene>? _loading;
    private Task<Dictionary<uint, string>>? _pedNames;
    private Task<Dictionary<string, string>>? _clipFiles;
    private readonly Dictionary<int, Task<Dictionary<(uint, int), AnimationClip>>> _cuts = new();
    private readonly List<Actor> _actors = [];
    private bool _actorsMade;
    // Map objects the cutscene hides (replaced by its own props), with the times it hides / shows them again
    // (events 14 / 15, in order).
    private readonly List<(MapEntity Entity, List<(float Time, bool On)> Switches)> _hides = [];
    private readonly HashSet<MapEntity> _hidden = [];

    // The map objects hidden at Time.
    public IReadOnlySet<MapEntity> Hidden => _hidden;

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
        _hides.Clear();
        _hidden.Clear();
        _cuts.Clear();
        _actorsMade = false;
        _loading = null;
        Cutscene = null;
        Path = null;
        Error = null;
        _cameraKey = null;
    }

    // Once a frame: takes what was read, puts the models on the graphics card, places the objects at Time.
    public void Update(WorldLoader world, ModelRenderer renderer)
    {
        if (!ReferenceEquals(world.Content, _content) && _content is not null)
        {
            // The map was loaded again (other scripted maps shown...): the same cutscene is read again from its files.
            string? reopen = Path;
            Close();
            _content = null;
            _clipFiles = null;
            _pedNames = null;
            _reopen = reopen;
        }
        if (_reopen is { } waiting && world.Content is not null && world.Models is not null)
        {
            _reopen = null;
            Open(world, waiting);
        }
        if (_loading is { IsCompleted: true } loading)
        {
            _loading = null;
            if (loading.IsCompletedSuccessfully)
            {
                Cutscene = loading.Result;
                JustLoaded = true;
                var keys = Cutscene.ClipKeys();
                _cameraKey = Cutscene.Objects.FirstOrDefault(o => o.Kind == CutsceneObjectKind.Camera) is { } camera && keys.TryGetValue(camera.Id, out var cameraKey)
                    ? cameraKey : null;
            }
            else
                Error = loading.Exception?.InnerException?.Message ?? L("Lecture impossible.");
        }
        if (Cutscene is not { } cutscene || world.Models is not { } models)
            return;
        if (!_actorsMade)
        {
            _pedNames ??= Task.Run(() => models.PedNames().ToDictionary(n => Joaat.Hash(n), n => n));
            if (!_pedNames.IsCompleted)
                return;
            MakeActors(world, cutscene, _pedNames.IsCompletedSuccessfully ? _pedNames.Result : []);
            _actorsMade = true;
        }

        foreach (var actor in _actors)
        {
            Dress(world, actor);
            actor.Model.Update(renderer);
        }

        var (cut, start) = cutscene.CutAt(Time);
        var clips = Clips(cutscene, cut);
        Clips(cutscene, cut + 1); // The next cut, read ahead.
        foreach (var actor in _actors)
            Place(actor, clips, Time - start, cutscene.SceneTransformAt(Time));
        _hidden.Clear();
        foreach (var (entity, switches) in _hides)
        {
            if (IsOn(switches, Time, false))
                _hidden.Add(entity);
        }
    }

    private void Place(Actor actor, Dictionary<(uint, int), AnimationClip>? clips, float local, Matrix4x4 scene)
    {
        actor.Shown = false;
        actor.Posed = false;
        if (clips is null || !IsOn(actor.Switches, Time, true) || !clips.TryGetValue(actor.Key, out var clip) || actor.Model.Model is null)
            return;
        float time = Math.Clamp(local, 0f, Math.Max(clip.Duration * 0.9999f, 0f));
        var (position, rotation) = clip.Root(time, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation);
        actor.World = Matrix4x4.CreateFromQuaternion(rotation ?? Quaternion.Identity)
                      * Matrix4x4.CreateTranslation(position ?? Vector3.Zero) * scene;
        if (actor.Model.Data?.Skeleton is null)
        {
            // Without a skeleton, the root bone's movement (a glass tilted in the hand) moves the whole model.
            var (offset, turn) = clip.Root(time, AnimationTrackKind.BoneTranslation, AnimationTrackKind.BoneRotation);
            actor.World = Matrix4x4.CreateFromQuaternion(turn ?? Quaternion.Identity) * Matrix4x4.CreateTranslation(offset ?? Vector3.Zero) * actor.World;
        }
        if (actor.Model.Data?.Skeleton is { } skeleton)
        {
            if (actor.Pose is null || actor.Pose.Length != skeleton.Count)
                actor.Pose = new Matrix4x4[skeleton.Count];
            SkeletonAnimator.Pose(skeleton, clip, time, actor.Pose);
            actor.Posed = true;
        }
        actor.Shown = true;
    }

    // Reads the ped again when its clothes at Time are not those shown (the model shown stays until then). Clothes put
    // on before the first change of the cutscene are worn from the start.
    private void Dress(WorldLoader world, Actor actor)
    {
        if (actor.Clothes.Count == 0 || actor.Item.Ped is not { } ped)
            return;
        float until = Math.Max(Time, actor.Clothes[0].Time);
        int count = 0;
        while (count < actor.Clothes.Count && actor.Clothes[count].Time <= until)
            count++;
        // Same changes as for the model shown: nothing to do (the usual case, every frame).
        if (count == actor.DressedCount)
            return;
        actor.DressedCount = count;
        var outfit = new SortedDictionary<int, (int, int)>();
        foreach (var (_, component, drawable, texture) in actor.Clothes.Take(count))
            outfit[component] = (drawable, texture);
        string key = string.Join(";", outfit.Select(p => $"{p.Key}:{p.Value.Item1}:{p.Value.Item2}"));
        if (key == actor.Dressed)
            return;
        actor.Dressed = key;
        actor.Model.Open(actor.Item with { Ped = ped with { Outfit = outfit } }, world);
    }

    // Whether the last switch before time is on; empty: whenEmpty (objects never loaded nor unloaded are always there).
    private static bool IsOn(List<(float Time, bool On)> switches, float time, bool whenEmpty)
    {
        if (switches.Count == 0)
            return whenEmpty;
        bool on = false;
        foreach (var (at, value) in switches)
        {
            if (at > time)
                break;
            on = value;
        }
        return on;
    }

    // Where the cutscene's camera is at Time, where it looks and its vertical field of view (degrees); null while its
    // clip is read.
    public (Vector3 Position, Vector3 Forward, float FieldOfView)? Shot()
    {
        if (Cutscene is not { } cutscene || _cameraKey is not { } key)
            return null;
        var (cut, start) = cutscene.CutAt(Time);
        if (Clips(cutscene, cut) is not { } clips || !clips.TryGetValue(key, out var clip))
            return null;
        float time = Math.Clamp(Time - start, 0f, Math.Max(clip.Duration * 0.9999f, 0f));
        var (position, rotation) = clip.Root(time, AnimationTrackKind.CameraTranslation, AnimationTrackKind.CameraRotation);
        if (position is not { } p || rotation is not { } r)
            return null;
        var scene = cutscene.SceneTransformAt(Time);
        // The camera looks along its own +Y.
        var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.Transform(Vector3.UnitY, r), scene));
        return (Vector3.Transform(p, scene), forward, clip.Value(time, AnimationTrackKind.CameraFieldOfView) ?? 45f);
    }

    private (uint, int)? _cameraKey;
    private string? _reopen;

    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection)
    {
        if (_actors.Count == 0)
            return;
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            foreach (var actor in _actors)
            {
                // A skinned model without its pose would stand in its default pose (arms spread): not drawn.
                if (!actor.Shown || actor.Model.Model is not { } model || model.IsSkinned && !actor.Posed)
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
            _actors.Add(actor);
            byId[source.Id] = actor;
        }
        // Objects come and go with their models, loaded and unloaded along the cutscene.
        foreach (var e in cutscene.LoadEvents.Concat(cutscene.Events))
        {
            if (e.Kind is not (CutsceneEventKind.LoadModels or CutsceneEventKind.UnloadModels) || e.Args is null)
                continue;
            foreach (int id in e.Args.ObjectIds)
            {
                if (byId.TryGetValue(id, out var actor))
                    actor.Switches.Add((e.Time, e.Kind == CutsceneEventKind.LoadModels));
            }
        }
        foreach (var e in cutscene.LoadEvents.Concat(cutscene.Events).OrderBy(e => e.Time))
        {
            if (e.Kind == CutsceneEventKind.PedVariation && e.Args is { Component: >= 0 and < 12, Drawable: >= 0 } args
                && byId.TryGetValue(e.ObjectId, out var actor))
                actor.Clothes.Add((e.Time, args.Component, args.Drawable, Math.Max(args.Texture, 0)));
        }
        // Peds dressed by the cutscene are read with their clothes (Dress).
        foreach (var actor in _actors.Where(a => a.Clothes.Count == 0 || a.Item.Ped is null))
            actor.Model.Open(actor.Item, world);
        // Unloaded and loaded again at the same time: loaded wins.
        foreach (var actor in _actors)
            actor.Switches.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.On.CompareTo(b.On));
        HideMapObjects(world, cutscene);
    }

    // The map objects of each hidden model (its type, within its radius of its place on the map); several hidden models
    // can name the same object: the last event sent to any of them counts.
    private void HideMapObjects(WorldLoader world, Cutscene cutscene)
    {
        if (world.Maps is not { } maps)
            return;
        var targets = new Dictionary<int, List<MapEntity>>();
        foreach (var hidden in cutscene.Objects.Where(o => o.Kind == CutsceneObjectKind.HiddenModel && o.Name != 0))
        {
            float radius = Math.Max(hidden.Radius, 0.1f);
            var at = new Vector2(hidden.Position.X, hidden.Position.Y);
            // Objects of an interior are found through the interior, placed further away.
            var inside = maps.EntitiesAround(at, InteriorReach).SelectMany(e => e.InteriorEntities);
            targets[hidden.Id] = maps.EntitiesAround(at, radius).Concat(inside)
                .Where(e => (e.ArchetypeHash == hidden.Name || e.FileArchetypeHash == hidden.Name)
                            && Vector2.Distance(new Vector2(e.Position.X, e.Position.Y), at) <= radius && Math.Abs(e.Position.Z - hidden.Position.Z) <= radius + 2f)
                .Distinct()
                .ToList();
        }
        var byEntity = new Dictionary<MapEntity, List<(float, bool)>>();
        foreach (var e in cutscene.LoadEvents.Concat(cutscene.Events).OrderBy(e => e.Time))
        {
            if (e.Kind is not (CutsceneEventKind.EnableHideObject or CutsceneEventKind.DisableHideObject) || !targets.TryGetValue(e.ObjectId, out var entities))
                continue;
            foreach (var entity in entities)
            {
                if (!byEntity.TryGetValue(entity, out var switches))
                    byEntity[entity] = switches = [];
                switches.Add((e.Time, e.Kind == CutsceneEventKind.EnableHideObject));
            }
        }
        foreach (var (entity, switches) in byEntity)
            _hides.Add((entity, switches));
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
                // Props move parts of themselves with their own bones (a glass tilted, headphones bent).
                if (world.Maps?.GetArchetype(hash) is { } archetype && models.Locate(archetype) is { } location)
                    return new ViewerItem(name, ViewerKind.Object, location) { OwnBones = IsDrawable(location.Path) };
                return (models.DrawablePath(hash) ?? models.FragmentPath(hash)) is { } path
                    ? new ViewerItem(name, ViewerKind.Object, new ModelLocation(path, hash, hash)) { OwnBones = IsDrawable(path) } : null;
        }
    }

    private static bool IsDrawable(string path) => path.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Close();
}
