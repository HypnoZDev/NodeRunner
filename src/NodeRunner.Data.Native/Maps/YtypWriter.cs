using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Writes edited interiors (MLO) back to their archetype file (.ytyp).
public sealed class YtypWriter : IMapTypesWriter
{
    private const int PointerSize = 8;

    public MapFileContent Write(ReadOnlySpan<byte> source, IReadOnlyList<Archetype> interiors)
    {
        var document = MetaDocument.Read(source);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YtypReader.MapTypesStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de types (.ytyp).");

        var items = PointerItems(document, (root, 0), YtypReader.TypesArchetypes);
        foreach (var interior in interiors)
        {
            var item = items.Find(i => document.Blocks[i.Block].Structure == YtypReader.InteriorArchetypeStructure
                                       && ReadUInt32(document, i, YtypReader.ArchName) == interior.NameHash);
            if (item == default)
                throw new InvalidDataException($"L'intérieur 0x{interior.NameHash:X8} n'est pas dans ce fichier.");
            WriteInteriorEntities(document, item, interior.InteriorEntities);

            var (setBlock, setOffset, setCount) = Array(document, item, YtypReader.InteriorEntitySets);
            if (setCount != interior.InteriorEntitySets.Count)
                throw new InvalidDataException("Les entity sets de l'intérieur ne sont pas ceux du fichier.");
            for (int s = 0; s < setCount; s++)
                WriteEntitySet(document, (setBlock, setOffset + s * YtypReader.EntitySetSize), interior.InteriorEntitySets[s].Entities);
        }

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // The interior's objects: those deleted left out, those added (after the file's ones) appended, each in its room.
    // The rooms and portals list their objects by index, renumbered.
    private static void WriteInteriorEntities(MetaDocument document, (int Block, int Offset) interior, IReadOnlyList<MapEntity> entities)
    {
        var items = PointerItems(document, interior, YtypReader.InteriorEntities);
        int fileCount = WriteEntities(document, items, entities);

        var newIndex = new int[items.Count];
        var kept = new List<(int Block, int Offset)>();
        int entity = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bool isEntity = document.Blocks[items[i].Block].Structure == YmapReader.EntityStructure;
            bool keep = !isEntity || !entities[entity].IsDeleted;
            if (isEntity)
                entity++;
            newIndex[i] = keep ? kept.Count : -1;
            if (keep)
                kept.Add(items[i]);
        }
        var added = entities.Skip(fileCount).Where(e => !e.IsDeleted).ToList();
        if (kept.Count == items.Count && added.Count == 0)
            return;

        var additions = new List<(int Index, int Room)>();
        if (added.Count > 0)
        {
            var template = AnyEntity(document) ?? throw new InvalidDataException("aucun objet modèle dans ce fichier");
            var data = new byte[added.Count * YmapReader.EntitySize];
            int block = document.Blocks.Count;
            document.Blocks.Add(new MetaDocument.Block(YmapReader.EntityStructure, data));
            for (int i = 0; i < added.Count; i++)
            {
                int o = i * YmapReader.EntitySize;
                template.CopyTo(data, o);
                YmapWriter.WriteNewEntityFields(data, o, added[i]);
                WritePlacement(data, o, added[i]);
                additions.Add((kept.Count, added[i].InteriorRoom));
                kept.Add((block, o));
            }
        }

        WritePointers(document, interior, YtypReader.InteriorEntities, kept);
        Renumber(document, interior, YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomAttachedObjects, newIndex, additions);
        Renumber(document, interior, YtypReader.InteriorPortals, YtypReader.PortalSize, YtypReader.PortalAttachedObjects, newIndex, []);
    }

