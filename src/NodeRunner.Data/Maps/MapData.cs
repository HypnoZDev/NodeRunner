using System.Numerics;

namespace NodeRunner.Data.Maps;

/// <summary>
/// The content of one map data file (.ymap): a list of placed objects ("entities") and the area they cover.
/// Only entities are read for now; occluders, car generators, LOD lights, etc. come later.
/// </summary>
public sealed class MapData
{
    public required uint NameHash { get; init; }

    /// <summary>Hash of the parent ymap (holding the lower-detail versions of these entities), 0 if none.</summary>
    public uint ParentHash { get; init; }

    public uint Flags { get; init; }
    public uint ContentFlags { get; init; }

    /// <summary>
    /// Loaded only when a script asks for it (flag 1): prologue, Cayo Perico, mission states, time-of-day
    /// variants, special reflection models... Not part of the default map.
    /// </summary>
    public bool IsScripted => (Flags & 1) != 0;

    /// <summary>Area in which the game loads this file.</summary>
    public Vector3 StreamingExtentsMin { get; init; }
    public Vector3 StreamingExtentsMax { get; init; }

    /// <summary>Box containing every entity of the file.</summary>
    public Vector3 EntitiesExtentsMin { get; init; }
    public Vector3 EntitiesExtentsMax { get; init; }

    /// <summary>Entities in file order; objects added in the editor come last (see <see cref="MapLibrary.AddEntity"/>).</summary>
    public required IReadOnlyList<MapEntity> Entities { get => _entities; init => _entities = value.ToList(); }
    private List<MapEntity> _entities = [];

    internal void AddEntity(MapEntity entity) => _entities.Add(entity);

    /// <summary>
    /// Path of the file in the game archives, once loaded by <see cref="MapLibrary"/>; for a <see cref="IsCustom"/> map,
    /// its file name only.
    /// </summary>
    public string? FilePath { get; internal set; }

    /// <summary>
    /// A map that is not a game file: made by the user (the project's map of added objects, see
    /// <see cref="MapLibrary.CreateCustomMap"/>) or taken from the save folder without a game file of that name.
    /// It is added to the game's map instead of replacing a file of it.
    /// </summary>
    public bool IsCustom { get; internal set; }

    /// <summary>
    /// Loaded from the user's save folder (a version saved by NodeRunner) instead of the game's file (see
    /// <see cref="SavedContent"/>), or saved since; <see cref="FilePath"/> still names the game's file.
    /// </summary>
    public bool IsSaved { get; set; }

    /// <summary>
    /// For a map loaded from the user's save folder: the file's decompressed content, which saving starts from
    /// (its entities' <see cref="MapEntity.SourceIndex"/> refer to it). Null for a map read from the game.
    /// </summary>
    public byte[]? SavedContent { get; internal set; }

    /// <summary>Entities still on the map (not deleted), in file order: what a saved file contains.</summary>
    public IEnumerable<MapEntity> KeptEntities => Entities.Where(e => !e.IsDeleted);

    /// <summary>Index of an entity among <see cref="KeptEntities"/> (its index in a saved file), -1 if deleted or not in this map.</summary>
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

/// <summary>An object placed on the map: which model (archetype), where, how it is turned and scaled.</summary>
public sealed class MapEntity
{
    public required uint ArchetypeHash { get; init; }

    public required Vector3 Position { get => _position; init => _position = value; }
    private Vector3 _position;

    /// <summary>
    /// Rotation exactly as stored in the file (see <see cref="Orientation"/>); for an object inside an
    /// interior, already combined with the interior's rotation.
    /// </summary>
    public Quaternion StoredRotation { get => _storedRotation; init => _storedRotation = value; }
    private Quaternion _storedRotation = Quaternion.Identity;

    /// <summary>Horizontal (X and Y) and vertical (Z) scale.</summary>
    public Vector3 Scale { get => _scale; init => _scale = value; }
    private Vector3 _scale = Vector3.One;

    /// <summary>
    /// Incremented each time the object is moved, turned or scaled (see <see cref="SetPlacement"/>), so that
    /// what was computed from its placement (draw transform, rotation angles shown...) can be refreshed.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// False for the objects inside an interior: they are defined by the interior's archetype (.ytyp), not
    /// by a map file, and move with the interior instance.
    /// </summary>
    public bool CanEdit => Interior is null;

