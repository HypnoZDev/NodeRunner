using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class YtypWriterTests
{
    private const uint PointerBlock = 7;
    private const uint InteriorName = 0x1234;

    // A .ytyp holding one interior with two objects.
    private static byte[] Sample()
    {
        var document = new MetaDocument { Name = "sample", RootBlock = 0 };
        void Structure(uint hash, int size)
        {
            var info = new byte[0x20];
            BinaryPrimitives.WriteUInt32LittleEndian(info, hash);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0x18), size);
            document.Structures.Add(new MetaDocument.StructureInfo(info, []));
        }
        Structure(YtypReader.InteriorArchetypeStructure, 240);
        Structure(YmapReader.EntityStructure, YmapReader.EntitySize);

        void Array(byte[] data, int field, int block, int offset, int count)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(field), MetaDocument.Pointer(block, offset));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 8), (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 10), (ushort)count);
        }

        var types = new byte[64];
        Array(types, YtypReader.TypesArchetypes, 1, 0, 1);
        var archetypePointers = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(archetypePointers, MetaDocument.Pointer(2, 0));
        var interior = new byte[240];
        BinaryPrimitives.WriteUInt32LittleEndian(interior.AsSpan(YtypReader.ArchName), InteriorName);
        Array(interior, YtypReader.InteriorEntities, 3, 0, 2);
        var entityPointers = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(entityPointers, MetaDocument.Pointer(4, 0));
        BinaryPrimitives.WriteUInt64LittleEndian(entityPointers.AsSpan(8), MetaDocument.Pointer(4, YmapReader.EntitySize));
        var entities = new byte[2 * YmapReader.EntitySize];
        for (int i = 0; i < 2; i++)
        {
            int o = i * YmapReader.EntitySize;
            BinaryPrimitives.WriteUInt32LittleEndian(entities.AsSpan(o + YmapReader.EntityArchetype), (uint)(100 + i));
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityPosition), i);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityRotation + 12), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityScaleXy), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityScaleZ), 1f);
        }

        document.Blocks.Add(new MetaDocument.Block(YtypReader.MapTypesStructure, types));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock, archetypePointers));
        document.Blocks.Add(new MetaDocument.Block(YtypReader.InteriorArchetypeStructure, interior));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock, entityPointers));
        document.Blocks.Add(new MetaDocument.Block(YmapReader.EntityStructure, entities));
        return document.Write().Content;
    }

    [Fact]
    public void Moves_an_object_of_an_interior()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        Assert.Equal(InteriorName, interior.NameHash);
        var moved = interior.InteriorEntities[1];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        moved.SetPlacement(new Vector3(3, 4, 5), turn, new Vector3(2, 2, 3));

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(Vector3.Zero, back.InteriorEntities[0].Position);
        var read = back.InteriorEntities[1];
        Assert.Equal(new Vector3(3, 4, 5), read.Position);
        Assert.True(Quaternion.Dot(turn, read.Orientation) > 0.9999f);
        Assert.Equal(new Vector3(2, 2, 3), read.Scale);
    }

    [Fact]
    public void Refuses_another_file()
    {
        var interior = new YtypReader().Read(Sample()).Archetypes.Single();
        var other = new Archetype { NameHash = 99, Kind = ArchetypeKind.Interior, InteriorEntities = interior.InteriorEntities };
        Assert.Throws<InvalidDataException>(() => new YtypWriter().Write(Sample(), [other]));
    }
}
