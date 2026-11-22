using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Models;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Play mode in GTA V: ig_agatha with the game's female movement clips; the map's collision files, interiors included,
// with the hand edits, and the collision kept in the models of the objects placed around (the project's too).
internal sealed class Gta5PlayGame(WorldLoader world, Func<CollisionEdits> edits) : IPlayGame
{
    public const string PedName = "ig_agatha";
    private const string MoveDictionary = "move_f@generic";
    private const string JumpDictionary = "move_jump";
    private const string JumpClip = "jump_launch_l";
    private const string SwimDictionary = "swimming@swim";
    private const string SwimIdleDictionary = "swimming@base";
    // Seconds into the jump clip: feet leaving the ground (its crouch is skipped: she leaves at once); legs tucked in
    // the air. A 0.94 s jump then lands when the clip stretches the legs down (1.2 s).
    private const float JumpStart = 0.25f;
    private const float FallStart = 0.6f;

    // Interiors are found by their position, their objects being up to this far from it.
    private const float InteriorReach = 100f;
    private const int MaxModels = 3000;

    private readonly CollisionQuery _query = new();
    // Collision of the models read so far (null: none in the model), by file; used by the reading task only.
    private readonly Dictionary<string, CollisionMesh?> _models = new(StringComparer.OrdinalIgnoreCase);
    private object? _modelsFor;

    public object? Data => world.Collisions is not null && world.Models is not null ? world.Content : null;

    public int CollisionRevision => edits().Revision;

    public void OpenCharacter(PreviewModel model)
    {
        if (world.Models is { } models)
            model.Open(ViewerCatalog.Ped(models, PedName, null), world);
    }

