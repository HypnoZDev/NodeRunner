using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Writes an edited map back to a map file (.ymap).
public sealed class YmapWriter : IMapDataWriter
{
    private const int PointerSize = 8;

    public MapFileContent Write(ReadOnlySpan<byte> source, MapData map, MapExtents extents) => Write(source, map, extents, null);

    // borrowed: descriptions of structures (parking spots, timecycle zones) taken from game map files, for a file that
    // does not describe them yet and gets its first ones.
    // template: a game map file with ordinary objects, whose descriptions are used when objects are added to a file
    // that has none (saved empty, or with interiors only). interiorInstance: the description of CMloInstanceDef, for a
    // file getting its first interior instance; archetypes: the interiors' number of portals to the outside (given for
    // the user's own maps, whose interiors may have changed).
    public MapFileContent Write(ReadOnlySpan<byte> source, MapData map, MapExtents extents, IReadOnlyList<MetaDocument.StructureInfo>? borrowed,
        byte[]? template = null, MetaDocument.StructureInfo? interiorInstance = null, Func<uint, Archetype?>? archetypes = null)
    {
        var document = MetaDocument.Read(source);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YmapReader.MapDataStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de carte (.ymap).");
        var mapData = document.Blocks[root].Data;

        var sources = EntityItems(document, mapData);
        foreach (var entity in map.Entities)
        {
            if (entity.SourceIndex < 0)
                continue;
            if (entity.SourceIndex >= sources.Count || ReadUInt32(document, sources[entity.SourceIndex], YmapReader.EntityArchetype) != entity.FileArchetypeHash)
                throw new InvalidDataException($"L'objet n° {entity.SourceIndex + 1} du fichier n'est pas celui de la carte chargée.");
        }

        var kept = map.KeptEntities.ToList();
        var added = kept.Where(e => e.SourceIndex < 0).ToList();
        var newItems = added.Count == 0 ? [] : AppendEntities(document, mapData, sources, added, template, interiorInstance);
        var items = new List<(int Block, int Offset)>(kept.Count);
        int next = 0;
        foreach (var entity in kept)
            items.Add(entity.SourceIndex >= 0 ? sources[entity.SourceIndex] : newItems[next++]);
        for (int i = 0; i < kept.Count; i++)
        {
            WriteEntity(document, items[i], kept[i]);
            if (kept[i].IsInterior && document.Blocks[items[i].Block].Structure == YmapReader.InteriorInstanceStructure
                && (archetypes is not null || kept[i].SourceIndex < 0))
                WriteExitPortals(document, items[i], kept[i], archetypes);
        }
        WriteEntityPointers(document, mapData, items, template);
        WriteArray(document, mapData, SpotArray, map.CarGenerators, s => s.SourceIndex, s => s.IsDeleted, WriteCarGenerator, borrowed);
        WriteArray(document, mapData, ZoneArray, map.TimecycleModifiers, z => z.SourceIndex, z => z.IsDeleted, WriteTimecycle, borrowed);
        WriteLights(document, mapData, map);

        WriteVector3(mapData, YmapReader.MapStreamingMin, extents.StreamingMin);
        WriteVector3(mapData, YmapReader.MapStreamingMax, extents.StreamingMax);
        WriteVector3(mapData, YmapReader.MapEntitiesMin, extents.EntitiesMin);
        WriteVector3(mapData, YmapReader.MapEntitiesMax, extents.EntitiesMax);

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // Fields of CEntityDef written for a new object (offsets checked against the file's description of the structure).
    private const int EntityGuid = 16;
    private const int EntityPriority = 92;
    private const int EntityExtensions = 96;
    private const int EntityAmbientOcclusion = 112;
    private const int EntityArtificialAmbientOcclusion = 116;
    private const int EntityTint = 120;

    private static readonly (string Name, int Offset)[] CheckedEntityFields =
    [
        ("archetypeName", YmapReader.EntityArchetype), ("flags", 12), ("guid", 16), ("position", YmapReader.EntityPosition),
        ("rotation", YmapReader.EntityRotation), ("scaleXY", YmapReader.EntityScaleXy), ("scaleZ", YmapReader.EntityScaleZ),
        ("parentIndex", YmapReader.EntityParentIndex), ("lodDist", 76), ("childLodDist", 80), ("lodLevel", 84),
        ("numChildren", YmapReader.EntityChildCount), ("priorityLevel", EntityPriority), ("extensions", EntityExtensions),
        ("ambientOcclusionMultiplier", EntityAmbientOcclusion), ("artificialAmbientOcclusion", EntityArtificialAmbientOcclusion),
        ("tintValue", EntityTint),
    ];

    // CMloInstanceDef fields after those of CEntityDef.
    private const int InstanceDefaultEntitySets = 136;
    private const int InstanceExitPortals = 152;

    // Builds a whole map file for a map made by the user (IsCustom), which can hold added objects: the descriptions
    // of the structures are taken from template (any game map file with ordinary objects), the rest is new. Interior
    // instances (of interiors made by the user) need interiorInstance, the description of CMloInstanceDef taken from
    // a game map that has one, and archetypes (their number of portals to the outside).
    public MapFileContent Build(ReadOnlySpan<byte> template, MapData map, string name, MapExtents extents,
        MetaDocument.StructureInfo? interiorInstance = null, Func<uint, Archetype?>? archetypes = null,
        IReadOnlyList<MetaDocument.StructureInfo>? borrowed = null)
    {
        var document = MetaDocument.Read(template);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YmapReader.MapDataStructure)
            throw new InvalidDataException("Le modèle n'est pas un fichier de carte (.ymap).");
        CheckEntityLayout(document);
        var templateMapData = document.Blocks[root].Data;
        var templateItems = EntityItems(document, templateMapData);
        var entityTemplate = templateItems.Where(i => document.Blocks[i.Block].Structure == YmapReader.EntityStructure)
            .Select(i => document.Blocks[i.Block].Data.AsSpan(i.Offset, YmapReader.EntitySize).ToArray())
            .FirstOrDefault() ?? throw new InvalidDataException("Le modèle ne contient aucun objet.");
        uint pointerStructure = document.Blocks[MetaDocument.DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(templateMapData.AsSpan(YmapReader.MapEntities))).Block].Structure;

