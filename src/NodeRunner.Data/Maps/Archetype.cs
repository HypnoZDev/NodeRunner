using System.Numerics;

namespace NodeRunner.Data.Maps;

// A type of object (defined in a .ytyp file): its model, textures, size and draw distance. Every placed MapEntity
// refers to one archetype by name hash.
public sealed class Archetype
{
    public required uint NameHash { get; init; }

    public ArchetypeKind Kind { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public Vector3 SphereCenter { get; init; }
    public float SphereRadius { get; init; }

    public float LodDistance { get; init; }
    public uint Flags { get; init; }

    // Stand-in model the game only uses for shadows or reflections (flag 0x800: "shadowbox", "shadprox", "reflect"
    // models, e.g. closed boxes along the metro tunnels). Never drawn in the normal view.
    public bool IsShadowOrReflectionProxy => (Flags & 0x800) != 0;

    public uint TimeFlags { get; init; }

    public bool IsVisibleAt(int hour) => Kind != ArchetypeKind.Time || (TimeFlags & (1u << Math.Clamp(hour, 0, 23))) != 0;

    public uint TextureDictionaryHash { get; init; }

    public uint DrawableDictionaryHash { get; init; }

    public uint PhysicsDictionaryHash { get; init; }

    public ArchetypeAssetType AssetType { get; init; }

    public uint AssetNameHash { get; init; }

    // For an interior: the objects it contains (walls, furniture...), placed relative to the interior instance.
    public IReadOnlyList<MapEntity> InteriorEntities { get => _interiorEntities; init => _interiorEntities = value; }
    private IReadOnlyList<MapEntity> _interiorEntities = [];

    // A new list, not changed in place: background threads may be going through the old one.
    internal void AddInteriorEntity(MapEntity entity) => _interiorEntities = [.. _interiorEntities, entity];

    public IReadOnlyList<InteriorRoom> InteriorRooms { get; init; } = [];

    public IReadOnlyList<InteriorPortal> InteriorPortals { get; init; } = [];

    // For an interior: optional groups of objects (furniture styles, upgrades of Online properties...), hidden until
    // a script activates them.
    public IReadOnlyList<InteriorEntitySet> InteriorEntitySets { get; init; } = [];
}

// An optional group of objects of an interior, placed relative to it (each with its room).
public sealed class InteriorEntitySet(uint nameHash, IReadOnlyList<MapEntity> entities)
{
    public uint NameHash { get; } = nameHash;

    public IReadOnlyList<MapEntity> Entities { get; private set; } = entities;

    // A new list, not changed in place: background threads may be going through the old one.
    internal void AddEntity(MapEntity entity) => Entities = [.. Entities, entity];
}

// A room of an interior: its box, in the interior's space.
public sealed record InteriorRoom(Vector3 BoundsMin, Vector3 BoundsMax)
{
    public string Name { get; init; } = "";

    public uint Flags { get; init; }

    // The real outside is not drawn from this room (flag 0x100): the room holds its own simplified view of the
    // surroundings, seen through the windows (e.g. the psychiatrist's office, v_psycheoffice).
    public bool HidesExterior => (Flags & 0x100) != 0;

    public int ExteriorVisibilityDepth { get; init; }
}

// An opening between two rooms of an interior (room 0 being the outside).
public sealed record InteriorPortal(int RoomFrom, int RoomTo, uint Flags)
{
    public IReadOnlyList<Vector3> Corners { get; init; } = [];
}

public enum ArchetypeKind
{
    Base,

    Time,

    Interior,
}

public enum ArchetypeAssetType
{
    Uninitialized = 0,
    Fragment = 1,
    Drawable = 2,
    DrawableDictionary = 3,
    Assetless = 4,
}

public sealed class MapTypes
{
    public required uint NameHash { get; init; }

    public required IReadOnlyList<Archetype> Archetypes { get; init; }
}

// Decodes an archetype definition file (.ytyp).
public interface IMapTypesReader
{
    MapTypes Read(ReadOnlySpan<byte> data);
}

public interface IMapTypesWriter
{
    // Builds the decompressed content of a .ytyp from source (the file the interiors were read from): placement of
    // the objects of each interior and of its entity sets; everything else is kept.
    MapFileContent Write(ReadOnlySpan<byte> source, IReadOnlyList<Archetype> interiors);
}
