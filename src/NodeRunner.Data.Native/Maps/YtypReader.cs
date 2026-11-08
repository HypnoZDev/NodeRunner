using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// reader for archetype definition files (binary Meta .ytyp, root structure CMapTypes).
public sealed class YtypReader : IMapTypesReader
{
    // Structure name hashes as they appear in the files.
    internal const uint MapTypesStructure = 3649811809;
    private const uint BaseArchetypeStructure = 2195127427;
    private const uint TimeArchetypeStructure = 1991296364;
    internal const uint InteriorArchetypeStructure = 273704021;

    private const int BaseArchetypeSize = 144;
    private const int OldBaseArchetypeSize = 128; // Older layout without asset type and asset name.
    private const int InteriorArchetypeSize = 240; // A base archetype followed by interior-specific fields.

    // CMapTypes fields.
    internal const int TypesArchetypes = 24;
    internal const int TypesName = 40;

    // CBaseArchetypeDef fields.
    private const int ArchLodDistance = 8;
    private const int ArchFlags = 12;
    private const int ArchBoundsMin = 32;
    private const int ArchBoundsMax = 48;
    private const int ArchSphereCenter = 64;
    private const int ArchSphereRadius = 80;
    internal const int ArchName = 88;
    private const int ArchTextureDictionary = 92;
    private const int ArchDrawableDictionary = 100;
    private const int ArchPhysicsDictionary = 104;
    private const int ArchAssetType = 108;
    internal const int ArchAssetName = 112;

    // CMloArchetypeDef fields.
    internal const int InteriorEntities = 152;
    internal const int InteriorRooms = 168;
    internal const int InteriorPortals = 184;
    internal const int InteriorEntitySets = 200;

    // CMloRoomDef and CMloPortalDef.
    internal const uint RoomStructure = 186126833;
    internal const uint PortalStructure = 2572186314;
    internal const int RoomSize = 112;
    internal const int PortalSize = 64;
    internal const int RoomName = 8;
    internal const int RoomBoundsMin = 32;
    internal const int RoomBoundsMax = 48;
    internal const int RoomTimecycle = 68;
    internal const int RoomSecondaryTimecycle = 72;
    internal const int RoomFlags = 76;
    internal const int RoomPortalCount = 80;
    internal const int RoomFloorId = 84;
    internal const int RoomExteriorVisibilityDepth = 88;
    internal const int RoomAttachedObjects = 96;
    internal const int PortalRoomFrom = 8;
    internal const int PortalRoomTo = 12;
    internal const int PortalFlags = 16;
    internal const int PortalMirrorPriority = 20;
    internal const int PortalOpacity = 24;
    internal const int PortalAudioOcclusion = 28;
    internal const int PortalCorners = 32;
    internal const int PortalAttachedObjects = 48;

    // CMloEntitySet: name, room of each object, objects.
    internal const uint EntitySetStructure = 3601308153;
    internal const int EntitySetSize = 48;
    internal const int EntitySetName = 8;
    internal const int EntitySetLocations = 16;
    internal const int EntitySetEntities = 32;

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
                TimecycleHash = d.UInt32(r + RoomTimecycle),
                SecondaryTimecycleHash = d.UInt32(r + RoomSecondaryTimecycle),
                FloorId = (int)d.UInt32(r + RoomFloorId),
                SourceIndex = i,
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
                MirrorPriority = (int)d.UInt32(p + PortalMirrorPriority),
                Opacity = (int)d.UInt32(p + PortalOpacity),
                AudioOcclusionHash = d.UInt32(p + PortalAudioOcclusion),
                SourceIndex = i,
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

    // Optional groups of objects of an interior (furniture styles, upgrades...), shown when a script activates them.
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
