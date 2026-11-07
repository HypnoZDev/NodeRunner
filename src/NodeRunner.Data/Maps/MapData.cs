using System.Numerics;

namespace NodeRunner.Data.Maps;

// The content of one map data file (.ymap): a list of placed objects ("entities") and the area they cover.
public sealed class MapData
{
    public required uint NameHash { get; init; }

    public uint ParentHash { get; init; }

    public uint Flags { get; init; }
    public uint ContentFlags { get; init; }

    // Loaded only when a script asks for it (flag 1): prologue, Cayo Perico, mission states, time-of-day variants,
    // special reflection models...
    public bool IsScripted => (Flags & 1) != 0;

    public Vector3 StreamingExtentsMin { get; init; }
    public Vector3 StreamingExtentsMax { get; init; }

    public Vector3 EntitiesExtentsMin { get; init; }
    public Vector3 EntitiesExtentsMax { get; init; }

    public required IReadOnlyList<MapEntity> Entities { get => _entities; init => _entities = value.ToList(); }
    private List<MapEntity> _entities = [];

    // A new list, not changed in place: background threads may be going through the old one.
    internal void AddEntity(MapEntity entity) => _entities = [.. _entities, entity];

    // Path of the file in the game archives, once loaded by MapLibrary; for a IsCustom map, its file name only.
    public string? FilePath { get; internal set; }

    // A map that is not a game file: made by the user (the project's map of added objects, see CreateCustomMap) or
    // taken from the save folder without a game file of that name.
    public bool IsCustom { get; internal set; }

    // Loaded from the user's save folder (a version saved by NodeRunner) instead of the game's file, or saved since.
    public bool IsSaved { get; set; }

    // For a map loaded from the user's save folder: the file's decompressed content, which saving starts from (its
    // entities' SourceIndex refer to it).
    public byte[]? SavedContent { get; internal set; }

    public IEnumerable<MapEntity> KeptEntities => Entities.Where(e => !e.IsDeleted);

    public int KeptIndexOf(MapEntity entity)
    {
        int index = 0;
        foreach (var e in Entities)
        {
            if (e == entity)
                return e.IsDeleted ? -1 : index;
            if (!e.IsDeleted)
                index++;
        }
        return -1;
    }
}

// An object placed on the map: which model (archetype), where, how it is turned and scaled.
public sealed class MapEntity
{
    public required uint ArchetypeHash { get; init; }

    public required Vector3 Position { get => _position; init => _position = value; }
    private Vector3 _position;

    // Rotation exactly as stored in the file; for an object inside an interior, already combined with the interior's
    // rotation.
    public Quaternion StoredRotation { get => _storedRotation; init => _storedRotation = value; }
    private Quaternion _storedRotation = Quaternion.Identity;

    public Vector3 Scale { get => _scale; init => _scale = value; }
    private Vector3 _scale = Vector3.One;

    // Incremented each time the object is moved, turned or scaled, so that what was computed from its placement (draw
    // transform, rotation angles shown...) can be refreshed.
    public int Revision { get; private set; }

    // Every placed object can be moved. An object inside an interior is defined by the interior's archetype (.ytyp):
    // moving it moves it in every instance of the interior.
    public bool CanEdit => true;

    // False for the objects of an interior's entity set (not handled yet).
    public bool CanDelete => EntitySet is null;

    // For an object inside an interior: its definition in the interior's archetype (placed relative to the interior).
    public MapEntity? Definition => _definition;

    // For a definition: its copies placed in the interior instances.
    public IReadOnlyList<MapEntity> Copies => _copies ?? (IReadOnlyList<MapEntity>)[];

    // Moves, turns and scales the object (orientation is the world orientation, as Orientation).
    public void SetPlacement(Vector3 position, Quaternion orientation, Vector3 scale)
    {
        if (Interior is { } interior && _definition is { } definition)
        {
            var toLocal = Quaternion.Inverse(interior.Orientation);
            definition.SetPlacement(Vector3.Transform(position - interior.Position, toLocal),
                Quaternion.Normalize(toLocal * orientation), scale);
            return;
        }

        _position = position;
        _storedRotation = IsInterior ? orientation : Quaternion.Inverse(orientation);
        _scale = scale;
        Revision++;
        foreach (var copy in _copies ?? [])
            copy.FollowInterior();
        foreach (var inside in InteriorEntities)
            inside.FollowInterior();
        foreach (var set in EntitySets)
        {
            foreach (var inside in set.Entities)
                inside.FollowInterior();
        }
    }

    public uint Flags { get; init; }
    public uint Guid { get; init; }

    public int SourceIndex { get; init; } = -1;

    public bool IsDeleted { get; internal set; }

    // Index of the lower-detail entity, -1 if none: in the parent ymap if LodInParentMap, otherwise in the same ymap.
    public int ParentIndex { get; init; } = -1;

    public bool LodInParentMap => (Flags & 8) != 0;

    // Render flags: only drawn in shadows, in reflections, in water reflections, in mirrors.
    private const uint OnlyInShadowsOrReflections = 0x800000 | 0x2000000 | 0x8000000 | 0x20000000;