    /// <summary>
    /// Moves, turns and scales the object (<paramref name="orientation"/> is the world orientation, as
    /// <see cref="Orientation"/>). For an interior instance, the objects inside follow.
    /// <para>The map's spatial index is not updated: an object moved by more than a few hundred metres may
    /// not be found by the camera's surroundings anymore.</para>
    /// </summary>
    public void SetPlacement(Vector3 position, Quaternion orientation, Vector3 scale)
    {
        if (!CanEdit)
            throw new InvalidOperationException("Objects inside an interior move with the interior instance.");

        _position = position;
        _storedRotation = IsInterior ? orientation : Quaternion.Inverse(orientation);
        _scale = scale;
        Revision++;
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

    /// <summary>Position of the entity in the file it was read from (see <see cref="MapData.SavedContent"/>), -1 if added by the editor.</summary>
    public int SourceIndex { get; init; } = -1;

    /// <summary>Deleted in the editor (see <see cref="MapLibrary.SetDeleted"/>): no longer drawn, left out of saved files.</summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>
    /// Index of the lower-detail entity, -1 if none: in the parent ymap if <see cref="LodInParentMap"/>,
    /// otherwise in the same ymap.
    /// </summary>
    public int ParentIndex { get; init; } = -1;

    /// <summary>The lower-detail entity is in the parent ymap rather than in the same one (flag 8).</summary>
    public bool LodInParentMap => (Flags & 8) != 0;

    // Render flags: only drawn in shadows, in reflections, in water reflections, in mirrors.
    private const uint OnlyInShadowsOrReflections = 0x800000 | 0x2000000 | 0x8000000 | 0x20000000;

    /// <summary>
    /// False for stand-ins the game only draws in shadows or reflections (e.g. closed boxes standing for a
    /// metro tunnel in reflections): they must not be drawn in the normal view.
    /// </summary>
    public bool IsDrawnInMainView => (Flags & OnlyInShadowsOrReflections) == 0;

    /// <summary>Distance up to which this entity is drawn.</summary>
    public float LodDistance { get; init; }

    /// <summary>Distance up to which the higher-detail children replace this entity.</summary>
    public float ChildLodDistance { get; init; }

    public LodLevel LodLevel { get; init; }
    public int ChildCount { get; init; }

    /// <summary>True for interior instances (MLO), whose content comes from the archetype.</summary>
    public bool IsInterior { get; init; }

    /// <summary>For an interior instance: the objects inside it, placed in the world (empty otherwise).</summary>
    public IReadOnlyList<MapEntity> InteriorEntities { get; internal set; } = [];

    /// <summary>For an interior instance: its optional groups of objects, placed in the world (empty otherwise).</summary>
    public IReadOnlyList<PlacedEntitySet> EntitySets { get; internal set; } = [];

    /// <summary>For an object of an entity set: the set (null otherwise).</summary>
    public PlacedEntitySet? EntitySet { get; internal set; }

    /// <summary>For an object inside an interior: the interior instance holding it (null otherwise).</summary>
    public MapEntity? Interior { get; private init; }

    /// <summary>For an object inside an interior: the object as defined by the interior's archetype, relative to it.</summary>
    private MapEntity? _definition;

    /// <summary>The map file placing this entity (for an object inside an interior: the one placing the interior).</summary>
    public MapData? Map { get; internal set; }

    /// <summary>For an object inside an interior: index of its room (0 = "limbo", the outside), -1 if none.</summary>
    public int InteriorRoom { get; init; } = -1;

    /// <summary>For an object inside an interior: index of the portal it is attached to (doors...), -1 if none.</summary>
    public int InteriorPortal { get; init; } = -1;

    /// <summary>
    /// For an object inside an interior: a simplified view of the surroundings, only seen through the windows from
    /// inside (see <see cref="MapLibrary"/>). Never drawn: the real surroundings are.
    /// </summary>
    public bool IsWindowView { get; internal set; }

    /// <summary>
    /// Copy of this object (placed relative to an interior, as in the interior's archetype) moved into the
    /// world by the interior <paramref name="instance"/>: its rotation and position apply on top of this one's.
    /// </summary>
    internal MapEntity PlaceInside(MapEntity instance)
    {
        var (position, storedRotation) = PlacedInside(instance);
        return new MapEntity
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
    }

    private (Vector3 Position, Quaternion StoredRotation) PlacedInside(MapEntity instance) =>
        (instance.Position + Vector3.Transform(Position, instance.Orientation),
         Quaternion.Inverse(instance.Orientation * Orientation));

    /// <summary>Places this object inside an interior again, after the interior instance was moved.</summary>
    private void FollowInterior()
    {
        if (Interior is null || _definition is null)
            return;
        (_position, _storedRotation) = _definition.PlacedInside(Interior);
        Revision++;
    }

    /// <summary>The lower-detail entity that replaces this one from far away, once the LOD tree is linked.</summary>
    public MapEntity? Parent { get; internal set; }

    /// <summary>The higher-detail entities that replace this one up close (empty if none).</summary>
    public IReadOnlyList<MapEntity> Children => _children ?? (IReadOnlyList<MapEntity>)[];
    private List<MapEntity>? _children;

    internal void AddChild(MapEntity child)
    {
        (_children ??= new List<MapEntity>()).Add(child);
        child.Parent = this;
    }

    /// <summary>
    /// World orientation. Ordinary entities store the inverse rotation; interior instances store it directly.
    /// </summary>
    public Quaternion Orientation => IsInterior ? StoredRotation : Quaternion.Inverse(StoredRotation);

    /// <summary>Model-to-world transform: scale, then rotation, then translation.</summary>
    public Matrix4x4 Transform =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Orientation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>
/// An entity set of one interior instance (see <see cref="InteriorEntitySet"/>): its objects placed in the world,
/// and whether it is shown. Hidden by default, as in the game until a script activates it.
/// </summary>
public sealed class PlacedEntitySet
{
    public required uint NameHash { get; init; }

    /// <summary>The interior instance holding the set.</summary>
    public required MapEntity Interior { get; init; }

    public IReadOnlyList<MapEntity> Entities { get; internal set; } = [];

    /// <summary>Shown (see <see cref="MapLibrary.SetEntitySetActive"/>).</summary>
    public bool IsActive { get; internal set; }
}

/// <summary>Level of detail of an entity, from the most detailed (HD) to the coarsest (SLOD4).</summary>
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
    /// <summary>Rank from the most detailed (0: HD and OrphanHd) to the coarsest (5: SLOD4).</summary>
    public static int Coarseness(this LodLevel level) => level switch
    {
        LodLevel.OrphanHd => 0,
        LodLevel.Slod4 => 5,
        _ => (int)level,
    };
}

/// <summary>Decodes a map data file (.ymap).</summary>
public interface IMapDataReader
{
    /// <summary>Parses the decompressed content of a .ymap (as returned by IGameFileSystem.ReadFile).</summary>
    /// <exception cref="InvalidDataException">The data is not a valid map data file.</exception>
    MapData Read(ReadOnlySpan<byte> data);
}

/// <summary>Boxes of a map file: where its objects are, and from where the game must load it (streaming).</summary>
public readonly record struct MapExtents(Vector3 StreamingMin, Vector3 StreamingMax, Vector3 EntitiesMin, Vector3 EntitiesMax)
{
    public static MapExtents Of(MapData map) =>
        new(map.StreamingExtentsMin, map.StreamingExtentsMax, map.EntitiesExtentsMin, map.EntitiesExtentsMax);
}

/// <summary>Decompressed content of a resource file to write, and the system page flags of its header.</summary>
public readonly record struct MapFileContent(byte[] Content, uint SystemPageFlags);

public interface IMapDataWriter
{
    /// <summary>
    /// Builds the decompressed content of a .ymap from <paramref name="source"/>, the file <paramref name="map"/> was
    /// read from (the game's, or <see cref="MapData.SavedContent"/>): placement of the entities, deleted entities left
    /// out, links to lower-detail parents and boxes of the map updated; everything else is kept.
    /// </summary>
    /// <exception cref="InvalidDataException">The data does not match the map (other file or other entities).</exception>
    MapFileContent Write(ReadOnlySpan<byte> source, MapData map, MapExtents extents);
}
