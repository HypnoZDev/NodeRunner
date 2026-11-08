using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A new interior (MLO) made by the user: its archetype goes in a .ytyp of the project named after it, an instance of
// it in the project's map. It starts with limbo, one room and a door to the outside.
public static class InteriorCreation
{
    public const float DefaultLodDistance = 150f;

    // Most interior instances of the game (lodLevel ORPHANHD).
    public const uint InstanceFlags = 0x180000;

    public static readonly Vector3 RoomMin = new(-3f, -3f, -0.5f);
    public static readonly Vector3 RoomMax = new(3f, 3f, 3.5f);

    // Names usable as a file name and an archetype name: lower-case letters, digits and '_', starting with a letter,
    // at most 48 characters.
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 48 && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static string ArchetypeFile(string name) => $@"{Data.FileSystem.GameContent.LooseFilesRoot}\{name}.ytyp";

    // The first free name "<prefix>_int", "<prefix>_int2"... (or with another suffix).
    public static string SuggestName(string prefix, Func<string, bool> isFree, string suffix = "_int")
    {
        string root = string.Concat(prefix.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'));
        root = root.Length > 0 && char.IsAsciiLetterLower(root[0]) ? root : "mlo_" + root;
        root = root[..Math.Min(root.Length, 48 - suffix.Length - 2)].TrimEnd('_') + suffix;
        for (int i = 1; ; i++)
        {
            string name = i == 1 ? root : root + i;
            if (isFree(name))
                return name;
        }
    }

    public static Archetype NewArchetype(string name)
    {
        uint hash = Data.Hashing.Joaat.Hash(name);
        var limbo = new InteriorRoom(new Vector3(float.MaxValue), new Vector3(float.MinValue))
        {
            Name = "limbo",
            Flags = DefaultRoomFlags,
            SourceIndex = 0,
        };
        var room = new InteriorRoom(RoomMin, RoomMax) { Name = "room1", Flags = DefaultRoomFlags };
        // A door of 1.2 x 2.2 m in the middle of the side -Y, to the outside.
        float y = RoomMin.Y, bottom = 0f, top = 2.2f;
        var door = new InteriorPortal(1, 0, 0) { Corners = [new(-0.6f, y, top), new(0.6f, y, top), new(0.6f, y, bottom), new(-0.6f, y, bottom)] };
        return new Archetype
        {
            NameHash = hash,
            Kind = ArchetypeKind.Interior,
            LodDistance = DefaultLodDistance,
            AssetType = ArchetypeAssetType.Assetless,
            AssetNameHash = hash,
            IsCreated = true,
            InteriorRooms = [limbo, room],
            InteriorPortals = [door],
        };
    }

    // Limbo of every game interior, and most of their rooms.
    public const uint DefaultRoomFlags = 0x60;

    public static MapEntity NewInstance(uint archetypeHash, Vector3 position, Quaternion orientation, float lodDistance = DefaultLodDistance) => new()
    {
        ArchetypeHash = archetypeHash,
        Position = position,
        StoredRotation = orientation, // Interior instances keep their world orientation as it is.
        Flags = InstanceFlags,
        Guid = (uint)Random.Shared.Next(1, int.MaxValue),
        LodDistance = lodDistance,
        LodLevel = LodLevel.OrphanHd,
        IsInterior = true,
    };

    // Draw distance of a new instance of archetype (as most game instances: that of their archetype).
    public static float InstanceLodDistance(Archetype archetype) =>
        archetype.LodDistance > 0f ? Math.Min(archetype.LodDistance, 10000f) : DefaultLodDistance;

    // Turned about Z so that its side -Y (the door) faces a camera looking along forward.
    public static Quaternion FacingAway(Vector3 forward) =>
        forward.X == 0f && forward.Y == 0f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(-forward.X, forward.Y));
}

// A new interior placed in a map (InteriorCreation): undone by deleting its instance (its archetype stays known,
// unused), redone by bringing it back.
public sealed class InteriorCreateEdit : IEdit, IInteriorEdit
{
    private readonly MapLibrary _library;

    private InteriorCreateEdit(MapLibrary library, string name, MapEntity instance, string description)
    {
        _library = library;
        Name = name;
        Interior = instance;
        Description = description;
    }

    public string Name { get; }

    public MapEntity Interior { get; }

    public string Description { get; }
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [Interior];
    public IReadOnlyList<MapEntity> Selection => [Interior];

    public static InteriorCreateEdit Apply(MapLibrary library, MapData map, string name, Vector3 position, Quaternion orientation, string description)
    {
        if (!InteriorCreation.IsValidName(name))
            throw new ArgumentException("Invalid interior name.", nameof(name));
        var archetype = InteriorCreation.NewArchetype(name);
        library.AddInteriorArchetype(archetype, InteriorCreation.ArchetypeFile(name));
        var instance = InteriorCreation.NewInstance(archetype.NameHash, position, orientation);
        library.AddEntity(map, instance);
        return new InteriorCreateEdit(library, name, instance, description);
    }

    public void Undo() => _library.SetDeleted([Interior], true);
    public void Redo() => _library.SetDeleted([Interior], false);
}

// A copy of an interior under a new name (MapLibrary.DuplicateInterior), placed in a map: undone by deleting its
// instance (its archetype stays known, unused), redone by bringing it back.
public sealed class InteriorDuplicateEdit : IEdit, IInteriorEdit
{
    private readonly MapLibrary _library;

    private InteriorDuplicateEdit(MapLibrary library, uint sourceHash, string name, MapEntity instance, string description)
    {
        _library = library;
        SourceHash = sourceHash;
        Name = name;
        Interior = instance;
        Description = description;
    }

    public uint SourceHash { get; }

    public string Name { get; }

    public MapEntity Interior { get; }

    public string Description { get; }
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [Interior];
    public IReadOnlyList<MapEntity> Selection => [Interior];

    public static InteriorDuplicateEdit Apply(MapLibrary library, MapData map, uint sourceHash, string name, Vector3 position, Quaternion orientation,
        string description)
    {
        if (!InteriorCreation.IsValidName(name))
            throw new ArgumentException("Invalid interior name.", nameof(name));
        var archetype = library.DuplicateInterior(sourceHash, name, InteriorCreation.ArchetypeFile(name));
        var instance = InteriorCreation.NewInstance(archetype.NameHash, position, orientation, InteriorCreation.InstanceLodDistance(archetype));
        library.AddEntity(map, instance);
        return new InteriorDuplicateEdit(library, sourceHash, name, instance, description);
    }

    public void Undo() => _library.SetDeleted([Interior], true);
    public void Redo() => _library.SetDeleted([Interior], false);
}