        MetaDocument? saved = map.SavedContent is { } content ? MetaDocument.Read(content) : null;
        var spots = map.KeptCarGenerators.ToList();
        var zones = map.TimecycleModifiers.Where(z => !z.IsDeleted).ToList();
        if (spots.Count > 0)
            RequireStructure(document, SpotArray, borrowed);
        if (zones.Count > 0)
            RequireStructure(document, ZoneArray, borrowed);
        var savedItems = saved is null ? [] : EntityItems(saved, saved.Blocks[saved.RootBlock].Data);

        if (map.KeptEntities.Any(e => e.IsInterior) && document.FindStructure(YmapReader.InteriorInstanceStructure) is null)
        {
            if (interiorInstance is null || interiorInstance.Size != YmapReader.InteriorInstanceSize)
                throw new InvalidDataException("Description des intérieurs placés (CMloInstanceDef) introuvable.");
            document.Structures.Add(interiorInstance);
        }

        // The new document: the template's descriptions, new blocks.
        document.Blocks.Clear();
        document.Name = name;
        var kept = map.KeptEntities.ToList();
        var mapData = new byte[templateMapData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(8), map.NameHash);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(12), map.ParentHash);
        var (flags, contentFlags) = MapFlags(kept);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(16), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(20), contentFlags);
        WriteVector3(mapData, YmapReader.MapStreamingMin, extents.StreamingMin);
        WriteVector3(mapData, YmapReader.MapStreamingMax, extents.StreamingMax);
        WriteVector3(mapData, YmapReader.MapEntitiesMin, extents.EntitiesMin);
        WriteVector3(mapData, YmapReader.MapEntitiesMax, extents.EntitiesMax);
        document.Blocks.Add(new MetaDocument.Block(YmapReader.MapDataStructure, mapData));
        document.RootBlock = 0;

        // Entities, in blocks of at most MaxBlockLength bytes, then the array of pointers to them.
        var items = new List<(int Block, int Offset)>();
        var current = new List<byte>();
        uint currentStructure = YmapReader.EntityStructure;
        void Flush()
        {
            if (current.Count == 0)
                return;
            document.Blocks.Add(new MetaDocument.Block(currentStructure, current.ToArray()));
            current.Clear();
        }
        var fresh = new HashSet<int>();
        foreach (var entity in kept)
        {
            uint structure = entity.IsInterior ? YmapReader.InteriorInstanceStructure : YmapReader.EntityStructure;
            int size = entity.IsInterior ? YmapReader.InteriorInstanceSize : YmapReader.EntitySize;
            byte[] item;
            if (entity.SourceIndex >= 0 && saved is not null && entity.SourceIndex < savedItems.Count
                && saved.Blocks[savedItems[entity.SourceIndex].Block].Structure == structure)
            {
                var (b, o) = savedItems[entity.SourceIndex];
                item = saved.Blocks[b].Data.AsSpan(o, size).ToArray();
                item.AsSpan(EntityExtensions, 16).Clear(); // Would point into the old file.
                if (entity.IsInterior)
                    item.AsSpan(InstanceDefaultEntitySets, 16).Clear();
            }
            else
            {
                // Every field of a new interior instance is written: it starts from zeros.
                item = entity.IsInterior ? new byte[size] : (byte[])entityTemplate.Clone();
                fresh.Add(items.Count);
            }
            if (current.Count > 0 && (structure != currentStructure || current.Count + item.Length > MetaDocument.MaxBlockLength))
                Flush();
            currentStructure = structure;
            items.Add((document.Blocks.Count, current.Count));
            current.AddRange(item);
        }
        Flush();

        for (int i = 0; i < kept.Count; i++)
        {
            if (fresh.Contains(i))
                WriteNewEntityFields(document.Blocks[items[i].Block].Data, items[i].Offset, kept[i]);
            WriteEntity(document, items[i], kept[i]);
            if (kept[i].IsInterior)
                WriteExitPortals(document, items[i], kept[i], archetypes);
        }

        if (items.Count > 0)
        {
            var pointers = new byte[items.Count * PointerSize];
            for (int i = 0; i < items.Count; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(i * PointerSize), MetaDocument.Pointer(items[i].Block, items[i].Offset));
            document.Blocks.Add(new MetaDocument.Block(pointerStructure, pointers));
            var field = mapData.AsSpan(YmapReader.MapEntities);
            BinaryPrimitives.WriteUInt64LittleEndian(field, MetaDocument.Pointer(document.Blocks.Count - 1, 0));
            BinaryPrimitives.WriteUInt16LittleEndian(field[8..], (ushort)items.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(field[10..], (ushort)items.Count);
        }

        // Parking spots and timecycle zones: each one of the saved file starts from its bytes there.
        AddArray(document, mapData, SpotArray, spots, x => x.SourceIndex, WriteCarGenerator, saved);
        AddArray(document, mapData, ZoneArray, zones, x => x.SourceIndex, WriteTimecycle, saved);

        var (bytes, pageFlags) = document.Write();
        return new MapFileContent(bytes, pageFlags);
    }

    // Objects of a written map given lower-detail parents in another map (automatic LODs): the map names its parent
    // map, each object listed (by its number in the file) becomes HD with its parent's number there and the flag
    // "lower detail in the parent map".
    public static MapFileContent LinkToParent(MapFileContent written, uint parentMap, IReadOnlyDictionary<int, int> parents)
    {
        var document = MetaDocument.Read(written.Content);
        var mapData = document.Blocks[document.RootBlock].Data;
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(12), parentMap);
        var items = EntityItems(document, mapData);
        foreach (var (index, parent) in parents)
        {
            var (block, offset) = items[index];
            var item = document.Blocks[block].Data.AsSpan(offset, YmapReader.EntitySize);
            BinaryPrimitives.WriteUInt32LittleEndian(item[12..], BinaryPrimitives.ReadUInt32LittleEndian(item[12..]) | LodInParentMapFlag);
            BinaryPrimitives.WriteInt32LittleEndian(item[YmapReader.EntityParentIndex..], parent);
            BinaryPrimitives.WriteInt32LittleEndian(item[84..], (int)LodLevel.Hd);
        }
        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    private const uint LodInParentMapFlag = 8;

    // Lower-detail objects of a written map: how many children each one has (by its number in the file), and up to
    // where those are drawn.
    public static MapFileContent SetChildren(MapFileContent written, IReadOnlyDictionary<int, (int Count, float Distance)> children)
    {
        var document = MetaDocument.Read(written.Content);
        var items = EntityItems(document, document.Blocks[document.RootBlock].Data);
        foreach (var (index, (count, distance)) in children)
        {
            var (block, offset) = items[index];
            var item = document.Blocks[block].Data.AsSpan(offset, YmapReader.EntitySize);
            BinaryPrimitives.WriteInt32LittleEndian(item[YmapReader.EntityChildCount..], count);
            BinaryPrimitives.WriteSingleLittleEndian(item[80..], distance);
        }
        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // The descriptions of parking spots (CCarGen) and timecycle zones (CTimeCycleModifier) in a map file.
    public static MetaDocument.StructureInfo? CarGeneratorDescription(ReadOnlySpan<byte> source) =>
        MetaDocument.Read(source).FindStructure(YmapReader.CarGeneratorStructure);

    public static MetaDocument.StructureInfo? TimecycleDescription(ReadOnlySpan<byte> source) =>
        MetaDocument.Read(source).FindStructure(YmapReader.TimecycleStructure);

    // An interior instance's number of portals to the outside (limbo, room 0); the file's value kept when the interior
    // is unknown.
    private static void WriteExitPortals(MetaDocument document, (int Block, int Offset) item, MapEntity entity, Func<uint, Archetype?>? archetypes)
    {
        int? count = archetypes?.Invoke(entity.ArchetypeHash)?.InteriorPortals.Count(p => p.RoomFrom == 0 || p.RoomTo == 0);
        if (count is not null || entity.SourceIndex < 0)
            BinaryPrimitives.WriteInt32LittleEndian(document.Blocks[item.Block].Data.AsSpan(item.Offset + InstanceExitPortals), count ?? 0);
    }

    // The description of interior instances (CMloInstanceDef) in a map file, null if it has none.
    public static MetaDocument.StructureInfo? InteriorInstanceDescription(ReadOnlySpan<byte> source) =>
        MetaDocument.Read(source).FindStructure(YmapReader.InteriorInstanceStructure);

    internal static void WriteNewEntityFields(byte[] data, int o, MapEntity entity)
    {
        var item = data.AsSpan(o, YmapReader.EntitySize);
        BinaryPrimitives.WriteUInt32LittleEndian(item[YmapReader.EntityArchetype..], entity.ArchetypeHash);
        BinaryPrimitives.WriteUInt32LittleEndian(item[12..], entity.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(item[16..], entity.Guid);
        BinaryPrimitives.WriteInt32LittleEndian(item[YmapReader.EntityParentIndex..], -1);
        BinaryPrimitives.WriteSingleLittleEndian(item[76..], entity.LodDistance);
        BinaryPrimitives.WriteSingleLittleEndian(item[80..], entity.ChildLodDistance);
        BinaryPrimitives.WriteInt32LittleEndian(item[84..], (int)entity.LodLevel);
        BinaryPrimitives.WriteInt32LittleEndian(item[YmapReader.EntityChildCount..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(item[EntityPriority..], 0); // PRI_REQUIRED.
        item.Slice(EntityExtensions, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(item[EntityAmbientOcclusion..], 255);
        BinaryPrimitives.WriteInt32LittleEndian(item[EntityArtificialAmbientOcclusion..], 255);
        BinaryPrimitives.WriteUInt32LittleEndian(item[EntityTint..], 0);
    }

    private static (uint Flags, uint ContentFlags) MapFlags(IEnumerable<MapEntity> entities)
    {
        uint flags = 0, contentFlags = 0;
        foreach (var entity in entities)
        {
            if (entity.IsInterior)
                contentFlags |= 8;
            switch (entity.LodLevel)
            {
                case LodLevel.Hd or LodLevel.OrphanHd:
                    contentFlags |= 1;
                    break;
                case LodLevel.Lod:
                    contentFlags |= 2;
                    flags |= 2;
                    break;
                case LodLevel.Slod1:
                    contentFlags |= 16;
                    flags |= 2;
                    break;
                default:
                    contentFlags |= 4 | 16;
                    flags |= 2;
                    break;
            }
        }
        return (flags, contentFlags);
    }

    // Checks that the file describes CEntityDef with the field offsets written here (fields found by name).
    private static void CheckEntityLayout(MetaDocument document)
    {
        var structure = document.FindStructure(YmapReader.EntityStructure)
                        ?? throw new InvalidDataException("Le modèle ne décrit pas les objets (CEntityDef).");
        if (structure.Size != YmapReader.EntitySize)
            throw new InvalidDataException("Taille inattendue des objets (CEntityDef) dans le modèle.");
        var offsets = new Dictionary<uint, int>();
        for (int i = 0; i + 16 <= structure.Entries.Length; i += 16)
            offsets[BinaryPrimitives.ReadUInt32LittleEndian(structure.Entries.AsSpan(i))] = BinaryPrimitives.ReadInt32LittleEndian(structure.Entries.AsSpan(i + 4));
        foreach (var (field, offset) in CheckedEntityFields)
        {
            if (offsets.TryGetValue(Hashing.Joaat.Hash(field), out int actual) && actual != offset)
                throw new InvalidDataException($"Champ {field} des objets à un emplacement inattendu ({actual} au lieu de {offset}).");
        }
    }

    // Where each entity of the file is (block, offset), in the order the reader numbers them (SourceIndex).
    private static List<(int Block, int Offset)> EntityItems(MetaDocument document, byte[] mapData)
    {
        var items = new List<(int Block, int Offset)>();
        var (arrayBlock, arrayOffset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(mapData.AsSpan(YmapReader.MapEntities)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(mapData.AsSpan(YmapReader.MapEntities + 8));
        if (count == 0 || arrayBlock < 0)
            return items;
        var array = document.Blocks[arrayBlock].Data;
        for (int i = 0; i < count; i++)
        {
            var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(arrayOffset + i * PointerSize)));
            if (block < 0)
                continue; // Null pointer (skipped by the reader too).
            if (document.Blocks[block].Structure is YmapReader.EntityStructure or YmapReader.InteriorInstanceStructure)
                items.Add((block, offset));
        }
        return items;
    }

    private static void WriteEntity(MetaDocument document, (int Block, int Offset) item, MapEntity entity)
    {
        var data = document.Blocks[item.Block].Data;
        int o = item.Offset;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.EntityArchetype), entity.ArchetypeHash);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + EntityGuid), entity.Guid);
        WriteVector3(data, o + YmapReader.EntityPosition, entity.Position);
        var r = entity.StoredRotation;
        WriteVector3(data, o + YmapReader.EntityRotation, new Vector3(r.X, r.Y, r.Z));
        WriteFloat(data, o + YmapReader.EntityRotation + 12, r.W);
        WriteFloat(data, o + YmapReader.EntityScaleXy, entity.Scale.X);
        WriteFloat(data, o + YmapReader.EntityScaleZ, entity.Scale.Z);

        // Lower-detail parent: its index in its own map once the deleted entities are left out. Links the map
        // library did not make (parent in a map loaded by scripts only...) are kept as they are.
        if (entity.Parent is { Map: { } parentMap } parent)
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.EntityParentIndex), parentMap.KeptIndexOf(parent));
        // Children left: deleted now, or missing from a saved child map (deleted in an earlier session). The game keeps
        // drawing a simplified model until all its declared children are there.
        if (MapLibrary.HasLostChildren(entity))
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.EntityChildCount), entity.Children.Count(c => !c.IsDeleted));
    }

    // Objects added to an existing file (a map imported or saved earlier): new blocks after the file's, an ordinary
    // object starting from one of the file's, an interior instance from zeros. Returns where each one is, in the order given.
    private static List<(int Block, int Offset)> AppendEntities(MetaDocument document, byte[] mapData,
        List<(int Block, int Offset)> sources, List<MapEntity> added, byte[]? gameTemplate, MetaDocument.StructureInfo? interiorInstance)
    {
        if (added.Any(e => e.IsInterior) && document.FindStructure(YmapReader.InteriorInstanceStructure) is null)
        {
            if (interiorInstance is null || interiorInstance.Size != YmapReader.InteriorInstanceSize)
                throw new InvalidDataException("Description des intérieurs placés (CMloInstanceDef) introuvable.");
            document.Structures.Add(interiorInstance);
        }
        byte[]? template = null;
        if (added.Any(e => !e.IsInterior))
        {
            int found = sources.FindIndex(i => document.Blocks[i.Block].Structure == YmapReader.EntityStructure);
            if (found >= 0)
            {
                var source = sources[found];
                template = document.Blocks[source.Block].Data.AsSpan(source.Offset, YmapReader.EntitySize).ToArray();
            }
            else
            {
                // No ordinary object in the file: every field of the new ones is written, they start from zeros.
                if (document.FindStructure(YmapReader.EntityStructure) is null)
                {
                    var description = (gameTemplate is null ? null : MetaDocument.Read(gameTemplate).FindStructure(YmapReader.EntityStructure))
                                      ?? throw new InvalidDataException("Description des objets (CEntityDef) introuvable.");
                    document.Structures.Add(description);
                }
                template = new byte[YmapReader.EntitySize];
            }
            CheckEntityLayout(document);
        }

        var items = new List<(int Block, int Offset)>();
        var current = new List<byte>();
        uint currentStructure = YmapReader.EntityStructure;
        void Flush()
        {
            if (current.Count == 0)
                return;
            document.Blocks.Add(new MetaDocument.Block(currentStructure, current.ToArray()));
            current.Clear();
        }
        foreach (var entity in added)
        {
            uint structure = entity.IsInterior ? YmapReader.InteriorInstanceStructure : YmapReader.EntityStructure;
            var item = entity.IsInterior ? new byte[YmapReader.InteriorInstanceSize] : template!;
            if (current.Count > 0 && (structure != currentStructure || current.Count + item.Length > MetaDocument.MaxBlockLength))
                Flush();
            currentStructure = structure;
            items.Add((document.Blocks.Count, current.Count));
            current.AddRange(item);
        }
        Flush();
        for (int i = 0; i < added.Count; i++)
            WriteNewEntityFields(document.Blocks[items[i].Block].Data, items[i].Offset, added[i]);

        var (flags, contentFlags) = MapFlags(added);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(16), BinaryPrimitives.ReadUInt32LittleEndian(mapData.AsSpan(16)) | flags);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(20), BinaryPrimitives.ReadUInt32LittleEndian(mapData.AsSpan(20)) | contentFlags);
        return items;
    }

    private static void WriteEntityPointers(MetaDocument document, byte[] mapData, List<(int Block, int Offset)> items, byte[]? gameTemplate)
    {
        var field = mapData.AsSpan(YmapReader.MapEntities);
        var (arrayBlock, arrayOffset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(field));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(field[8..]);
        if (items.Count > count)
        {
            // More objects than the file's array holds: a new array, same structure (the game file's if the file has none).
            uint structure = arrayBlock >= 0 && arrayBlock < document.Blocks.Count
                ? document.Blocks[arrayBlock].Structure
                : PointerArrayStructure(document, gameTemplate);
            var pointers = new byte[items.Count * PointerSize];
            for (int i = 0; i < items.Count; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(i * PointerSize), MetaDocument.Pointer(items[i].Block, items[i].Offset));
            document.Blocks.Add(new MetaDocument.Block(structure, pointers));
            BinaryPrimitives.WriteUInt64LittleEndian(field, MetaDocument.Pointer(document.Blocks.Count - 1, 0));
            BinaryPrimitives.WriteUInt16LittleEndian(field[8..], (ushort)items.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(field[10..], (ushort)items.Count);
            return;
        }
        if (arrayBlock < 0)
            return; // No entities before, none now.

        var array = document.Blocks[arrayBlock].Data.AsSpan(arrayOffset, count * PointerSize);
        array.Clear();
        for (int i = 0; i < items.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(array[(i * PointerSize)..], MetaDocument.Pointer(items[i].Block, items[i].Offset));
        BinaryPrimitives.WriteUInt64LittleEndian(field, items.Count == 0 ? 0 : MetaDocument.Pointer(arrayBlock, arrayOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(field[8..], (ushort)items.Count);  // Count.
        BinaryPrimitives.WriteUInt16LittleEndian(field[10..], (ushort)items.Count); // Capacity.
    }

    // The type of the block holding the pointers to the objects, taken from a game map file (with its description, if any).
    private static uint PointerArrayStructure(MetaDocument document, byte[]? gameTemplate)
    {
        var game = gameTemplate is null ? null : MetaDocument.Read(gameTemplate);
        if (game is null || game.RootBlock < 0 || game.RootBlock >= game.Blocks.Count)
            throw new InvalidDataException("Aucun fichier de carte du jeu utilisable comme modèle pour la liste des objets.");
        var (block, _) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(game.Blocks[game.RootBlock].Data.AsSpan(YmapReader.MapEntities)));
        if (block < 0 || block >= game.Blocks.Count)
            throw new InvalidDataException("Aucun fichier de carte du jeu utilisable comme modèle pour la liste des objets.");
        uint structure = game.Blocks[block].Structure;
        if (document.FindStructure(structure) is null && game.FindStructure(structure) is { } description)
            document.Structures.Add(description);
        return structure;
    }

    // An array of structures held in CMapData (pointer, count, capacity).
    private sealed record ArrayKind(int Field, uint Structure, int Size, string What);

    private static readonly ArrayKind SpotArray = new(YmapReader.MapCarGenerators, YmapReader.CarGeneratorStructure,
        YmapReader.CarGeneratorSize, "places de véhicules (CCarGen)");
    private static readonly ArrayKind ZoneArray = new(YmapReader.MapTimecycleModifiers, YmapReader.TimecycleStructure,
        YmapReader.TimecycleSize, "zones de timecycle (CTimeCycleModifier)");

    // The items: written in place when none was added or deleted (the file's bytes are kept as they are), otherwise as
    // a new array, each item of the file starting from its own bytes.
    private static void WriteArray<T>(MetaDocument document, byte[] mapData, ArrayKind kind, IReadOnlyList<T> all,
        Func<T, int> source, Func<T, bool> deleted, Action<byte[], int, T> write, IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        var old = FindArray(document, mapData, kind);
        int count = old?.Count ?? 0;
        var kept = all.Where(x => !deleted(x)).ToList();
        if (all.Any(x => source(x) >= count))
            throw new InvalidDataException($"Les {kind.What} ne sont pas celles du fichier.");
        if (kept.Count == count && kept.Select((x, i) => source(x) == i).All(same => same))
        {
            if (old is not { } a)
                return;
            var data = document.Blocks[a.Block].Data;
            for (int i = 0; i < count; i++)
                write(data, a.Offset + i * kind.Size, kept[i]);
            return;
        }

        if (kept.Count == 0)
        {
            SetArray(mapData, kind.Field, -1, 0);
            return;
        }
        RequireStructure(document, kind, borrowed);
        byte[]? oldData = old is { } o ? document.Blocks[o.Block].Data : null;
        var items = NewItems(kept, source, kind.Size, write,
            i => oldData is null ? default : oldData.AsSpan(old!.Value.Offset + i * kind.Size, kind.Size));
        // The file's array alone in its block (as in the game files): the block is reused, nothing is left unused.
        if (old is { Offset: 0 } whole && document.Blocks[whole.Block].Data.Length == whole.Count * kind.Size)
        {
            document.Blocks[whole.Block].Data = items;
            SetArray(mapData, kind.Field, whole.Block, kept.Count);
        }
        else
        {
            document.Blocks.Add(new MetaDocument.Block(kind.Structure, items));
            SetArray(mapData, kind.Field, document.Blocks.Count - 1, kept.Count);
        }
    }

    // A new array in a map built whole, its items of the saved file (if any) starting from their bytes there.
    private static void AddArray<T>(MetaDocument document, byte[] mapData, ArrayKind kind, List<T> kept, Func<T, int> source,
        Action<byte[], int, T> write, MetaDocument? saved)
    {
        if (kept.Count == 0)
            return;
        var old = saved is null ? null : FindArray(saved, saved.Blocks[saved.RootBlock].Data, kind);
        var items = NewItems(kept, source, kind.Size, write, i => old is { } a && i < a.Count
            ? saved!.Blocks[a.Block].Data.AsSpan(a.Offset + i * kind.Size, kind.Size) : default);
        document.Blocks.Add(new MetaDocument.Block(kind.Structure, items));
        SetArray(mapData, kind.Field, document.Blocks.Count - 1, kept.Count);
    }

    private static void RequireStructure(MetaDocument document, ArrayKind kind, IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        if (document.FindStructure(kind.Structure) is not null)
            return;
        var description = borrowed?.FirstOrDefault(d => d.NameHash == kind.Structure && d.Size == kind.Size)
                          ?? throw new InvalidDataException($"Description des {kind.What} introuvable.");
        document.Structures.Add(description);
    }

    private delegate ReadOnlySpan<byte> OriginalItem(int sourceIndex);

    // The items of a new array: one of the file starts from its bytes (original(SourceIndex)), a new one from zeros.
    private static byte[] NewItems<T>(List<T> kept, Func<T, int> source, int size, Action<byte[], int, T> write, OriginalItem original)
    {
        var items = new byte[kept.Count * size];
        for (int i = 0; i < kept.Count; i++)
        {
            int o = i * size;
            if (source(kept[i]) >= 0)
            {
                var bytes = original(source(kept[i]));
                if (bytes.Length == size)
                    bytes.CopyTo(items.AsSpan(o));
            }
            write(items, o, kept[i]);
        }
        return items;
    }

    // Where the file's array is, null when empty.
    private static (int Block, int Offset, int Count)? FindArray(MetaDocument document, byte[] mapData, ArrayKind kind)
    {
        var field = mapData.AsSpan(kind.Field);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(field[8..]);
        if (count == 0)
            return null;
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(field));
        if (block < 0 || block >= document.Blocks.Count || document.Blocks[block].Structure != kind.Structure
            || offset + count * kind.Size > document.Blocks[block].Data.Length)
            throw new InvalidDataException($"{kind.What} introuvables ou tronquées dans le fichier.");
        return (block, offset, count);
    }

    // An array field of CMapData (pointer, count, capacity) set to the start of block, or emptied (count 0).
    private static void SetArray(byte[] mapData, int fieldOffset, int block, int count)
    {
        var field = mapData.AsSpan(fieldOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(field, count == 0 ? 0 : MetaDocument.Pointer(block, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(field[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(field[10..], (ushort)count);
    }

    // Distant lights and their details (one item per light kept), in their arrays: in place when their number did not
    // change, otherwise in new arrays (the file's block reused when the array is alone in it, as in the game files).
    private static void WriteLights(MetaDocument document, byte[] mapData, MapData map)
    {
        int distantField = YmapReader.MapDistantLights;
        var lights = map.KeptDistantLights;
        if (lights.Count > 0 || ArrayCount(mapData, distantField + YmapReader.DistantPosition) > 0)
        {
            LightArray(document, mapData, distantField + YmapReader.DistantPosition, 12, lights.Count, (d, o, i) => WriteVector3(d, o, lights[i].Position));
            LightArray(document, mapData, distantField + YmapReader.DistantRgbi, 4, lights.Count, (d, o, i) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), lights[i].Rgbi));
            BinaryPrimitives.WriteUInt16LittleEndian(mapData.AsSpan(distantField + YmapReader.DistantStreetLights), (ushort)map.KeptStreetLights);
            BinaryPrimitives.WriteUInt16LittleEndian(mapData.AsSpan(distantField + YmapReader.DistantCategory), (ushort)map.DistantCategory);
        }
        int field = YmapReader.MapLodLights;
        var details = map.KeptLodLights;
        if (details.Count > 0 || ArrayCount(mapData, field + YmapReader.LodDirection) > 0)
        {
            int n = details.Count;
            LightArray(document, mapData, field + YmapReader.LodDirection, 12, n, (d, o, i) => WriteVector3(d, o, details[i].Direction));
            LightArray(document, mapData, field + YmapReader.LodFalloff, 4, n, (d, o, i) => WriteFloat(d, o, details[i].Falloff));
            LightArray(document, mapData, field + YmapReader.LodFalloffExponent, 4, n, (d, o, i) => WriteFloat(d, o, details[i].FalloffExponent));
            LightArray(document, mapData, field + YmapReader.LodTimeAndStateFlags, 4, n, (d, o, i) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), details[i].TimeAndStateFlags));
            LightArray(document, mapData, field + YmapReader.LodHash, 4, n, (d, o, i) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), details[i].Hash));
            LightArray(document, mapData, field + YmapReader.LodConeInner, 1, n, (d, o, i) => d[o] = details[i].ConeInnerAngle);
            LightArray(document, mapData, field + YmapReader.LodConeOuter, 1, n, (d, o, i) => d[o] = details[i].ConeOuterAngleOrCapExt);
            LightArray(document, mapData, field + YmapReader.LodCorona, 1, n, (d, o, i) => d[o] = details[i].CoronaIntensity);
        }
    }

    private static int ArrayCount(byte[] mapData, int field) => BinaryPrimitives.ReadUInt16LittleEndian(mapData.AsSpan(field + 8));

    // One array of the lights (pointer, count, capacity at field), set to count items of size bytes, each written by
    // write(data, offset, item number). A new array takes the type of the file's block.
    private static void LightArray(MetaDocument document, byte[] mapData, int field, int size, int count, Action<byte[], int, int> write)
    {
        var span = mapData.AsSpan(field);
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(span));
        bool found = fileCount > 0 && block >= 0 && block < document.Blocks.Count && offset + fileCount * size <= document.Blocks[block].Data.Length;
        if (fileCount > 0 && !found)
            throw new InvalidDataException("Les lumières ne sont pas celles du fichier.");
        if (found && count == fileCount)
        {
            var data = document.Blocks[block].Data;
            for (int i = 0; i < count; i++)
                write(data, offset + i * size, i);
            return;
        }
        if (count == 0)
        {
            SetArray(mapData, field, -1, 0);
            return;
        }
        if (!found)
            throw new InvalidDataException("Le fichier n'a pas de tableau de lumières à reprendre.");
        var items = new byte[count * size];
        for (int i = 0; i < count; i++)
            write(items, i * size, i);
        if (offset == 0 && document.Blocks[block].Data.Length == fileCount * size)
            document.Blocks[block].Data = items;
        else
        {
            document.Blocks.Add(new MetaDocument.Block(document.Blocks[block].Structure, items));
            block = document.Blocks.Count - 1;
        }
        SetArray(mapData, field, block, count);
    }

    // A light file made by the user (its lights only), built from template, a game file of the same kind
    // (distlodlights or lodlights: their descriptions, flags and arrays are reused).
    public MapFileContent BuildLights(ReadOnlySpan<byte> template, MapData map, string name, MapExtents extents)
    {
        var document = MetaDocument.Read(template);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YmapReader.MapDataStructure)
            throw new InvalidDataException("Le modèle n'est pas un fichier de carte (.ymap).");
        var mapData = document.Blocks[root].Data;
        if (map.KeptEntities.Any())
            throw new InvalidDataException("Un fichier de lumières ne contient pas d'objets.");
        SetArray(mapData, YmapReader.MapEntities, -1, 0);
        if (map.DistantLights.Count > 0 && ArrayCount(mapData, YmapReader.MapDistantLights + YmapReader.DistantPosition) == 0
            || map.LodLights.Count > 0 && ArrayCount(mapData, YmapReader.MapLodLights + YmapReader.LodDirection) == 0)
            throw new InvalidDataException("Le modèle n'est pas un fichier de lumières du même genre.");
        document.Name = name;
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(8), map.NameHash);
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(12), map.ParentHash);
        WriteLights(document, mapData, map);
        WriteVector3(mapData, YmapReader.MapStreamingMin, extents.StreamingMin);
        WriteVector3(mapData, YmapReader.MapStreamingMax, extents.StreamingMax);
        WriteVector3(mapData, YmapReader.MapEntitiesMin, extents.EntitiesMin);
        WriteVector3(mapData, YmapReader.MapEntitiesMax, extents.EntitiesMax);
        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    private static void WriteTimecycle(byte[] data, int o, TimecycleModifier zone)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.TimecycleName), zone.NameHash);
        // Only X, Y, Z: the 4th float of each corner of a zone of the file is kept as it is; a new one gets the game's.
        WriteVector3(data, o + YmapReader.TimecycleMin, zone.BoundsMin);
        WriteVector3(data, o + YmapReader.TimecycleMax, zone.BoundsMax);
        if (zone.SourceIndex < 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.TimecycleMin + 12), GameCornerW);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.TimecycleMax + 12), GameCornerW);
        }
        WriteFloat(data, o + YmapReader.TimecyclePercentage, zone.Percentage);
        WriteFloat(data, o + YmapReader.TimecycleRange, zone.Range);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.TimecycleStartHour), zone.StartHour);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.TimecycleEndHour), zone.EndHour);
    }

    // The 4th float of the corners of the game's timecycle zones (a NaN).
    private const uint GameCornerW = 0x7F800001;

    private static void WriteCarGenerator(byte[] data, int o, CarGenerator spot)
    {
        WriteVector3(data, o + YmapReader.CarPosition, spot.Position);
        WriteFloat(data, o + YmapReader.CarOrientX, spot.Orientation.X);
        WriteFloat(data, o + YmapReader.CarOrientY, spot.Orientation.Y);
        WriteFloat(data, o + YmapReader.CarPerpendicularLength, spot.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.CarModel), spot.CarModel);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.CarFlags), spot.Flags);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.CarColor1), spot.Color1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.CarColor1 + 4), spot.Color2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.CarColor1 + 8), spot.Color3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YmapReader.CarColor1 + 12), spot.Color4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YmapReader.CarPopGroup), spot.PopGroup);
        data[o + YmapReader.CarLivery] = (byte)(sbyte)spot.Livery;
    }

    private static uint ReadUInt32(MetaDocument document, (int Block, int Offset) item, int field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[item.Block].Data.AsSpan(item.Offset + field));

    private static void WriteVector3(byte[] data, int offset, Vector3 v)
    {
        WriteFloat(data, offset, v.X);
        WriteFloat(data, offset + 4, v.Y);
        WriteFloat(data, offset + 8, v.Z);
    }

    private static void WriteFloat(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
}