    // False for stand-ins the game only draws in shadows or reflections (e.g. closed boxes standing for a metro
    // tunnel in reflections): they must not be drawn in the normal view.
    public bool IsDrawnInMainView => (Flags & OnlyInShadowsOrReflections) == 0;

    public float LodDistance { get; init; }

    public float ChildLodDistance { get; init; }

    public LodLevel LodLevel { get; init; }
    public int ChildCount { get; init; }

    public bool IsInterior { get; init; }

    public IReadOnlyList<MapEntity> InteriorEntities { get; internal set; } = [];

    public IReadOnlyList<PlacedEntitySet> EntitySets { get; internal set; } = [];

    public PlacedEntitySet? EntitySet { get; internal set; }

    public MapEntity? Interior { get; private init; }

    private MapEntity? _definition;
    // For a definition: its copies placed in the interior instances.
    private List<MapEntity>? _copies;

    public MapData? Map { get; internal set; }

    public int InteriorRoom { get; init; } = -1;

    public int InteriorPortal { get; init; } = -1;

    // For an object inside an interior: a simplified view of the surroundings, only seen through the windows from
    // inside.
    public bool IsWindowView { get; internal set; }

    // Copy of this object (placed relative to an interior, as in the interior's archetype) moved into the world by
    // the interior instance: its rotation and position apply on top of this one's.
    internal MapEntity PlaceInside(MapEntity instance)
    {
        var (position, storedRotation) = PlacedInside(instance);
        var copy = new MapEntity
        {
            ArchetypeHash = ArchetypeHash,
            Position = position,
            StoredRotation = storedRotation,
            Scale = Scale,
            Flags = Flags,
            Guid = Guid,
            LodDistance = LodDistance,
            ChildLodDistance = ChildLodDistance,
            LodLevel = LodLevel,
            Interior = instance,
            Map = instance.Map,
            InteriorRoom = InteriorRoom,
            InteriorPortal = InteriorPortal,
            IsWindowView = IsWindowView,
            _definition = this,
        };
        (_copies ??= []).Add(copy);
        return copy;
    }

    private (Vector3 Position, Quaternion StoredRotation) PlacedInside(MapEntity instance) =>
        (instance.Position + Vector3.Transform(Position, instance.Orientation),
         Quaternion.Inverse(instance.Orientation * Orientation));

    private void FollowInterior()
    {
        if (Interior is null || _definition is null)
            return;
        (_position, _storedRotation) = _definition.PlacedInside(Interior);
        _scale = _definition.Scale;
        Revision++;
    }

    public MapEntity? Parent { get; internal set; }

    public IReadOnlyList<MapEntity> Children => _children ?? (IReadOnlyList<MapEntity>)[];
    private List<MapEntity>? _children;

    internal void AddChild(MapEntity child)
    {
        (_children ??= new List<MapEntity>()).Add(child);
        child.Parent = this;
    }

    public Quaternion Orientation => IsInterior ? StoredRotation : Quaternion.Inverse(StoredRotation);

    public Matrix4x4 Transform =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Orientation) * Matrix4x4.CreateTranslation(Position);
}

// An entity set of one interior instance: its objects placed in the world, and whether it is shown. Hidden by
// default, as in the game until a script activates it.
public sealed class PlacedEntitySet
{
    public required uint NameHash { get; init; }

    public required MapEntity Interior { get; init; }

    public IReadOnlyList<MapEntity> Entities { get; internal set; } = [];

    public bool IsActive { get; internal set; }
}

// Level of detail of an entity, from the most detailed (HD) to the coarsest (SLOD4).
public enum LodLevel
{
    Hd = 0,
    Lod = 1,
    Slod1 = 2,
    Slod2 = 3,
    Slod3 = 4,
    OrphanHd = 5,
    Slod4 = 6,
}

public static class LodLevels
{
    public static int Coarseness(this LodLevel level) => level switch
    {
        LodLevel.OrphanHd => 0,
        LodLevel.Slod4 => 5,
        _ => (int)level,
    };
}

// Decodes a map data file (.ymap).
public interface IMapDataReader
{
    // Parses the decompressed content of a .ymap (as returned by IGameFileSystem.ReadFile).
    MapData Read(ReadOnlySpan<byte> data);
}

// Boxes of a map file: where its objects are, and from where the game must load it (streaming).
public readonly record struct MapExtents(Vector3 StreamingMin, Vector3 StreamingMax, Vector3 EntitiesMin, Vector3 EntitiesMax)
{
    public static MapExtents Of(MapData map) =>
        new(map.StreamingExtentsMin, map.StreamingExtentsMax, map.EntitiesExtentsMin, map.EntitiesExtentsMax);
}

// Decompressed content of a resource file to write, and the system page flags of its header.
public readonly record struct MapFileContent(byte[] Content, uint SystemPageFlags);

public interface IMapDataWriter
{
    // Builds the decompressed content of a .ymap from source, the file map was read from (the game's, or
    // SavedContent): placement of the entities, deleted entities left out, links to lower-detail parents and boxes of
    // the map updated; everything else is kept.
    MapFileContent Write(ReadOnlySpan<byte> source, MapData map, MapExtents extents);
}