    public PlayClips? ReadClips()
    {
        if (world.Content is not { } content)
            return null;
        try
        {
            var moves = ReadDictionary(content, MoveDictionary);
            var jumps = ReadDictionary(content, JumpDictionary);
            if (Find(moves, "idle") is not { } idle || Find(moves, "walk") is not { } walk || Find(moves, "run") is not { } run
                || Find(moves, "sprint") is not { } sprint)
                return null;
            IReadOnlyList<AnimationClip> All(params string[] names) => names.Select(n => Find(moves, n)).OfType<AnimationClip>().ToList();
            return new PlayClips(idle, walk, run, sprint, Find(jumps, JumpClip),
                SpeedOf(walk, 1.8f), SpeedOf(run, 3.7f), SpeedOf(sprint, 5.8f), JumpStart, FallStart)
            {
                WalkStarts = All("wstart_l_0", "wstart_r_0", "wstart_l_-90", "wstart_r_90", "wstart_l_-180", "wstart_r_180"),
                RunStarts = All("rstart_l_0", "rstart_r_0", "rstart_l_-90", "rstart_r_90", "rstart_l_-180", "rstart_r_180"),
                WalkStops = All("wstop_l_0", "wstop_r_0"),
                RunStops = All("rstop_l", "rstop_r"),
                WalkTurns = All("walk_turn_180_l", "walk_turn_180_r"),
                RunTurns = All("run_turn_180_l", "run_turn_180_r"),
                Swimming = ReadSwimming(content),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public float? WaterAt(Vector2 point) => world.Water?.HeightAt(point.X, point.Y);

    // Breaststroke (walk), crawl (run), fast crawl (sprint), treading water (idle); setting off in four directions
    // and stopping. Swimming pools are model meshes, not in the water files: not swum in.
    private static PlayClips? ReadSwimming(GameContent content)
    {
        var swim = ReadDictionary(content, SwimDictionary);
        var still = ReadDictionary(content, SwimIdleDictionary);
        if (Find(still, "idle") is not { } idle || Find(swim, "walk") is not { } walk || Find(swim, "run") is not { } run
            || Find(swim, "sprint") is not { } sprint)
            return null;
        return new PlayClips(idle, walk, run, sprint, null, SpeedOf(walk, 1.5f), SpeedOf(run, 3f), SpeedOf(sprint, 4f))
        {
            RunStarts = new[] { "swim_start_0", "swim_start_90l", "swim_start_90r", "swim_start_180l", "swim_start_180r" }
                .Select(n => Find(swim, n)).OfType<AnimationClip>().ToList(),
            RunStops = Find(swim, "run_stop_r") is { } stop ? [stop] : [],
        };
    }

    public Func<Vector3, Vector3, IEnumerable<PlacedCollision>> CollisionReader()
    {
        var doors = world.DoorTunings; // Asked here: its first use starts loading it.
        var applied = edits();
        return (min, max) =>
        {
            var collisions = _query.CollisionIn(world, min, max, applied);
            collisions.AddRange(ObjectsIn(min, max, doors));
            return collisions;
        };
    }

    // The objects placed in an area (deleted ones left out) with a collision of their own in their model: props,
    // fences, street furniture. Doors are left open, as the player pushes them.
    private IEnumerable<PlacedCollision> ObjectsIn(Vector3 min, Vector3 max, DoorTunings? doors)
    {
        if (world.Maps is not { } library || world.Models is not { } models || world.ModelFiles is not { } files)
            yield break;
        if (!ReferenceEquals(_modelsFor, files))
        {
            _modelsFor = files;
            _models.Clear();
        }
        if (_models.Count > MaxModels)
            _models.Clear();
        var centre = new Vector2(min.X + max.X, min.Y + max.Y) * 0.5f;
        float radius = MathF.Max(max.X - min.X, max.Y - min.Y) * 0.5f;
        var seen = new HashSet<MapEntity>();
        var placed = new List<MapEntity>();
        foreach (var entity in library.EntitiesAround(centre, radius + InteriorReach))
        {
            if (entity.IsInterior)
                placed.AddRange(entity.InteriorEntities.Where(e => !e.IsDeleted && Vector2.Distance(Flat(e.Position), centre) <= radius));
            else if (Vector2.Distance(Flat(entity.Position), centre) <= radius)
                placed.Add(entity);
        }
        foreach (var entity in placed)
        {
            if (entity.LodLevel is not (LodLevel.Hd or LodLevel.OrphanHd) || !seen.Add(entity))
                continue;
            uint hash = entity.ArchetypeHash;
            if (doors?.OfModel(hash) is not null || Doors.IsDoor(world.Names?.TryGet(hash)))
                continue;
            if (library.GetArchetype(hash) is not { } archetype || models.Locate(archetype) is not { } location)
                continue;
            if (!_models.TryGetValue(location.Path, out var mesh))
                _models[location.Path] = mesh = ReadModel(files, location.Path);
            if (mesh is not null)
                yield return new PlacedCollision(mesh, entity.Transform);
        }
    }

    private static CollisionMesh? ReadModel(IGameFileSystem files, string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".ydr", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".yft", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return files.ReadFile(path) is { } data ? YbnReader.ReadEmbedded(data, extension) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<uint, AnimationClip> ReadDictionary(GameContent content, string dictionary)
    {
        // The last one listed: a DLC's version over the base game's.
        string? path = content.EnumerateFiles(".ycd")
            .LastOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(dictionary, StringComparison.OrdinalIgnoreCase));
        if (path is null || content.ReadFile(path) is not { } data || content.GetResourceHeader(path) is not { } header)
            return new Dictionary<uint, AnimationClip>();
        return ClipDictionaryReader.Read(data, header);
    }

    private static AnimationClip? Find(IReadOnlyDictionary<uint, AnimationClip> clips, string name) =>
        clips.GetValueOrDefault(Joaat.Hash(name))
        ?? clips.Values.FirstOrDefault(c => c.Name is { } full
            && Path.GetFileNameWithoutExtension(full.Replace('\\', '/').Split('/')[^1]).Equals(name, StringComparison.OrdinalIgnoreCase));

    // How fast a looping clip moves its ped forward, from its mover track (metres per second).
    private static float SpeedOf(AnimationClip clip, float fallback)
    {
        if (clip.Duration <= 0f)
            return fallback;
        var a = clip.Root(0f, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation).Position;
        var b = clip.Root(clip.Duration - 0.001f, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation).Position;
        float speed = a is { } from && b is { } to ? (to - from).Length() / clip.Duration : 0f;
        return speed is > 0.5f and < 12f ? speed : fallback;
    }

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);
}
