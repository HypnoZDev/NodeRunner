using System.Buffers.Binary;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PackManifestTests
{
    private static readonly uint Bench = Joaat.Hash("my_bench"), Chair = Joaat.Hash("prop_chair_01a"), Room = Joaat.Hash("my_int");

    private static string? TypeFile(uint archetype) =>
        archetype == Bench ? "my_props" : archetype == Chair ? "v_res_chairs" : archetype == Room ? "my_int" : null;

    [Fact]
    public void MapsUsingTheResourceTypesListAllTheirTypes()
    {
        var xml = PackManifest.Build(
            [
                new("my_map", [Chair, Bench, Bench, Room], HasInterior: true),
                new("game_only", [Chair], HasInterior: false),
            ],
            [new("my_props", []), new("my_int", [new PackManifest.Interior(Room, [Bench, Chair, Room])])],
            TypeFile)!;

        var map = Assert.Single(xml.Element("imapDependencies_2")!.Elements("Item"));
        Assert.Equal("my_map", map.Element("imapName")!.Value);
        Assert.Equal("INTERIOR_DATA", map.Element("manifestFlags")!.Value);
        Assert.Equal(["v_res_chairs", "my_props", "my_int"], map.Element("itypDepArray")!.Elements().Select(e => e.Value));

        var type = Assert.Single(xml.Element("itypDependencies_2")!.Elements("Item"));
        Assert.Equal("my_int", type.Element("itypName")!.Value);
        Assert.Equal(["my_props", "v_res_chairs"], type.Element("itypDepArray")!.Elements().Select(e => e.Value));
        Assert.Equal(PackManifest.HashName(Room), xml.Element("Interiors")!.Element("Item")!.Element("Name")!.Value);
    }

    [Fact]
    public void NothingIsWrittenWithoutTypesOfTheResource()
    {
        Assert.Null(PackManifest.Build([new("my_map", [Chair], false)], [], TypeFile));
        Assert.Null(PackManifest.Build([new("my_map", [Chair], false)], [new("my_props", [])], TypeFile));
    }

    // A PSO file holding only descriptions: root (hash, flags, array of hashes), its enum of flags.
    private static byte[] Schema(uint root, uint flagsEnum)
    {
        static byte[] Field(uint name, byte type, byte subtype, int offset, uint reference)
        {
            var f = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(f, name);
            f[4] = type;
            f[5] = subtype;
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(6), (ushort)offset);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), reference);
            return f;
        }
        var fields = new[]
        {
            Field(Joaat.Hash("itypName"), 0x0B, 7, 0, 0),
            Field(0x100, 0x0E, 0, 0, flagsEnum),
            Field(Joaat.Hash("manifestFlags"), 0x0F, 0, 4, 0x00200001),
            Field(0x100, 0x0B, 7, 0, 0),
            Field(Joaat.Hash("itypDepArray"), 0x0D, 0, 8, 3),
        };
        var structure = new byte[12 + fields.Length * 12];
        BinaryPrimitives.WriteUInt32BigEndian(structure, (uint)fields.Length);
        BinaryPrimitives.WriteInt32BigEndian(structure.AsSpan(4), 24);
        for (int i = 0; i < fields.Length; i++)
            fields[i].CopyTo(structure, 12 + i * 12);
        var enumInfo = new byte[4 + 16];
        BinaryPrimitives.WriteUInt32BigEndian(enumInfo, 0x01000002);
        BinaryPrimitives.WriteUInt32BigEndian(enumInfo.AsSpan(4), Joaat.Hash("OTHER"));
        BinaryPrimitives.WriteInt32BigEndian(enumInfo.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(enumInfo.AsSpan(12), Joaat.Hash("INTERIOR_DATA"));
        BinaryPrimitives.WriteInt32BigEndian(enumInfo.AsSpan(16), 3);

        var section = new byte[12 + 16 + structure.Length + enumInfo.Length];
        BinaryPrimitives.WriteUInt32BigEndian(section, 0x50534348);
        BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(4), section.Length);
        BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(12), root);
        BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(16), 28);
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(20), flagsEnum);
        BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(24), 28 + structure.Length);
        structure.CopyTo(section, 28);
        enumInfo.CopyTo(section, 28 + structure.Length);
        return section;
    }

    [Fact]
    public void PsoIsWrittenWhereTheDescriptionsSay()
    {
        uint root = Joaat.Hash("CItypDependencies"), flagsEnum = Joaat.Hash("eManifestFlags");
        var schema = new PsoSchema();
        schema.Add(Schema(root, flagsEnum));
        var file = PsoXml.Build(XElement.Parse("""
            <CItypDependencies>
              <itypName>my_int</itypName>
              <manifestFlags>INTERIOR_DATA</manifestFlags>
              <itypDepArray><Item>my_props</Item><Item>hash_12345678</Item></itypDepArray>
            </CItypDependencies>
            """), schema);

        var xml = PsoDocument.Parse(file, ["itypName", "manifestFlags", "itypDepArray", "CItypDependencies"]);
        Assert.Equal("CItypDependencies", xml.Name.LocalName);
        Assert.Equal(PackManifest.HashName(Joaat.Hash("my_int")), xml.Element("itypName")!.Value);
        Assert.Equal("8", xml.Element("manifestFlags")!.Value); // Bit 3.
        Assert.Equal([PackManifest.HashName(Joaat.Hash("my_props")), "hash_12345678"],
            xml.Element("itypDepArray")!.Elements("Item").Select(e => e.Value));

        // The schema is copied: the written file describes itself.
        var again = new PsoSchema();
        again.Add(file);
        Assert.Equal(2, again.Count);
    }
}
