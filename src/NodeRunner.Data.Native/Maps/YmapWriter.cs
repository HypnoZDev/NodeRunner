using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

/// <summary>
/// Writes an edited map back to a map file (.ymap). Starts from the file the map was read from and keeps every item
/// of it as Rockstar made it (flags, extensions, occluders, car generators...); only changes, for each entity, its
/// placement and its links to its lower-detail parent (index, number of children), leaves out the deleted entities
/// and updates the boxes of the map. Pointers between the file's items survive (see <see cref="MetaDocument"/>):
/// the items of a deleted entity simply stay unused.
/// </summary>
public sealed class YmapWriter : IMapDataWriter
{
    private const int PointerSize = 8;

    public MapFileContent Write(ReadOnlySpan<byte> source, MapData map, MapExtents extents)
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
                throw new InvalidDataException("L'ajout d'objets n'est pas encore pris en charge.");
            if (entity.SourceIndex >= sources.Count || ReadUInt32(document, sources[entity.SourceIndex], YmapReader.EntityArchetype) != entity.ArchetypeHash)
                throw new InvalidDataException($"L'objet n° {entity.SourceIndex + 1} du fichier n'est pas celui de la carte chargée.");
        }

        var kept = map.KeptEntities.ToList();
        foreach (var entity in kept)
            WriteEntity(document, sources[entity.SourceIndex], entity);
        WriteEntityPointers(document, mapData, kept.Select(e => sources[e.SourceIndex]).ToList());

        WriteVector3(mapData, YmapReader.MapStreamingMin, extents.StreamingMin);
        WriteVector3(mapData, YmapReader.MapStreamingMax, extents.StreamingMax);
        WriteVector3(mapData, YmapReader.MapEntitiesMin, extents.EntitiesMin);
        WriteVector3(mapData, YmapReader.MapEntitiesMax, extents.EntitiesMax);

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // Fields of CEntityDef written for a new object (offsets checked against the file's description of the structure).
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

    /// <summary>
    /// Builds a whole map file for a map made by the user (<see cref="MapData.IsCustom"/>), which can hold added objects:
    /// the descriptions of the structures are taken from <paramref name="template"/> (any game map file with ordinary
    /// objects), the rest is new. Objects already in the map's saved file (<see cref="MapData.SavedContent"/>) keep their
    /// data (except extensions); added ones are new entities without a lower-detail parent ("orphan HD"), as map tools
    /// make them. Interiors are not supported in such a map.
    /// </summary>
    public MapFileContent Build(ReadOnlySpan<byte> template, MapData map, string name, MapExtents extents)
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
        var savedItems = saved is null ? [] : EntityItems(saved, saved.Blocks[saved.RootBlock].Data);

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
        void Flush()
        {
            if (current.Count == 0)
                return;
            document.Blocks.Add(new MetaDocument.Block(YmapReader.EntityStructure, current.ToArray()));
            current.Clear();
        }
        var fresh = new HashSet<int>();
        foreach (var entity in kept)
        {
            if (entity.IsInterior)
                throw new InvalidDataException("Les intérieurs ne sont pas pris en charge dans un YMAP du projet.");
            byte[] item;
            if (entity.SourceIndex >= 0 && saved is not null && entity.SourceIndex < savedItems.Count
                && saved.Blocks[savedItems[entity.SourceIndex].Block].Structure == YmapReader.EntityStructure)
            {
                var (b, o) = savedItems[entity.SourceIndex];
                item = saved.Blocks[b].Data.AsSpan(o, YmapReader.EntitySize).ToArray();
                item.AsSpan(EntityExtensions, 16).Clear(); // Would point into the old file.
            }
            else
            {
                item = (byte[])entityTemplate.Clone();
                fresh.Add(items.Count);
            }
            if (current.Count + item.Length > MetaDocument.MaxBlockLength)
                Flush();
            items.Add((document.Blocks.Count, current.Count));
            current.AddRange(item);
        }
        Flush();

        for (int i = 0; i < kept.Count; i++)
        {
            if (fresh.Contains(i))
                WriteNewEntityFields(document.Blocks[items[i].Block].Data, items[i].Offset, kept[i]);
            WriteEntity(document, items[i], kept[i]);
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

        var (bytes, pageFlags) = document.Write();
        return new MapFileContent(bytes, pageFlags);
    }

    /// <summary>The fields of a new entity that are not its placement: as map tools set them for an added object.</summary>
    private static void WriteNewEntityFields(byte[] data, int o, MapEntity entity)
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

    /// <summary>The map's flags from its entities, as map tools compute them (only levels of detail matter here).</summary>
    private static (uint Flags, uint ContentFlags) MapFlags(IEnumerable<MapEntity> entities)
    {
        uint flags = 0, contentFlags = 0;
        foreach (var entity in entities)
        {
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

    /// <summary>Checks that the file describes CEntityDef with the field offsets written here (fields found by name).</summary>
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

    /// <summary>Where each entity of the file is (block, offset), in the order the reader numbers them (<see cref="MapEntity.SourceIndex"/>).</summary>
    private static List<(int Block, int Offset)> EntityItems(MetaDocument document, byte[] mapData)
    {
        var items = new List<(int, int)>();
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

    /// <summary>Replaces the list of entities of the map (array of pointers) by <paramref name="items"/>, in place.</summary>
    private static void WriteEntityPointers(MetaDocument document, byte[] mapData, List<(int Block, int Offset)> items)
    {
        var field = mapData.AsSpan(YmapReader.MapEntities);
        var (arrayBlock, arrayOffset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(field));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(field[8..]);
        if (items.Count > count)
            throw new InvalidDataException("L'ajout d'objets n'est pas encore pris en charge.");
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
