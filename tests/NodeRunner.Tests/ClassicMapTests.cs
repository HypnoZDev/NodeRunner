using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Classic;

namespace NodeRunner.Tests;

// GTA III / Vice City / San Andreas definitions (".ide"), placements (".ipl") and load lists, written by hand.
public class ClassicMapTests
{
    [Fact]
    public void Definitions_ReadAllLayouts()
    {
        var defs = new ClassicDefinitions();
        int bad = defs.Read("""
            # comment
            objs
            100, barroad01, roads, 1, 300, 0
            101, LODroad01, roads, 2, 300, 1000, 4
            102, lae2_house, lae2, 250, 132
            end
            tobj
            103, neon, signs, 1, 100, 0, 20, 6
            104, sa_neon, signs, 150, 4, 21, 5
            end
            anim
            105, crane, cranes, crane_anim, 200, 0
            end
            txdp
            lae2, generic_parent
            end
            """);

        Assert.Equal(0, bad);
        Assert.Equal(6, defs.Models.Count);
        Assert.Equal(300, defs.Find(100)!.DrawDistance);
        Assert.Equal(1000, defs.Find("lodroad01")!.DrawDistance);
        Assert.Equal(4u, defs.Find(101)!.Flags);
        Assert.Equal((250f, 132u), (defs.Find(102)!.DrawDistance, defs.Find(102)!.Flags));
        Assert.Equal((true, 20, 6), (defs.Find(103)!.IsTimed, defs.Find(103)!.TimeOn, defs.Find(103)!.TimeOff));
        Assert.Equal((150f, 21, 5), (defs.Find(104)!.DrawDistance, defs.Find(104)!.TimeOn, defs.Find(104)!.TimeOff));
        Assert.Equal("crane_anim", defs.Find(105)!.AnimationFile);
        Assert.Equal("generic_parent", defs.TextureParents["lae2"]);
        Assert.Same(defs.Find(101), defs.LodModelOf(defs.Find(100)!));
    }

    [Fact]
    public void TextPlacements_FollowEachGame()
    {
        var gta3 = ClassicPlacements.ReadText("inst\n100, road01, 1, 2, 3, 1, 1, 1, 0, 0, 0, 1\nend\n", ClassicGame.Gta3, out int bad3);
        var vc = ClassicPlacements.ReadText("inst\n100, road01, 5, 1, 2, 3, 2, 2, 2, 0, 0, 0, 1\nend\n", ClassicGame.ViceCity, out int badVc);
        var sa = ClassicPlacements.ReadText("cull\nend\ninst\n100, road01, 0, 1, 2, 3, 0, 0, 0.7071068, 0.7071068, 7\nend\n",
            ClassicGame.SanAndreas, out int badSa);

        Assert.Equal((0, 0, 0), (bad3, badVc, badSa));
        Assert.Equal(new Vector3(1, 2, 3), gta3[0].Position);
        Assert.Equal((5, new Vector3(2)), (vc[0].Interior, vc[0].Scale));
        Assert.Equal(7, sa[0].Lod);
        // Stored inverted: +90 degrees around Z is read as -90.
        var turned = Vector3.Transform(Vector3.UnitX, sa[0].Rotation);
        Assert.True(Vector3.Distance(-Vector3.UnitY, turned) < 1e-4f);
    }

    [Fact]
    public void BinaryPlacements_AreRead()
    {
        var data = new byte[76 + 40];
        BinaryPrimitives.WriteUInt32LittleEndian(data, ClassicPlacements.BinaryMagic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 76);
        var s = data.AsSpan(76);
        BinaryPrimitives.WriteSingleLittleEndian(s, 10);
        BinaryPrimitives.WriteSingleLittleEndian(s[4..], 20);
        BinaryPrimitives.WriteSingleLittleEndian(s[8..], 30);
        BinaryPrimitives.WriteSingleLittleEndian(s[24..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], 4567);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], 12);

        var instance = Assert.Single(ClassicPlacements.ReadBinary(data));
        Assert.Equal((4567, 3, 12, new Vector3(10, 20, 30)), (instance.ModelId, instance.Interior, instance.Lod, instance.Position));
        Assert.Equal(Quaternion.Identity, instance.Rotation);
    }

    [Fact]
    public void LoadList_KeepsMapFiles()
    {
        var entries = ClassicMap.ReadLoadList("""
            # map
            IMG DATA\PATHS\CARREC.IMG
            IDE DATA\MAPS\LA\LAE2.IDE
            COLFILE 0 MODELS\COLL\GENERIC.COL
            IPL DATA\MAPS\LA\LAE2.IPL
            SPLASH loadsc0
            """).ToList();

        Assert.Equal(
        [
            ("IMG", @"data\paths\carrec.img"),
            ("IDE", @"data\maps\la\lae2.ide"),
            ("COLFILE", @"models\coll\generic.col"),
            ("IPL", @"data\maps\la\lae2.ipl"),
        ], entries);
    }

    [Fact]
    public void TimedModels_AreShownFromOnToOff()
    {
        Assert.Equal(0b1111u << 20 | 0b111111u, ClassicScene.Hours(20, 6));
        Assert.Equal(0b111u << 9, ClassicScene.Hours(9, 12));
        Assert.Equal(0xFFFFFFu, ClassicScene.Hours(0, 24));
    }

    [Fact]
    public void Collisions_GiveBoundsOfEachVersion()
    {
        static byte[] Entry(string magic, string name, float[] bounds)
        {
            var data = new byte[8 + 24 + 40];
            Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
            Encoding.ASCII.GetBytes(name).CopyTo(data, 8);
            for (int i = 0; i < bounds.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(32 + i * 4), bounds[i]);
            return data;
        }
        // COLL: radius, centre, min, max; COL3: min, max, centre, radius.
        var data = Entry("COLL", "old", [5, 1, 2, 3, -1, -2, -3, 4, 5, 6])
            .Concat(Entry("COL3", "new", [-1, -2, -3, 4, 5, 6, 1, 2, 3, 7])).ToArray();

        var bounds = ClassicCollisions.ReadBounds(data);

        Assert.Equal(new ClassicBounds("old", new Vector3(-1, -2, -3), new Vector3(4, 5, 6), new Vector3(1, 2, 3), 5), bounds[0]);
        Assert.Equal(new ClassicBounds("new", new Vector3(-1, -2, -3), new Vector3(4, 5, 6), new Vector3(1, 2, 3), 7), bounds[1]);
    }
}
