using System.Diagnostics;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Hashing;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Doors opened in the 3D view only (nothing is saved): each one moves smoothly to its open or closed position, the
// way the game opens it, and stops where its collision check found an obstacle.
internal sealed class DoorAnimator
{
    private const float Speed = 1.6f; // Share of the movement per second.

    private sealed class Motion
    {
        public DoorShape Shape;
        public float Angle;
        public float Direction = 1f;
        public float Amount;
        public float Target;
    }

    public readonly record struct Clearance(float WantedMin, float WantedMax, float ReachedMin, float ReachedMax)
    {
        public bool MinBlocked => DoorClearance.IsBlocked(WantedMin, ReachedMin);
        public bool MaxBlocked => DoorClearance.IsBlocked(WantedMax, ReachedMax);
        public bool Blocked => MinBlocked || MaxBlocked;
        public bool NoPassage => DoorClearance.NoPassage(WantedMin, WantedMax, ReachedMin, ReachedMax);
    }

    private readonly Dictionary<MapEntity, Motion> _doors = new();
    private readonly Dictionary<MapEntity, (int Revision, Clearance? Result)> _clearances = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MapLibrary? _library;
    private double _last;

    public int Count => _doors.Count;

    // Changes whenever a door moves (the sun's shadow images are drawn again).
    public int Version { get; private set; }

    public bool IsOpen(MapEntity door) => _doors.TryGetValue(door, out var motion) && motion.Target > 0f;

    // The leaf's movement to apply before the object's own placement, while the door is not closed.
    public bool TryGetPose(MapEntity entity, out Matrix4x4 local)
    {
        if (_doors.Count > 0 && _doors.TryGetValue(entity, out var motion) && motion.Amount > 0f)
        {
            float t = motion.Amount;
            local = motion.Shape.Opened(t * t * (3f - 2f * t), motion.Angle, motion.Direction);
            return true;
        }
        local = Matrix4x4.Identity;
        return false;
    }

