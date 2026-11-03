using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

/// <summary>
/// NodeRunner's own reader for archetype definition files (binary Meta .ytyp, root structure CMapTypes).
/// Layout documented by the GTA V modding community (CodeWalker was used as a reference to understand it).
/// Reads the common part of every archetype (time and interior archetypes start with the same fields)
/// and the content of interiors (objects, rooms, portals, entity sets).
/// </summary>
public sealed class YtypReader : IMapTypesReader
{
    // Structure name hashes as they appear in the files.
    private const uint MapTypesStructure = 3649811809;
    private const uint BaseArchetypeStructure = 2195127427;
    private const uint TimeArchetypeStructure = 1991296364;
    private const uint InteriorArchetypeStructure = 273704021;

    private const int BaseArchetypeSize = 144;
    private const int OldBaseArchetypeSize = 128; // Older layout without asset type and asset name.
    private const int InteriorArchetypeSize = 240; // A base archetype followed by interior-specific fields.

    // CMapTypes fields.
    private const int TypesArchetypes = 24;
    private const int TypesName = 40;

    // CBaseArchetypeDef fields.
    private const int ArchLodDistance = 8;
    private const int ArchFlags = 12;
    private const int ArchBoundsMin = 32;
    private const int ArchBoundsMax = 48;
    private const int ArchSphereCenter = 64;
    private const int ArchSphereRadius = 80;
    private const int ArchName = 88;
    private const int ArchTextureDictionary = 92;
    private const int ArchDrawableDictionary = 100;
    private const int ArchPhysicsDictionary = 104;
    private const int ArchAssetType = 108;
    private const int ArchAssetName = 112;

    // CMloArchetypeDef fields.
    private const int InteriorEntities = 152;
    private const int InteriorRooms = 168;
    private const int InteriorPortals = 184;
    private const int InteriorEntitySets = 200;

    // CMloRoomDef and CMloPortalDef.
    private const uint RoomStructure = 186126833;
    private const uint PortalStructure = 2572186314;
    private const int RoomSize = 112;
    private const int PortalSize = 64;
    private const int RoomName = 8;
    private const int RoomBoundsMin = 32;
    private const int RoomBoundsMax = 48;
    private const int RoomFlags = 76;
    private const int RoomExteriorVisibilityDepth = 88;
    private const int RoomAttachedObjects = 96;
    private const int PortalRoomFrom = 8;
    private const int PortalRoomTo = 12;
    private const int PortalFlags = 16;
    private const int PortalCorners = 32;
    private const int PortalAttachedObjects = 48;

    // CMloEntitySet: name, room of each object, objects.
    private const uint EntitySetStructure = 3601308153;
    private const int EntitySetSize = 48;
    private const int EntitySetName = 8;
    private const int EntitySetLocations = 16;
    private const int EntitySetEntities = 32;

