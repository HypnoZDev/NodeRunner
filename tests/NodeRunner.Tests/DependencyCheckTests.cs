using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public class DependencyCheckTests
{
    private static uint H(string name) => Joaat.Hash(name);

    // The game has the model "prop_game" (type, drawable, textures) and nothing else.
    private static readonly DependencyCheck.GameFiles Game = new(h => h == H("prop_game"), h => h == H("prop_game"),
        _ => false, _ => false, h => h == H("prop_game_txd"));

    private static Archetype Type(string name, string? textures = null, ArchetypeAssetType asset = ArchetypeAssetType.Drawable) => new()
    {
        NameHash = H(name),
        AssetNameHash = H(name),
        AssetType = asset,
        TextureDictionaryHash = textures is null ? 0 : H(textures),
    };

    private static MapData Map(params string[] models) => new()
    {
        NameHash = 1,
        Entities = [.. models.Select(m => new MapEntity { ArchetypeHash = H(m), Position = Vector3.Zero })],
    };

    private static List<DependencyCheck.Kind> Kinds(string[] files, IReadOnlyList<(string, MapTypes)>? types = null,
        IReadOnlyList<(string, MapData)>? maps = null, Func<string, long?>? memory = null) =>
        [.. DependencyCheck.Check(files, types ?? [], maps ?? [], [], Game, memory ?? (_ => 1000)).Select(i => i.Kind)];

    [Fact]
    public void ACompleteResourceHasNothingToReport()
    {
        var types = new MapTypes { NameHash = 2, Archetypes = [Type("my_prop", "my_txd"), Type("my_embedded", "my_embedded")] };
        var kinds = Kinds([@"s\my_prop.ydr", @"s\my_txd.ytd", @"s\my_embedded.ydr", @"s\t.ytyp", @"s\m.ymap", @"s\screen.gfx"],
            [("t.ytyp", types)], [("m.ymap", Map("my_prop", "prop_game"))]);
        Assert.Empty(kinds);
    }

    [Fact]
    public void MissingLinksAreFound()
    {
        var types = new MapTypes { NameHash = 2, Archetypes = [Type("no_model"), Type("my_prop", "lost_txd")] };
        var kinds = Kinds([@"a\my_prop.ydr", @"b\my_prop.ydr", @"a\lonely.ydr", @"a\notes.txt", @"a\huge.ytd"],
            [("t.ytyp", types)], [("m.ymap", Map("unknown_model"))], f => f.EndsWith("huge.ytd") ? 40 << 20 : 1000);
        Assert.Equal(
        [
            DependencyCheck.Kind.DuplicateFile, DependencyCheck.Kind.NotStreamed, DependencyCheck.Kind.Oversized,
            DependencyCheck.Kind.MissingModel, DependencyCheck.Kind.MissingTextures, DependencyCheck.Kind.UnknownArchetype,
            DependencyCheck.Kind.ModelWithoutArchetype,
        ], kinds);
    }

    [Fact]
    public void TheManifestIsChecked()
    {
        string[] files = ["fxmanifest.lua", "stream/a.ymap", "audio/x_game.dat151.rel", "data/handling.meta"];
        Assert.Empty(DependencyCheck.CheckManifest("fxmanifest.lua",
            "fx_version 'cerulean'\nthis_is_a_map 'yes'\ndata_file 'AUDIO_GAMEDATA' 'audio/x_game.dat'\ndata_file 'HANDLING_FILE' 'data/*.meta'\n"
            + "data_file 'DLC_ITYP_REQUEST' 'my.ytyp'\nfiles { 'data/handling.meta' }\nclient_script '@lib/init.lua'", files, streamsMaps: true));

        var kinds = DependencyCheck.CheckManifest("fxmanifest.lua", "game 'gta5'\ndata_file 'AUDIO_GAMEDATA' 'audio/y_game.dat'\nfiles { 'gone.meta' }",
            files, streamsMaps: true).Select(i => i.Kind);
        Assert.Equal([DependencyCheck.Kind.NoFxVersion, DependencyCheck.Kind.MissingDataFile, DependencyCheck.Kind.MissingListedFile,
            DependencyCheck.Kind.NotAMap], kinds);
        Assert.Equal([DependencyCheck.Kind.NoManifest], DependencyCheck.CheckManifest(null, null, files, false).Select(i => i.Kind));
    }
}