    public void Update(MapLibrary? library)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float seconds = (float)Math.Min(now - _last, 0.1);
        _last = now;
        if (library != _library)
        {
            _library = library;
            if (_doors.Count > 0)
                Version++;
            _doors.Clear();
            _clearances.Clear();
        }
        if (_doors.Count == 0)
            return;
        List<MapEntity>? closed = null;
        foreach (var (entity, motion) in _doors)
        {
            float step = Speed * seconds;
            float amount = motion.Amount;
            motion.Amount = motion.Target > motion.Amount
                ? MathF.Min(motion.Target, motion.Amount + step)
                : MathF.Max(motion.Target, motion.Amount - step);
            if (motion.Amount != amount)
                Version++;
            if ((motion.Amount <= 0f && motion.Target <= 0f) || entity.IsDeleted)
                (closed ??= []).Add(entity);
        }
        if (closed is not null)
        {
            foreach (var entity in closed)
                _doors.Remove(entity);
        }
    }

    public void CloseAll()
    {
        foreach (var motion in _doors.Values)
            motion.Target = 0f;
    }

    // Opens a closed door or closes an open one; returns a message for the status bar.
    public string? Toggle(MapEntity door, AppState state) => Set(door, !IsOpen(door), state);

    public string? Set(MapEntity door, bool open, AppState state)
    {
        if (!open)
        {
            if (_doors.TryGetValue(door, out var opened))
                opened.Target = 0f;
            return null;
        }
        if (state.World.Maps is not { } library || library.GetArchetype(door.ArchetypeHash) is not { } archetype)
            return null;
        string? name = state.World.Names?.TryGet(door.ArchetypeHash);
        var shape = DoorShape.Of(archetype, name);
        if (shape.Kind == DoorKind.Fixed)
            return L("Cette porte ne s'ouvre pas en jeu : son type n'a pas le drapeau « Dynamic ».");

        var motion = _doors.TryGetValue(door, out var existing) ? existing : new Motion();
        motion.Shape = shape;
        string? message = null;
        if (shape.Kind == DoorKind.Hinged)
        {
            var (min, max) = DoorShape.Swing(state.World.DoorTunings?.OfModel(door.ArchetypeHash));
            var check = ClearanceOf(door, state);
            float reachMin = check?.ReachedMin ?? min, reachMax = check?.ReachedMax ?? max;
            // Away from the camera, as when walking through it; the side that opens wider when that one is blocked.
            Matrix4x4.Invert(door.Transform, out var toDoor);
            float cameraSide = Vector3.Transform(state.Camera.Position, toDoor).Y;
            bool positive = cameraSide * shape.Side <= 0f;
            float preferred = positive ? reachMax : reachMin, other = positive ? reachMin : reachMax;
            float enough = MathF.Min(DoorClearance.Passage, MathF.Abs(positive ? max : min) - 0.5f);
            motion.Angle = MathF.Abs(preferred) >= enough || MathF.Abs(preferred) >= MathF.Abs(other) ? preferred : other;
            if (check is { NoPassage: true })
                message = MathF.Abs(motion.Angle) < 1f
                    ? L("La porte touche un obstacle dès qu'elle s'ouvre : on ne pourra pas passer.")
                    : L($"La porte touche un obstacle à {MathF.Abs(motion.Angle):0}° : trop peu ouverte pour passer.");
        }
        else if (shape.Kind == DoorKind.Sliding)
        {
            motion.Direction = SlideDirection(door, library, state.World.Names);
        }
        motion.Target = 1f;
        _doors[door] = motion;
        return message;
    }

    // A sliding door next to another one (double door) slides away from it.
    private static float SlideDirection(MapEntity door, MapLibrary library, HashNames? names)
    {
        if (!Matrix4x4.Invert(door.Transform, out var toDoor))
            return 1f;
        var partner = Partner(door, library, names);
        return partner is not null && Vector3.Transform(partner.Position, toDoor).X > 0f ? -1f : 1f;
    }

    // How far a swinging door opens before touching the collision around it, null when it does not swing or the
    // collisions are not loaded. Kept until the door moves.
    public Clearance? ClearanceOf(MapEntity door, AppState state)
    {
        if (_clearances.TryGetValue(door, out var cached) && cached.Revision == door.Revision)
            return cached.Result;
        var result = Check(door, state.World, state.CollisionQuery, state.CollisionEdits);
        _clearances[door] = (door.Revision, result);
        return result;
    }

    public static Clearance? Check(MapEntity door, WorldLoader world, CollisionQuery query, Data.Collisions.CollisionEdits? edits)
    {
        if (world.Collisions is null || world.Maps?.GetArchetype(door.ArchetypeHash) is not { } archetype)
            return null;
        var shape = DoorShape.Of(archetype, world.Names?.TryGet(door.ArchetypeHash));
        if (shape.Kind != DoorKind.Hinged)
            return null;
        var (min, max) = DoorShape.Swing(world.DoorTunings?.OfModel(door.ArchetypeHash));
        float scale = MathF.Max(door.Scale.X, MathF.Max(door.Scale.Y, door.Scale.Z));
        float reach = (shape.Width + 0.5f) * scale;
        var p = door.Position;
        var collisions = query.CollisionIn(world,
            new Vector3(p.X - reach, p.Y - reach, p.Z + shape.Bottom * scale - 0.5f),
            new Vector3(p.X + reach, p.Y + reach, p.Z + (shape.Bottom + shape.Height) * scale + 0.5f), edits);
        // Movable furniture has its own collision, in no collision file: its box stands for it. A big piece's origin
        // can be farther than the leaf reaches.
        float floor = p.Z + shape.Bottom * scale;
        foreach (var prop in Nearby(door, world.Maps!, reach + DoorClearance.MaxFurniture / 2f))
        {
            if (prop.IsInterior || Doors.IsDoor(world.Names?.TryGet(prop.ArchetypeHash))
                || world.Maps!.GetArchetype(prop.ArchetypeHash) is not { } type || (type.Flags & DoorCheck.DynamicFlag) == 0
                || DoorClearance.Furniture(type.BoundsMin, type.BoundsMax, prop.Transform, door.Position, floor) is not { } box)
                continue;
            collisions.Add(new PlacedCollision(box, prop.Transform));
        }
        var (reachedMin, reachedMax) = DoorClearance.Reach(shape, min, max, door.Transform, collisions);
        return new Clearance(min, max, reachedMin, reachedMax);
    }

    // The objects around a door, those of its interior included (the map's index only holds the interior itself).
    private static IEnumerable<MapEntity> Nearby(MapEntity door, MapLibrary library, float radius)
    {
        var center = new Vector2(door.Position.X, door.Position.Y);
        bool Close(MapEntity e) => e != door && !e.IsDeleted && e.LodLevel is LodLevel.Hd or LodLevel.OrphanHd
            && (e.EntitySet is null || e.EntitySet.IsActive) && Vector2.Distance(new Vector2(e.Position.X, e.Position.Y), center) <= radius;
        var found = library.EntitiesAround(center, radius).Where(Close);
        return door.Interior is { } interior ? found.Concat(interior.InteriorEntities.Where(Close)).Distinct() : found;
    }

    // The other leaf of a double door, if any.
    public static MapEntity? Partner(MapEntity door, MapLibrary library, HashNames? names)
    {
        MapEntity? partner = null;
        float best = DoorLockScript.PairDistance;
        foreach (var other in Nearby(door, library, DoorLockScript.PairDistance))
        {
            if (!Doors.IsDoor(names?.TryGet(other.ArchetypeHash)) || MathF.Abs(other.Position.Z - door.Position.Z) >= 0.5f)
                continue;
            float distance = Vector3.Distance(other.Position, door.Position);
            if (distance < best)
                (partner, best) = (other, distance);
        }
        return partner;
    }
}