    public MapTypes Read(ReadOnlySpan<byte> data)
    {
        var meta = new MetaResource(data);
        int root = meta.RootBlock;
        if (root < 0 || meta.BlockStructure(root) != MapTypesStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de types (.ytyp).");

        int types = meta.BlockStart(root);
        int baseSize = meta.StructureSize(BaseArchetypeStructure);
        if (baseSize != -1 && baseSize != BaseArchetypeSize && baseSize != OldBaseArchetypeSize)
            throw new InvalidDataException($"Structure CBaseArchetypeDef inattendue (taille {baseSize}).");
        bool oldLayout = baseSize == OldBaseArchetypeSize;

        var archetypes = new List<Archetype>();
        foreach (var (offset, structure) in meta.ReadPointerArray(types + TypesArchetypes))
        {
            var kind = structure switch
            {
                BaseArchetypeStructure => ArchetypeKind.Base,
                TimeArchetypeStructure => ArchetypeKind.Time,
                InteriorArchetypeStructure => ArchetypeKind.Interior,
                _ => (ArchetypeKind?)null,
            };
            if (kind is null)
                continue;
            var interior = kind == ArchetypeKind.Interior ? ReadInterior(meta, offset) : InteriorContent.None;
            archetypes.Add(ReadArchetype(meta, offset, kind.Value, oldLayout && kind == ArchetypeKind.Base, interior));
        }

        return new MapTypes { NameHash = meta.Data.UInt32(types + TypesName), Archetypes = archetypes };
    }

    private sealed record InteriorContent(List<MapEntity> Entities, List<InteriorRoom> Rooms, List<InteriorPortal> Portals, List<InteriorEntitySet> EntitySets)
    {
        public static readonly InteriorContent None = new([], [], [], []);
    }

    /// <summary>The objects of an interior, each with the room or portal it belongs to, and the rooms and portals.</summary>
    private static InteriorContent ReadInterior(MetaResource meta, int o)
    {
        meta.RequireStructure(InteriorArchetypeStructure, InteriorArchetypeSize, "CMloArchetypeDef");
        var d = meta.Data;
        var pointers = meta.ReadPointerArray(o + InteriorEntities);

        // Rooms and portals list the indices of their objects.
        var roomOf = new int[pointers.Count];
        var portalOf = new int[pointers.Count];
        Array.Fill(roomOf, -1);
        Array.Fill(portalOf, -1);

        var rooms = new List<InteriorRoom>();
        var (roomStart, roomCount) = meta.ReadArray(o + InteriorRooms, RoomSize);
        if (roomCount > 0)
            meta.RequireStructure(RoomStructure, RoomSize, "CMloRoomDef");
        for (int i = 0; i < roomCount; i++)
        {
            int r = roomStart + i * RoomSize;
            rooms.Add(new InteriorRoom(meta.Vector3(r + RoomBoundsMin), meta.Vector3(r + RoomBoundsMax))
            {
                Name = meta.String(r + RoomName),
                Flags = d.UInt32(r + RoomFlags),
                ExteriorVisibilityDepth = (int)d.UInt32(r + RoomExteriorVisibilityDepth),
            });
            AttachObjects(meta, r + RoomAttachedObjects, roomOf, i);
        }

        var portals = new List<InteriorPortal>();
        var (portalStart, portalCount) = meta.ReadArray(o + InteriorPortals, PortalSize);
        if (portalCount > 0)
            meta.RequireStructure(PortalStructure, PortalSize, "CMloPortalDef");
        for (int i = 0; i < portalCount; i++)
        {
            int p = portalStart + i * PortalSize;
            var (cornerStart, cornerCount) = meta.ReadArray(p + PortalCorners, 16);
            var corners = new Vector3[cornerCount];
            for (int c = 0; c < cornerCount; c++)
                corners[c] = meta.Vector3(cornerStart + c * 16);
            portals.Add(new InteriorPortal((int)d.UInt32(p + PortalRoomFrom), (int)d.UInt32(p + PortalRoomTo), d.UInt32(p + PortalFlags))
            {
                Corners = corners,
            });
            AttachObjects(meta, p + PortalAttachedObjects, portalOf, i);
        }

        var entities = new List<MapEntity>();
        for (int i = 0; i < pointers.Count; i++)
        {
            var (offset, structure) = pointers[i];
            if (structure == YmapReader.EntityStructure)
                entities.Add(YmapReader.ReadEntity(meta, offset, isInterior: false, roomOf[i], portalOf[i]));
        }
        if (entities.Count > 0)
            meta.RequireStructure(YmapReader.EntityStructure, YmapReader.EntitySize, "CEntityDef");
        return new InteriorContent(entities, rooms, portals, ReadEntitySets(meta, o));
    }

    /// <summary>
    /// Optional groups of objects of an interior (furniture styles, upgrades...), shown when a script activates them.
    /// Each object comes with the index of its room.
    /// </summary>
    private static List<InteriorEntitySet> ReadEntitySets(MetaResource meta, int o)
    {
        var sets = new List<InteriorEntitySet>();
        var (setStart, setCount) = meta.ReadArray(o + InteriorEntitySets, EntitySetSize);
        if (setCount > 0)
            meta.RequireStructure(EntitySetStructure, EntitySetSize, "CMloEntitySet");
        for (int i = 0; i < setCount; i++)
        {
            int s = setStart + i * EntitySetSize;
            var (locationStart, locationCount) = meta.ReadArray(s + EntitySetLocations, 4);
            var entities = new List<MapEntity>();
            var pointers = meta.ReadPointerArray(s + EntitySetEntities);
            for (int e = 0; e < pointers.Count; e++)
            {
                var (offset, structure) = pointers[e];
                if (structure != YmapReader.EntityStructure)
                    continue;
                int room = e < locationCount ? (int)meta.Data.UInt32(locationStart + e * 4) : -1;
                entities.Add(YmapReader.ReadEntity(meta, offset, isInterior: false, room, -1));
            }
            if (entities.Count > 0)
                meta.RequireStructure(YmapReader.EntityStructure, YmapReader.EntitySize, "CEntityDef");
            sets.Add(new InteriorEntitySet(meta.Data.UInt32(s + EntitySetName), entities));
        }
        return sets;
    }

    private static void AttachObjects(MetaResource meta, int arrayField, int[] owners, int owner)
    {
        var (start, count) = meta.ReadArray(arrayField, 4);
        for (int i = 0; i < count; i++)
        {
            uint index = meta.Data.UInt32(start + i * 4);
            if (index < owners.Length)
                owners[index] = owner;
        }
    }

    private static Archetype ReadArchetype(MetaResource meta, int o, ArchetypeKind kind, bool oldLayout, InteriorContent interior)
    {
        var d = meta.Data;
        uint name = d.UInt32(o + ArchName);
        uint drawableDictionary = d.UInt32(o + ArchDrawableDictionary);
        return new Archetype
        {
            NameHash = name,
            Kind = kind,
            LodDistance = meta.Float(o + ArchLodDistance),
            Flags = d.UInt32(o + ArchFlags),
            BoundsMin = meta.Vector3(o + ArchBoundsMin),
            BoundsMax = meta.Vector3(o + ArchBoundsMax),
            SphereCenter = meta.Vector3(o + ArchSphereCenter),
            SphereRadius = meta.Float(o + ArchSphereRadius),
            TextureDictionaryHash = d.UInt32(o + ArchTextureDictionary),
            DrawableDictionaryHash = drawableDictionary,
            PhysicsDictionaryHash = d.UInt32(o + ArchPhysicsDictionary),
            // The old layout has no asset fields: the model has the archetype's name.
            AssetType = oldLayout
                ? drawableDictionary != 0 ? ArchetypeAssetType.DrawableDictionary : ArchetypeAssetType.Drawable
                : (ArchetypeAssetType)d.UInt32(o + ArchAssetType),
            AssetNameHash = oldLayout ? name : d.UInt32(o + ArchAssetName),
            // CTimeArchetypeDef: a base archetype followed by the hours it is visible.
            TimeFlags = kind == ArchetypeKind.Time ? d.UInt32(o + BaseArchetypeSize) : 0,
            InteriorEntities = interior.Entities,
            InteriorRooms = interior.Rooms,
            InteriorPortals = interior.Portals,
            InteriorEntitySets = interior.EntitySets,
        };
    }
}
