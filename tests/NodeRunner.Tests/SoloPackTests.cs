using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Native.Dlc;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class SoloPackTests
{
    [Theory]
    [InlineData("ma-carte", "ma_carte")]
    [InlineData("Ma Carte", "ma_carte")]
    [InlineData("noderunner_map", "noderunner_map")]
    public void ToPackName_GivesLowerCaseLettersDigitsAndUnderscores(string text, string expected)
    {
        Assert.Equal(expected, SoloPack.ToPackName(text));
    }

    [Fact]
    public void Setup_StartsTheChangeSetAtStartupAfterTheGamesPacks()
    {
        var setup = new DlcManifestReader().ReadSetup(Encoding.UTF8.GetBytes(SoloPack.BuildSetup("ma_carte", 42, new DateTime(2026, 11, 15))));

        Assert.Equal("dlc_ma_carte", setup.DeviceName);
        Assert.Equal("ma_carte", setup.Name);
        Assert.Equal("content.xml", setup.ContentFile);
        Assert.Equal(42, setup.Order);
        Assert.Equal(["MA_CARTE_AUTOGEN"], setup.ChangeSetGroups["GROUP_STARTUP"]);
    }

    [Fact]
    public void Content_DeclaresTheMapArchiveAsOverlayAndEnablesEveryFile()
    {
        string text = SoloPack.BuildContent("ma_carte", ["Mon_Interieur.ytyp"], hasAudioGameData: true);
        var content = new DlcManifestReader().ReadContent(Encoding.UTF8.GetBytes(text));

        Assert.Equal(
            [
                ("dlc_ma_carte:/%PLATFORM%/levels/gta5/ma_carte.rpf", "RPF_FILE"),
                ("dlc_ma_carte:/%PLATFORM%/levels/gta5/mon_interieur.ityp", "DLC_ITYP_REQUEST"),
                ("dlc_ma_carte:/%PLATFORM%/audio/ma_carte_game.dat", "AUDIO_GAMEDATA"),
            ],
            content.DataFiles.Select(f => (f.FileName, f.FileType)));
        Assert.Equal(content.DataFiles.Select(f => f.FileName), content.ChangeSets["MA_CARTE_AUTOGEN"].FilesToEnable);

        var archive = XDocument.Parse(text).Root!.Element("dataFiles")!.Elements("Item").First();
        Assert.Equal("true", archive.Element("overlay")!.Attribute("value")!.Value);
        Assert.Equal("CONTENTS_DLC_MAP_DATA", archive.Element("contents")!.Value);
    }

    [Fact]
    public void PathsInsideThePack_MatchTheDeclaredFiles()
    {
        Assert.Equal("x64/levels/gta5/ma_carte.rpf", SoloPack.MapArchivePath("ma_carte"));
        Assert.Equal("x64/audio/ma_carte_game.dat151.rel", SoloPack.AudioGameDataPath("ma_carte"));
        Assert.Equal("dlcpacks:/ma_carte/", SoloPack.ListEntry("ma_carte"));
    }
}
