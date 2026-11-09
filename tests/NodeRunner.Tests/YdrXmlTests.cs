using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class YdrXmlTests
{
    // 4 x 4 DXT1, one level: one block of 8 bytes.
    private static byte[] Dds()
    {
        var dds = new byte[128 + 8];
        var s = dds.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x1007);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], 0x31545844);
        for (int i = 0; i < 8; i++)
            dds[128 + i] = (byte)(i * 31 + 1);
        return dds;
    }

    private const string Triangle = """
                -1.0 0.0 0.0   0.0 0.0 1.0   255 128 0 255   0.0 0.0
                1.0 0.0 0.0   0.0 0.0 1.0   10 20 30 40   1.0 0.0
                0.0 2.0 0.5   0.0 0.0 1.0   255 255 255 255   0.5 1.0
        """;

    private static string Geometry(string vertices = Triangle, string indices = "0 1 2", int shader = 0) => $"""
           <Item>
            <ShaderIndex value="{shader}" />
            <BoundingBoxMin x="-1" y="0" z="0" />
            <BoundingBoxMax x="1" y="2" z="0.5" />
            <VertexBuffer>
             <Flags value="0" />
             <Layout type="GTAV1"><Position /><Normal /><Colour0 /><TexCoord0 /></Layout>
             <Data>{vertices}</Data>
            </VertexBuffer>
            <IndexBuffer><Data>{indices}</Data></IndexBuffer>
           </Item>
        """;

    private static string Xml(string geometries, string extra = "", int hasSkin = 0) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Drawable>
         <Name>my_prop</Name>
         <BoundingSphereCenter x="0" y="1" z="0" />
         <BoundingSphereRadius value="1.5" />
         <BoundingBoxMin x="-1" y="0" z="0" />
         <BoundingBoxMax x="1" y="2" z="0.5" />
         <LodDistHigh value="60" />
         <LodDistMed value="120" />
         <FlagsHigh value="1" />
         <ShaderGroup>
          <TextureDictionary>
           <Item><Name>my_prop_d</Name><Usage>DIFFUSE</Usage><FileName>my_prop_d.dds</FileName></Item>
          </TextureDictionary>
          <Shaders>
           <Item>
            <Name>normal_spec</Name>
            <FileName>normal_spec.sps</FileName>
            <RenderBucket value="0" />
            <Parameters>
             <Item name="DiffuseSampler" type="Texture"><Name>my_prop_d</Name></Item>
             <Item name="BumpSampler" type="Texture"><Name>shared_n</Name></Item>
             <Item name="matDiffuseColor" type="Vector" x="1" y="0.5" z="0.25" w="1" />
             <Item name="2464666374" type="Vector" x="0" y="0" z="1" w="0" />
             <Item name="wind" type="Array"><Value x="1" y="2" z="3" w="4" /><Value x="5" y="6" z="7" w="8" /></Item>
            </Parameters>
           </Item>
          </Shaders>
         </ShaderGroup>
         <DrawableModelsHigh>
          <Item>
           <RenderMask value="255" />
           <HasSkin value="{hasSkin}" />
           <BoneIndex value="0" />
           <Geometries>
        {geometries}
           </Geometries>
          </Item>
         </DrawableModelsHigh>
        {extra}
        </Drawable>
        """;

    private const string Extras = """
         <DrawableModelsMedium>
          <Item>
           <RenderMask value="255" />
           <Geometries>
            <Item>
             <ShaderIndex value="0" />
             <VertexBuffer>
              <Layout type="GTAV1"><Position /></Layout>
              <Data>0 0 0
              1 0 0
              0 1 0</Data>
             </VertexBuffer>
             <IndexBuffer><Data>0 1 2</Data></IndexBuffer>
            </Item>
           </Geometries>
          </Item>
         </DrawableModelsMedium>
         <Skeleton>
          <Bones>
           <Item><Name>root</Name><Tag value="0" /><Index value="0" /><ParentIndex value="-1" /><SiblingIndex value="-1" />
            <Flags>RotX, TransZ</Flags><Translation x="0" y="0" z="0" /><Rotation x="0" y="0" z="0" w="1" /><Scale x="1" y="1" z="1" /></Item>
           <Item><Name>arm</Name><Tag value="1234" /><Index value="1" /><ParentIndex value="0" /><SiblingIndex value="-1" />
            <Flags>None</Flags><Translation x="0" y="0" z="2" /><Rotation x="0" y="0" z="0" w="1" /><Scale x="1" y="1" z="1" /></Item>
          </Bones>
         </Skeleton>
         <Lights>
          <Item>
           <Position x="0" y="0" z="1.5" />
           <Colour r="255" g="200" b="100" />
           <Intensity value="4" />
           <Type>Spot</Type>
           <Falloff value="6" />
           <ConeOuterAngle value="45" />
          </Item>
         </Lights>
         <Bounds type="Box">
          <BoxMin x="-1" y="-1" z="0" />
          <BoxMax x="1" y="1" z="2" />
          <BoxCenter x="0" y="0" z="1" />
          <SphereCenter x="0" y="0" z="1" />
          <SphereRadius value="1.7" />
          <MaterialIndex value="12" />
         </Bounds>
        """;

    private static XmlExport Export(string xml) =>
        new("my_prop.ydr.xml", xml, path => path == "my_prop/my_prop_d.dds" ? Dds() : null);

    private static ulong U64(byte[] c, int o) => BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(o));
    private static int At(ulong pointer) => (int)(pointer - 0x50000000);

    [Fact]
    public void ConvertsADrawable()
    {
        var (header, content) = ResourceFile.Read(YdrXml.Convert(Export(Xml(Geometry(), Extras))));
        Assert.Equal(165, header.Version);

        var model = DrawableReader.Read(content, header, "my_prop", ".ydr")[Joaat.Hash("my_prop")];
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal([new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 2, 0.5f)], mesh.Positions);
        Assert.Equal([0, 1, 2], mesh.Indices.Select(i => (int)i));
        Assert.Equal(new Vector2(0.5f, 1), mesh.TexCoords[2]);
        Assert.Equal(new Vector4(10, 20, 30, 40) / 255f, mesh.Colors[1]);
        Assert.Equal("my_prop_d", mesh.DiffuseTextureName);
        Assert.Equal("shared_n", mesh.ShaderTextures[Joaat.Hash("BumpSampler")]);
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1), mesh.DiffuseColor);
        Assert.Equal(Joaat.Hash("normal_spec"), mesh.ShaderHash);
        Assert.Equal(new Vector3(1, 2, 0.5f), model.BoundsMax);
        var texture = Assert.Single(model.EmbeddedTextures).Value;
        Assert.Equal(Dds().AsSpan(128).ToArray(), texture.Data);

        // A parameter named by its number (unknown name): kept as is. 5 parameters of 0, 0, 1, 1 and 2 vectors.
        int group = At(U64(content, 0x10));
        int parameters = At(U64(content, At(U64(content, At(U64(content, group + 0x10))))));
        Assert.Equal(2464666374u, BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(parameters + 16 * 5 + 16 * 4 + 4 * 3)));

        // Raw fields: name, medium level, lights, collision, skeleton.
        Assert.Equal("my_prop.#dr", Text(content, At(U64(content, 0xA8))));
        Assert.NotEqual(0ul, U64(content, 0x58));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(0xB8)));
        int light = At(U64(content, 0xB0));
        Assert.Equal(2, content[light + 0x26]);
        Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(light + 0x2C)));
        int bound = At(U64(content, 0xC8));
        Assert.Equal(3, content[bound + 0x10]);
        Assert.Equal(12, content[bound + 0x4C]);
        int skeleton = At(U64(content, 0x18));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(skeleton + 0x5E)));
        // Inverse bind pose of the arm: 2 m up, so its inverse moves 2 m down.
        int inverse = At(U64(content, skeleton + 0x28));
        Assert.Equal(-2f, BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(inverse + 64 + 56)));
        int bones = At(U64(content, skeleton + 0x20));
        Assert.Equal(0x41, BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(bones + 0x40)));
        Assert.Equal("arm", Text(content, At(U64(content, bones + 80 + 0x38))));
    }

    [Fact]
    public void WritesSeveralGeometriesAndAMinimalDrawable()
    {
        var (header, content) = ResourceFile.Read(YdrXml.Convert(Export(Xml(Geometry() + Geometry(indices: "2 1 0")))));
        var model = DrawableReader.Read(content, header, "my_prop", ".ydr")[Joaat.Hash("my_prop")];
        Assert.Equal(2, model.Meshes.Count);
        Assert.Equal([2, 1, 0], model.Meshes[1].Indices.Select(i => (int)i));
        Assert.Equal(0ul, U64(content, 0x18));
        Assert.Equal(0ul, U64(content, 0xC8));
    }

    [Fact]
    public void RejectsBadInput()
    {
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(Geometry(), hasSkin: 1))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(Geometry(indices: "0 1 3")))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(Geometry(indices: "0 1")))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(Geometry(shader: 1)))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(Geometry(vertices: "0 0 0   0 0 1   255 255 255 255")))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export(Xml(""))));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(Export("<TextureDictionary />")));
        Assert.Throws<InvalidDataException>(() => YdrXml.Convert(new XmlExport("my_prop.ydr.xml", Xml(Geometry()), _ => null)));
    }

    private static string Text(byte[] content, int at)
    {
        int end = Array.IndexOf(content, (byte)0, at);
        return Encoding.ASCII.GetString(content, at, end - at);
    }
}
