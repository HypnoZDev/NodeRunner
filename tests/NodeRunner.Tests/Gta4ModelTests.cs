using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Gta4;

namespace NodeRunner.Tests;

// GTA IV models built in memory (no game file): a drawable with one textured triangle, then the same drawable as the
// piece of a fragment, moved by its bone.
public class Gta4ModelTests
{
    private const int SystemSize = 0x2000;
    private const int DrawableAt = 0x400;

    private static uint Sys(int offset) => 0x50000000u | (uint)offset;

    private static uint Gfx(int offset) => 0x60000000u | (uint)offset;

    // One triangle: position, normal, colour (BGRA), texture coordinates (36-byte vertices, as the game's).
    private static byte[] Build(Action<byte[]>? more = null)
    {
        var data = new byte[SystemSize + 0x200];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), value);
        void Text(int at, string text) => Encoding.ASCII.GetBytes(text + "\0").CopyTo(data, at);
        int b = DrawableAt;

        U32(b + 0x08, Sys(b + 0x100));
        F(b + 0x20, -1); F(b + 0x24, -1); F(b + 0x28, 0);
        F(b + 0x30, 1); F(b + 0x34, 1); F(b + 0x38, 2);
        U32(b + 0x40, Sys(b + 0x200));

        U32(b + 0x108, Sys(b + 0x120)); U16(b + 0x10C, 1);
        U32(b + 0x120, Sys(b + 0x140));
        U32(b + 0x140 + 0x14, Sys(b + 0x1C0)); U32(b + 0x140 + 0x1C, 1);
        U32(b + 0x140 + 0x24, Sys(b + 0x1D0)); U32(b + 0x140 + 0x34, Sys(b + 0x1E0)); U32(b + 0x140 + 0x44, Sys(b + 0x1F0));
        U32(b + 0x1C0, Sys(b + 0x380));
        U32(b + 0x1E0, Joaat.Hash("texturesampler"));
        Text(b + 0x1F0, "gta_default");
        U32(b + 0x380 + 0x14, Sys(b + 0x3A0));
        Text(b + 0x3A0, "brick01");

        U32(b + 0x200, Sys(b + 0x210)); U16(b + 0x204, 1);
        U32(b + 0x210, Sys(b + 0x220));
        U32(b + 0x224, Sys(b + 0x260)); U16(b + 0x228, 1); U32(b + 0x230, Sys(b + 0x270));
        U32(b + 0x260, Sys(b + 0x280));
        U32(b + 0x280 + 0x0C, Sys(b + 0x2E0)); U32(b + 0x280 + 0x1C, Sys(b + 0x320));
        U16(b + 0x2E0 + 4, 3); U32(b + 0x2E0 + 0x0C, 36); U32(b + 0x2E0 + 0x10, Sys(b + 0x340)); U32(b + 0x2E0 + 0x18, Gfx(0));
        U32(b + 0x320 + 4, 3); U32(b + 0x320 + 8, Gfx(0x100));
        U32(b + 0x340, 0x59); // position, normal, colour, first texture coordinates
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(b + 0x348), 0x6755555555996996);

        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 2)];
        for (int i = 0; i < 3; i++)
        {
            int v = SystemSize + i * 36;
            F(v, positions[i].X); F(v + 4, positions[i].Y); F(v + 8, positions[i].Z);
            F(v + 20, 1); // normal +Z
            data[v + 24] = 0x10; data[v + 25] = 0x20; data[v + 26] = 0xFF; data[v + 27] = 0xFF; // B, G, R, A
            F(v + 28, i * 0.5f); F(v + 32, 1);
            U16(SystemSize + 0x100 + i * 2, (ushort)i);
        }
        more?.Invoke(data);
        return data;
    }

    [Fact]
    public void ReadDrawable_DecodesMeshShaderAndTexture()
    {
        var res = new Gta4Drawables.Resource(Build(), SystemSize);

        var model = Gta4Drawables.ReadDrawable(res, DrawableAt, 42)!;

        Assert.Equal(42u, model.NameHash);
        Assert.Equal(new Vector3(-1, -1, 0), model.BoundsMin);
        Assert.Equal(new Vector3(1, 1, 2), model.BoundsMax);
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal([new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 2)], mesh.Positions);
        Assert.All(mesh.Normals, n => Assert.Equal(Vector3.UnitZ, n));
        Assert.Equal(new Vector2(0.5f, 1), mesh.TexCoords[1]);
        Assert.Equal(new Vector4(1f, 0x20 / 255f, 0x10 / 255f, 1f), mesh.Colors[0]);
        Assert.Equal([(ushort)0, (ushort)1, (ushort)2], mesh.Indices);
        Assert.Equal("brick01", mesh.DiffuseTextureName);
        Assert.Equal(Joaat.Hash("gta_default"), mesh.ShaderHash);
        Assert.Equal(MeshBlendMode.Opaque, mesh.BlendMode);
    }

    [Fact]
    public void ReadDrawable_BadIndex_DropsTheMesh()
    {
        var data = Build(d => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(SystemSize + 0x104), 9));

        Assert.Null(Gta4Drawables.ReadDrawable(new Gta4Drawables.Resource(data, SystemSize), DrawableAt, 1));
    }

    [Fact]
    public void ReadFragment_PlacesEachPieceOnItsBone()
    {
        // Fragment header at 0: an empty main drawable with a two-bone skeleton, one piece on bone 1 (10 m along X).
        var data = Build(d =>
        {
            void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), value);
            void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), value);
            U32(0xB4, Sys(0x900));
            U32(0x900 + 0x0C, Sys(0x980));
            U32(0x980 + 0x10, Sys(0x9C0));
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x980 + 0x14), 2);
            for (int bone = 0; bone < 2; bone++)
            {
                int m = 0x9C0 + bone * 64;
                F(m, 1); F(m + 20, 1); F(m + 40, 1);
                F(m + 48, bone * 10f);
            }
            U32(0xD4, Sys(0xA80));
            d[0x1F3] = 1;
            d[0x1F6] = 1;
            U32(0xA80, Sys(0xAA0));
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0xAA0 + 0x0E), 1);
            U32(0xAA0 + 0x90, Sys(DrawableAt));
        });

        var model = Gta4Drawables.ReadFragment(new Gta4Drawables.Resource(data, SystemSize), 7)!;

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(new Vector3(9, -1, 0), mesh.Positions[0]);
        Assert.Equal(new Vector3(9, -1, 0), model.BoundsMin);
        Assert.Equal(new Vector3(11, 1, 2), model.BoundsMax);
        Assert.Equal("brick01", mesh.DiffuseTextureName);
    }
}

