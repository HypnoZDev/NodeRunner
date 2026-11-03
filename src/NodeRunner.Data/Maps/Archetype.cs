using System.Numerics;

namespace NodeRunner.Data.Maps;

/// <summary>
/// A type of object (defined in a .ytyp file): its model, textures, size and draw distance.
/// Every placed <see cref="MapEntity"/> refers to one archetype by name hash.
/// </summary>
public sealed class Archetype
{
    public required uint NameHash { get; init; }

    public ArchetypeKind Kind { get; init; }

    /// <summary>Bounding box of the model, in model space.</summary>
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public Vector3 SphereCenter { get; init; }
    public float SphereRadius { get; init; }

    public float LodDistance { get; init; }
    public uint Flags { get; init; }

    /// <summary>
    /// Stand-in model the game only uses for shadows or reflections (flag 0x800: "shadowbox", "shadprox",
    /// "reflect" models, e.g. closed boxes along the metro tunnels). Never drawn in the normal view.
    /// </summary>
    public bool IsShadowOrReflectionProxy => (Flags & 0x800) != 0;

    /// <summary>For a <see cref="ArchetypeKind.Time"/> archetype: bit N set = visible during hour N (0 to 23).</summary>
    public uint TimeFlags { get; init; }

    /// <summary>False for a time archetype hidden at this hour (e.g. lit windows by day).</summary>
    public bool IsVisibleAt(int hour) => Kind != ArchetypeKind.Time || (TimeFlags & (1u << Math.Clamp(hour, 0, 23))) != 0;

    /// <summary>Texture dictionary (.ytd) hash, 0 if none.</summary>
    public uint TextureDictionaryHash { get; init; }

    /// <summary>Drawable dictionary (.ydd) hash when the model is stored in one, 0 otherwise.</summary>
    public uint DrawableDictionaryHash { get; init; }

    public uint PhysicsDictionaryHash { get; init; }

    public ArchetypeAssetType AssetType { get; init; }

    /// <summary>Name hash of the model file (.ydr / .yft) or of the entry in the drawable dictionary.</summary>
    public uint AssetNameHash { get; init; }

    /// <summary>
    /// For an interior: the objects it contains (walls, furniture...), placed relative to the interior instance.
    /// Empty for other archetypes. Optional entity sets (furniture variants switched by scripts) are in <see cref="InteriorEntitySets"/>.
    /// </summary>
    public IReadOnlyList<MapEntity> InteriorEntities { get; init; } = [];

    /// <summary>For an interior: its rooms (room 0, "limbo", stands for the outside). Empty otherwise.</summary>
    public IReadOnlyList<InteriorRoom> InteriorRooms { get; init; } = [];

    /// <summary>For an interior: the openings between its rooms, or to the outside. Empty otherwise.</summary>
    public IReadOnlyList<InteriorPortal> InteriorPortals { get; init; } = [];

    /// <summary>
    /// For an interior: optional groups of objects (furniture styles, upgrades of Online properties...), hidden
    /// until a script activates them. Empty otherwise.
    /// </summary>
    public IReadOnlyList<InteriorEntitySet> InteriorEntitySets { get; init; } = [];
}

/// <summary>An optional group of objects of an interior, placed relative to it (each with its room).</summary>
public sealed record InteriorEntitySet(uint NameHash, IReadOnlyList<MapEntity> Entities);

/// <summary>A room of an interior: its box, in the interior's space.</summary>
public sealed record InteriorRoom(Vector3 BoundsMin, Vector3 BoundsMax)
{
    /// <summary>Name given by the level designers (e.g. "limbo", "MichaelBedroom"), "" if none.</summary>
    public string Name { get; init; } = "";

    /// <summary>Room flags of the game (CMloRoomDef.flags).</summary>
    public uint Flags { get; init; }

    /// <summary>
    /// The real outside is not drawn from this room (flag 0x100): the room holds its own simplified view of the
    /// surroundings, seen through the windows (e.g. the psychiatrist's office, v_psycheoffice).
    /// </summary>
    public bool HidesExterior => (Flags & 0x100) != 0;

    /// <summary>How many portals deep the outside can be seen from this room (CMloRoomDef.exteriorVisibiltyDepth).</summary>
    public int ExteriorVisibilityDepth { get; init; }
}

/// <summary>An opening between two rooms of an interior (room 0 being the outside).</summary>
public sealed record InteriorPortal(int RoomFrom, int RoomTo, uint Flags)
{
    /// <summary>Corners of the opening, in the interior's space.</summary>
    public IReadOnlyList<Vector3> Corners { get; init; } = [];
}

public enum ArchetypeKind
{
    Base,

    /// <summary>Only visible at certain hours (e.g. lit windows at night).</summary>
    Time,

    /// <summary>Interior ("MLO"): a container of rooms and entities.</summary>
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

/// <summary>The content of one .ytyp file.</summary>
public sealed class MapTypes
{
    public required uint NameHash { get; init; }

    public required IReadOnlyList<Archetype> Archetypes { get; init; }
}

/// <summary>Decodes an archetype definition file (.ytyp).</summary>
public interface IMapTypesReader
{
    /// <exception cref="InvalidDataException">The data is not a valid .ytyp file.</exception>
    MapTypes Read(ReadOnlySpan<byte> data);
}