    // The objects of an entity set, those deleted left out along with their room in the set's list of rooms (same
    // index as the pointer).
    private static void WriteEntitySet(MetaDocument document, (int Block, int Offset) set, IReadOnlyList<MapEntity> entities)
    {
        var items = PointerItems(document, set, YtypReader.EntitySetEntities);
        WriteEntities(document, items, entities);

        var (locationBlock, locationOffset, locationCount) = Array(document, set, YtypReader.EntitySetLocations);
        var kept = new List<(int Block, int Offset)>();
        var rooms = new List<uint>();
        int entity = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bool isEntity = document.Blocks[items[i].Block].Structure == YmapReader.EntityStructure;
            bool keep = !isEntity || !entities[entity].IsDeleted;
            if (isEntity)
                entity++;
            if (!keep)
                continue;
            kept.Add(items[i]);
            if (i < locationCount)
                rooms.Add(BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[locationBlock].Data.AsSpan(locationOffset + i * 4)));
        }
        if (kept.Count == items.Count)
            return;

        WritePointers(document, set, YtypReader.EntitySetEntities, kept);
        for (int i = 0; i < rooms.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[locationBlock].Data.AsSpan(locationOffset + i * 4), rooms[i]);
        SetArray(document, set, YtypReader.EntitySetLocations, locationBlock, locationOffset, rooms.Count);
    }

    private static byte[]? AnyEntity(MetaDocument document)
    {
        var block = document.Blocks.Find(b => b.Structure == YmapReader.EntityStructure && b.Data.Length >= YmapReader.EntitySize);
        return block?.Data.AsSpan(0, YmapReader.EntitySize).ToArray();
    }

    // Replaces a pointer array, in place when it does not grow, otherwise in a new block.
    private static void WritePointers(MetaDocument document, (int Block, int Offset) item, int field, List<(int Block, int Offset)> targets)
    {
        var (block, offset, count) = Array(document, item, field);
        if (targets.Count > count)
        {
            uint structure = block >= 0 ? document.Blocks[block].Structure : PointerArrayStructure(document);
            block = document.Blocks.Count;
            offset = 0;
            document.Blocks.Add(new MetaDocument.Block(structure, new byte[targets.Count * PointerSize]));
        }
        if (block >= 0)
        {
            var data = document.Blocks[block].Data;
            for (int i = 0; i < targets.Count; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + i * PointerSize), MetaDocument.Pointer(targets[i].Block, targets[i].Offset));
        }
        SetArray(document, item, field, block, offset, targets.Count);
    }

    // The structure of the blocks holding arrays of pointers: that of the file's list of archetypes.
    private static uint PointerArrayStructure(MetaDocument document)
    {
        var (block, _, _) = Array(document, (document.RootBlock, 0), YtypReader.TypesArchetypes);
        return document.Blocks[block].Structure;
    }

    // The object lists (indices) of the rooms or portals of an interior: renumbered, with the added objects appended
    // to their room.
    private static void Renumber(MetaDocument document, (int Block, int Offset) interior, int field, int size, int objectsField,
        int[] newIndex, List<(int Index, int Room)> additions)
    {
        var (block, offset, count) = Array(document, interior, field);
        uint? listStructure = null;
        for (int r = 0; r < count; r++)
        {
            var (listBlock, _, listCount) = Array(document, (block, offset + r * size), objectsField);
            if (listCount > 0)
                listStructure ??= document.Blocks[listBlock].Structure;
        }
        for (int r = 0; r < count; r++)
        {
            var owner = (block, offset + r * size);
            var (listBlock, listOffset, listCount) = Array(document, owner, objectsField);
            var list = new List<uint>();
            for (int i = 0; i < listCount; i++)
            {
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4));
                if (index >= newIndex.Length)
                    list.Add(index);
                else if (newIndex[index] >= 0)
                    list.Add((uint)newIndex[index]);
            }
            list.AddRange(additions.Where(a => a.Room == r).Select(a => (uint)a.Index));
            if (list.Count > listCount)
            {
                listBlock = document.Blocks.Count;
                listOffset = 0;
                document.Blocks.Add(new MetaDocument.Block(listStructure ?? throw new InvalidDataException("aucune liste d'objets de salle dans ce fichier"),
                    new byte[list.Count * 4]));
            }
            for (int i = 0; i < list.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4), list[i]);
            SetArray(document, owner, objectsField, listBlock, listOffset, list.Count);
        }
    }

    // Pointer, count and capacity of an array field; an empty array points nowhere.
    private static void SetArray(MetaDocument document, (int Block, int Offset) item, int field, int block, int offset, int count)
    {
        var span = document.Blocks[item.Block].Data.AsSpan(item.Offset + field);
        BinaryPrimitives.WriteUInt64LittleEndian(span, count == 0 ? 0 : MetaDocument.Pointer(block, offset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)count);
    }

    // The objects of the file are the first ones of the archetype read from it, in the same order (the reader skips
    // the pointers to other structures); returns how many.
    private static int WriteEntities(MetaDocument document, List<(int Block, int Offset)> items, IReadOnlyList<MapEntity> entities)
    {
        var objects = items.Where(i => document.Blocks[i.Block].Structure == YmapReader.EntityStructure).ToList();
        if (objects.Count > entities.Count)
            throw new InvalidDataException("Les objets de l'intérieur ne sont pas ceux du fichier.");
        for (int i = 0; i < objects.Count; i++)
        {
            if (ReadUInt32(document, objects[i], YmapReader.EntityArchetype) != entities[i].ArchetypeHash)
                throw new InvalidDataException($"L'objet n° {i + 1} de l'intérieur n'est pas celui du fichier.");
            WritePlacement(document.Blocks[objects[i].Block].Data, objects[i].Offset, entities[i]);
        }
        return objects.Count;
    }

    private static void WritePlacement(byte[] data, int o, MapEntity entity)
    {
        WriteVector3(data, o + YmapReader.EntityPosition, entity.Position);
        var r = entity.StoredRotation;
        WriteVector3(data, o + YmapReader.EntityRotation, new Vector3(r.X, r.Y, r.Z));
        WriteFloat(data, o + YmapReader.EntityRotation + 12, r.W);
        WriteFloat(data, o + YmapReader.EntityScaleXy, entity.Scale.X);
        WriteFloat(data, o + YmapReader.EntityScaleZ, entity.Scale.Z);
    }

    private static (int Block, int Offset, int Count) Array(MetaDocument document, (int Block, int Offset) item, int field)
    {
        var span = document.Blocks[item.Block].Data.AsSpan(item.Offset + field);
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(span));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        return block < 0 ? (-1, 0, 0) : (block, offset, count);
    }

    private static List<(int Block, int Offset)> PointerItems(MetaDocument document, (int Block, int Offset) item, int field)
    {
        var items = new List<(int, int)>();
        var (block, offset, count) = Array(document, item, field);
        if (count == 0)
            return items;
        var array = document.Blocks[block].Data;
        for (int i = 0; i < count; i++)
        {
            var target = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(offset + i * PointerSize)));
            if (target.Block >= 0)
                items.Add(target);
        }
        return items;
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