// The GTA IV map turned into the maps the 3D view draws: levels of detail, LOD links, model files.
public class Gta4SceneTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noderunner-iv-scene-" + Guid.NewGuid().ToString("N"));

    public Gta4SceneTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Build_LinksLevelsOfDetailAndLocatesModels()
    {
        var map = new Gta4Map();
        map.Definitions.Read("""
            objs
            hd_house, house, 100, 0, 0, -5, -5, 0, 5, 5, 8, 0, 0, 4, 8, null
            lod_house, house_lod, 400, 0, 0, -5, -5, 0, 5, 5, 8, 0, 0, 4, 8, area_slod_grp
            slod_area, area_lod, 2000, 0, 0, -50, -50, 0, 50, 50, 20, 0, 0, 10, 60, area_slod_grp
            end
            """);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        var main = new Gta4MapFile(@"pc\data\maps\area.wpl",
        [
            new Gta4Instance(Joaat.Hash("slod_area"), new Vector3(0, 0, 0), Quaternion.Identity, 0, -1, -1),
            new Gta4Instance(Joaat.Hash("lod_house"), new Vector3(10, 0, 0), rotation, 0, 0, -1),
        ]);
        main.LodFile = main;
        var streamed = new Gta4MapFile(@"pc\data\maps\area.img\area_stream0.wpl",
        [
            new Gta4Instance(Joaat.Hash("hd_house"), new Vector3(10, 0, 0), rotation, 0, 1, 150),
            new Gta4Instance(Joaat.Hash("hd_house"), new Vector3(90, 0, 0), Quaternion.Identity, 0, -1, -1),
        ]);
        streamed.LodFile = main;
        map.Files.AddRange([main, streamed]);
        using var files = new Gta4FileSystem();
        files.Open(_folder);

        var scene = Gta4Scene.Build(map, files, Gta4Episode.Base);

        var entities = scene.Library.EntitiesAround(Vector2.Zero, 1000).ToDictionary(e => (e.ArchetypeHash, e.Position.X));
        var slod = entities[(Joaat.Hash("slod_area"), 0)];
        var lod = entities[(Joaat.Hash("lod_house"), 10)];
        var hd = entities[(Joaat.Hash("hd_house"), 10)];
        var orphan = entities[(Joaat.Hash("hd_house"), 90)];
        Assert.Equal(LodLevel.Slod1, slod.LodLevel);
        Assert.Equal(LodLevel.Lod, lod.LodLevel);
        Assert.Equal(LodLevel.Hd, hd.LodLevel);
        Assert.Equal(LodLevel.OrphanHd, orphan.LodLevel);
        Assert.Same(slod, lod.Parent);
        Assert.Same(lod, hd.Parent);
        Assert.Null(orphan.Parent);
        Assert.True(Quaternion.Dot(rotation, hd.Orientation) > 0.9999f);
        Assert.Equal(150, scene.Library.DrawDistance(hd));
        Assert.Equal(100, scene.Library.DrawDistance(orphan));

        var archetype = scene.Library.GetArchetype(Joaat.Hash("lod_house"))!;
        Assert.Equal(ArchetypeAssetType.DrawableDictionary, archetype.AssetType);
        Assert.Equal(Joaat.Hash("area_slod_grp"), archetype.DrawableDictionaryHash);
        Assert.Equal(Joaat.Hash("house_lod"), archetype.TextureDictionaryHash);
        Assert.Equal(ArchetypeAssetType.Drawable, scene.Library.GetArchetype(Joaat.Hash("hd_house"))!.AssetType);
    }
}
