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

    // A .ytyp holding one interior with three objects, the last two in its room.
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
        Structure(YtypReader.RoomStructure, YtypReader.RoomSize);

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
        Array(interior, YtypReader.InteriorEntities, 3, 0, 3);
        Array(interior, YtypReader.InteriorRooms, 5, 0, 1);
        var entityPointers = new byte[24];
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(entityPointers.AsSpan(i * 8), MetaDocument.Pointer(4, i * YmapReader.EntitySize));
        var room = new byte[YtypReader.RoomSize];
        Array(room, YtypReader.RoomAttachedObjects, 6, 0, 2);
        var roomObjects = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(roomObjects, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(roomObjects.AsSpan(4), 2);
        var entities = new byte[3 * YmapReader.EntitySize];
        for (int i = 0; i < 3; i++)
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
        document.Blocks.Add(new MetaDocument.Block(YtypReader.RoomStructure, room));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock + 1, roomObjects));
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

    [Fact]
    public void Deletes_an_object_and_renumbers_its_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        Assert.Equal(0, interior.InteriorEntities[2].InteriorRoom);
        var library = new MapLibrary();
        library.SetDeleted([interior.InteriorEntities[1]], true);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(2, back.InteriorEntities.Count);
        Assert.Equal([100u, 102u], back.InteriorEntities.Select(e => e.ArchetypeHash));
        Assert.Equal(-1, back.InteriorEntities[0].InteriorRoom);
        Assert.Equal(0, back.InteriorEntities[1].InteriorRoom);
    }

    [Fact]
    public void Adds_an_object_to_a_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var library = new MapLibrary();
        library.SetDeleted([interior.InteriorEntities[0]], true);
        interior.AddInteriorEntity(new MapEntity
        {
            ArchetypeHash = 555, Position = new Vector3(1, 2, 3), StoredRotation = Quaternion.Identity, InteriorRoom = 0,
            LodDistance = 50, LodLevel = LodLevel.OrphanHd,
        });

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([101u, 102u, 555u], back.InteriorEntities.Select(e => e.ArchetypeHash));
        var added = back.InteriorEntities[2];
        Assert.Equal(new Vector3(1, 2, 3), added.Position);
        Assert.Equal(0, added.InteriorRoom);
        Assert.Equal(50f, added.LodDistance);
        Assert.All(back.InteriorEntities.Skip(1), e => Assert.Equal(0, e.InteriorRoom));
    }
}
