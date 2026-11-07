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
            WriteEntities(document, PointerItems(document, item, YtypReader.InteriorEntities), interior.InteriorEntities);

            var (setBlock, setOffset, setCount) = Array(document, item, YtypReader.InteriorEntitySets);
            if (setCount != interior.InteriorEntitySets.Count)
                throw new InvalidDataException("Les entity sets de l'intérieur ne sont pas ceux du fichier.");
            for (int s = 0; s < setCount; s++)
            {
                var set = (setBlock, setOffset + s * YtypReader.EntitySetSize);
                WriteEntities(document, PointerItems(document, set, YtypReader.EntitySetEntities), interior.InteriorEntitySets[s].Entities);
            }
        }

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // The objects of the file are those of the archetype read from it, in the same order (the reader skips the
    // pointers to other structures).
    private static void WriteEntities(MetaDocument document, List<(int Block, int Offset)> items, IReadOnlyList<MapEntity> entities)
    {
        var objects = items.Where(i => document.Blocks[i.Block].Structure == YmapReader.EntityStructure).ToList();
        if (objects.Count != entities.Count)
            throw new InvalidDataException("Les objets de l'intérieur ne sont pas ceux du fichier.");
        for (int i = 0; i < objects.Count; i++)
        {
            var entity = entities[i];
            if (ReadUInt32(document, objects[i], YmapReader.EntityArchetype) != entity.ArchetypeHash)
                throw new InvalidDataException($"L'objet n° {i + 1} de l'intérieur n'est pas celui du fichier.");
            var data = document.Blocks[objects[i].Block].Data;
            int o = objects[i].Offset;
            WriteVector3(data, o + YmapReader.EntityPosition, entity.Position);
            var r = entity.StoredRotation;
            WriteVector3(data, o + YmapReader.EntityRotation, new Vector3(r.X, r.Y, r.Z));
            WriteFloat(data, o + YmapReader.EntityRotation + 12, r.W);
            WriteFloat(data, o + YmapReader.EntityScaleXy, entity.Scale.X);
            WriteFloat(data, o + YmapReader.EntityScaleZ, entity.Scale.Z);
        }
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
