using NodeRunner.Data.Cutscenes;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Cutscenes;

namespace NodeRunner.Tests;

public class CutsceneTests
{
    [Theory]
    [InlineData("pack:/player_zero_dual-3.clip", 3, "player_zero", 0)]
    [InlineData("pack:/s_m_y_cop_01^1-0.clip", 0, "s_m_y_cop_01", 1)]
    [InlineData("pack:/g_m_m_zombie_04^2_dual-0.clip", 0, "g_m_m_zombie_04", 2)]
    [InlineData("pack:/exportcamera-12.clip", 12, "exportcamera", 0)]
    [InlineData("pack:/prop_npc_phone-1.clip", 1, "prop_npc_phone", 0)]
    public void ClipKey_NamesTheModelAndCopy(string clip, int cut, string model, int copy)
    {
        Assert.Equal((Joaat.Hash(model), copy), Cutscene.ClipKey(clip, cut));
    }

    [Fact]
    public void ClipKeys_NumberTheCopiesOfAModel()
    {
        uint cop = Joaat.Hash("s_m_y_cop_01");
        var cutscene = new Cutscene
        {
            Name = "test",
            Objects =
            [
                new CutsceneObject(2, CutsceneObjectKind.Ped, 0, cop, 0),
                new CutsceneObject(3, CutsceneObjectKind.Camera, Joaat.Hash("exportcamera"), 0, 0),
                new CutsceneObject(4, CutsceneObjectKind.Ped, 0, cop, 0),
                new CutsceneObject(5, CutsceneObjectKind.Light, 0, 0, 0),
            ],
        };
        var keys = cutscene.ClipKeys();
        Assert.Equal((cop, 0), keys[2]);
        Assert.Equal((Joaat.Hash("exportcamera"), 0), keys[3]);
        Assert.Equal((cop, 1), keys[4]);
        Assert.False(keys.ContainsKey(5));
    }

    [Fact]
    public void CutAt_GivesTheCameraCutPlaying()
    {
        var cutscene = new Cutscene { Name = "test", CameraCuts = [10f, 25f] };
        Assert.Equal((0, 0f), cutscene.CutAt(3f));
        Assert.Equal((1, 10f), cutscene.CutAt(10f));
        Assert.Equal((2, 25f), cutscene.CutAt(40f));
    }

    [Fact]
    public void CaseHash_KeepsTheCaseOfPsoNames()
    {
        Assert.Equal(0x46A8A714u, CutsceneReader.CaseHash("fTotalDuration"));
        Assert.Equal(0x4FBD037Au, CutsceneReader.CaseHash("pCutsceneObjects"));
    }
}
