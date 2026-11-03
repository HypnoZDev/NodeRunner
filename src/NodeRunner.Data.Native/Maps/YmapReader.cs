using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// reader for map data files (binary Meta .ymap, root structure CMapData).
public sealed class YmapReader : IMapDataReader
{
    // Structure name hashes as they appear in the files.
    internal const uint MapDataStructure = 3545841574;
    internal const uint EntityStructure = 3461354627;
    internal const uint InteriorInstanceStructure = 164374718;

    private const int MapDataSize = 512;
    internal const int EntitySize = 128;
    private const int InteriorInstanceSize = 160; // An entity followed by interior-specific fields.

    // CMapData fields.
    private const int MapName = 8;
    private const int MapParent = 12;
    private const int MapFlags = 16;
    private const int MapContentFlags = 20;
    internal const int MapStreamingMin = 32;
    internal const int MapStreamingMax = 48;
    internal const int MapEntitiesMin = 64;
    internal const int MapEntitiesMax = 80;
    internal const int MapEntities = 96;

    // CEntityDef fields.
    internal const int EntityArchetype = 8;
    private const int EntityFlags = 12;
    private const int EntityGuid = 16;
    internal const int EntityPosition = 32;
    internal const int EntityRotation = 48;
    internal const int EntityScaleXy = 64;
    internal const int EntityScaleZ = 68;
    internal const int EntityParentIndex = 72;
    private const int EntityLodDistance = 76;
    private const int EntityChildLodDistance = 80;
    private const int EntityLodLevel = 84;
    internal const int EntityChildCount = 88;

    public MapData Read(ReadOnlySpan<byte> data)
    {
        var meta = new MetaResource(data);
        int root = meta.RootBlock;
        if (root < 0 || meta.BlockStructure(root) != MapDataStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de carte (.ymap).");
        meta.RequireStructure(MapDataStructure, MapDataSize, "CMapData");

        int map = meta.BlockStart(root);
        var entities = new List<MapEntity>();
        foreach (var (offset, structure) in EntityOffsets(meta, map))
            entities.Add(ReadEntity(meta, offset, isInterior: structure == InteriorInstanceStructure, sourceIndex: entities.Count));

        if (entities.Any(e => !e.IsInterior))
            meta.RequireStructure(EntityStructure, EntitySize, "CEntityDef");
        if (entities.Any(e => e.IsInterior))
            meta.RequireStructure(InteriorInstanceStructure, InteriorInstanceSize, "CMloInstanceDef");

        var d = meta.Data;
        return new MapData
        {
            NameHash = d.UInt32(map + MapName),
            ParentHash = d.UInt32(map + MapParent),
            Flags = d.UInt32(map + MapFlags),
            ContentFlags = d.UInt32(map + MapContentFlags),
            StreamingExtentsMin = meta.Vector3(map + MapStreamingMin),
            StreamingExtentsMax = meta.Vector3(map + MapStreamingMax),
            EntitiesExtentsMin = meta.Vector3(map + MapEntitiesMin),
            EntitiesExtentsMax = meta.Vector3(map + MapEntitiesMax),
            Entities = entities,
        };
    }

    // Offsets of the entities (ordinary ones and interior instances) of the CMapData at map, in file order.
    internal static IEnumerable<(int Offset, uint Structure)> EntityOffsets(MetaResource meta, int map) =>
        meta.ReadPointerArray(map + MapEntities).Where(e => e.Structure is EntityStructure or InteriorInstanceStructure);

    internal static MapEntity ReadEntity(MetaResource meta, int o, bool isInterior, int room = -1, int portal = -1, int sourceIndex = -1)
    {
        var d = meta.Data;
        float scaleXy = meta.Float(o + EntityScaleXy);
        var rotation = meta.Vector3(o + EntityRotation);
        return new MapEntity
        {
            ArchetypeHash = d.UInt32(o + EntityArchetype),
            Flags = d.UInt32(o + EntityFlags),
            Guid = d.UInt32(o + EntityGuid),
            Position = meta.Vector3(o + EntityPosition),
            StoredRotation = new Quaternion(rotation, meta.Float(o + EntityRotation + 12)),
            Scale = new Vector3(scaleXy, scaleXy, meta.Float(o + EntityScaleZ)),
            ParentIndex = (int)d.UInt32(o + EntityParentIndex),
            LodDistance = meta.Float(o + EntityLodDistance),
            ChildLodDistance = meta.Float(o + EntityChildLodDistance),
            LodLevel = (LodLevel)d.UInt32(o + EntityLodLevel),
            ChildCount = (int)d.UInt32(o + EntityChildCount),
            IsInterior = isInterior,
            InteriorRoom = room,
            InteriorPortal = portal,
            SourceIndex = sourceIndex,
        };
    }
}
