using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class MetaXmlTests
{
    private const byte StructurePointer = 0x07, UnsignedInt = 0x15, Float = 0x21, FloatXyz = 0x33, CharPointer = 0x44, Hash = 0x4A;
    private const byte Array = 0x52, IntEnum = 0x62, Boolean = 0x01;
    private const uint ArrayInfo = 0x100;

    // The game hashes some structure names with their case kept.
    private static uint LiteralHash(string text)
    {
        uint hash = 0;
        foreach (char c in text)
        {
            hash += (byte)c;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    private static readonly uint RootType = LiteralHash("TestRoot");
    private static readonly uint ItemType = Joaat.Hash("TestItem");
    private static readonly uint KindEnum = Joaat.Hash("eKind");

    private static MetaDocument.StructureInfo Structure(uint hash, int size, params (uint Name, int Offset, byte Type, short RefIndex, uint RefKey)[] entries)
    {
        var info = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(info, hash);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0x18), size);
        var data = new byte[entries.Length * 16];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = data.AsSpan(i * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(e, entries[i].Name);
            BinaryPrimitives.WriteInt32LittleEndian(e[4..], entries[i].Offset);
            e[8] = entries[i].Type;
            BinaryPrimitives.WriteInt16LittleEndian(e[10..], entries[i].RefIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], entries[i].RefKey);
        }
        return new MetaDocument.StructureInfo(info, data);
    }

    private static MetaSchema Schema()
    {
        var document = new MetaDocument();
        document.Structures.Add(Structure(RootType, 64,
            (Joaat.Hash("name"), 0, Hash, 0, 0),
            (Joaat.Hash("scale"), 4, Float, 0, 0),
            (Joaat.Hash("position"), 16, FloatXyz, 0, 0),
            (ArrayInfo, 0, StructurePointer, 0, 0),
            (Joaat.Hash("items"), 32, Array, 3, 0)));
        document.Structures.Add(Structure(ItemType, 64,
            (Joaat.Hash("label"), 0, CharPointer, 0, 0),
            (Joaat.Hash("count"), 16, UnsignedInt, 0, 0),
            (Joaat.Hash("kind"), 20, IntEnum, 0, KindEnum),
            (Joaat.Hash("visible"), 24, Boolean, 0, 0),
            (ArrayInfo, 0, UnsignedInt, 0, 0),
            (Joaat.Hash("indices"), 32, Array, 4, 0),
            (ArrayInfo, 0, FloatXyz, 0, 0),
            (Joaat.Hash("corners"), 48, Array, 6, 0)));
        var enumInfo = new byte[0x18];
        BinaryPrimitives.WriteUInt32LittleEndian(enumInfo, KindEnum);
        var values = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(values, Joaat.Hash("KIND_A"));
        BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(8), Joaat.Hash("KIND_B"));
        BinaryPrimitives.WriteInt32LittleEndian(values.AsSpan(12), 5);
        document.Enums.Add(new MetaDocument.EnumInfo(enumInfo, values));
        var schema = new MetaSchema();
        schema.Add(document);
        return schema;
    }

    private static (int Block, int Offset) Target(MetaDocument document, int block, int offset)
    {
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(document.Blocks[block].Data.AsSpan(offset));
        return MetaDocument.DecodePointer(pointer);
    }

    [Fact]
    public void EmptyArrayOfAnUndescribedStructureIsAccepted()
    {
        // Like containerLods in a .ymap: no game file describes its items, and it is always empty.
        var document = new MetaDocument();
        document.Structures.Add(Structure(RootType, 32,
            (ArrayInfo, 0, 0x05, 0, Joaat.Hash("undescribed")),
            (Joaat.Hash("lods"), 0, Array, 0, 0),
            (Joaat.Hash("scale"), 16, Float, 0, 0)));
        var schema = new MetaSchema();
        schema.Add(document);

        var written = MetaXml.Build(XElement.Parse("""<TestRoot><lods itemType="undescribed" /><scale value="3" /></TestRoot>"""), schema);
        var root = MetaDocument.Read(written.Write().Content);
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(root.Blocks[root.RootBlock].Data.AsSpan(16)));

        Assert.Throws<InvalidDataException>(() => MetaXml.Build(
            XElement.Parse("""<TestRoot><lods><Item /></lods></TestRoot>"""), schema));
    }

    [Fact]
    public void SollumzStyleXmlIsWrittenWhereTheDescriptionsSay()
    {
        var xml = XElement.Parse("""
            <TestRoot>
              <name>my_ytyp</name>
              <scale value="2.5" />
              <position x="1" y="2" z="3" />
              <unknownField value="9" />
              <items>
                <Item type="TestItem">
                  <label>limbo</label>
                  <count value="7" />
                  <kind>KIND_B</kind>
                  <visible value="True" />
                  <indices>1 2
            3</indices>
                  <corners><Item>1.5,2,3</Item><Item>4, 5, 6, 0</Item></corners>
                </Item>
              </items>
            </TestRoot>
            """);

        var written = MetaXml.Build(xml, Schema());
        var document = MetaDocument.Read(written.Write().Content);
        Assert.Equal([RootType, ItemType], document.Structures.Select(s => s.NameHash));
        Assert.Single(document.Enums);

        var root = document.Blocks[document.RootBlock];
        Assert.Equal(RootType, root.Structure);
        var data = root.Data;
        Assert.Equal(Joaat.Hash("my_ytyp"), BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(40)));

        var pointers = Target(document, document.RootBlock, 32);
        var (itemBlock, itemOffset) = Target(document, pointers.Block, pointers.Offset);
        Assert.Equal(ItemType, document.Blocks[itemBlock].Structure);
        var item = document.Blocks[itemBlock].Data.AsSpan(itemOffset);
        var label = Target(document, itemBlock, itemOffset);
        Assert.Equal("limbo\0", Encoding.ASCII.GetString(document.Blocks[label.Block].Data, label.Offset, 6));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(item[16..]));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(item[20..]));
        Assert.Equal(1, item[24]);

        var indices = Target(document, itemBlock, itemOffset + 32);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(item[40..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[indices.Block].Data.AsSpan(indices.Offset + 8)));

        // Corners: 16 bytes each, the 4th value NaN when not given.
        var corners = Target(document, itemBlock, itemOffset + 48);
        var c = document.Blocks[corners.Block].Data.AsSpan(corners.Offset);
        Assert.Equal(0x33u, document.Blocks[corners.Block].Structure);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(item[56..]));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(c));
        Assert.True(float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(c[12..])));
        Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(c[24..]));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(c[28..]));
    }

    [Fact]
    public void UnknownStructuresAndEnumValuesAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => MetaXml.Build(XElement.Parse("<Other />"), Schema()));
        var xml = XElement.Parse("""<TestRoot><items><Item type="TestItem"><kind>KIND_Z</kind></Item></items></TestRoot>""");
        Assert.Throws<InvalidDataException>(() => MetaXml.Build(xml, Schema()));
    }
}
