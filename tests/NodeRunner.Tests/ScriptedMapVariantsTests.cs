using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Tests;

public class ScriptedMapVariantsTests
{
    [Fact]
    public void Named_states_keep_the_default_version()
    {
        var maps = Named(
            "showroom_closed", "showroom_open", "showroom_open_props",
            "tree_start", "tree_end",
            "tunnel_hole", "tunnel_hole_swap",
            "club_forsale", "club_madonna",
            "x_mansion_original", "x_props_original", "x_mansion_shared", "x_mansion_private", "x_mansion_generic", "x_mansion_furniture",
            "hatch_01_cutscene", "door_open_lonely",
            "sp_fake_interior", "sp_real_interior");

        var hidden = ScriptedMapVariants.DefaultHidden(maps, []);

        Assert.Equal(
            ["club_madonna", "hatch_01_cutscene", "showroom_open", "showroom_open_props", "sp_fake_interior", "tree_end", "tunnel_hole_swap",
             "x_mansion_generic", "x_mansion_original", "x_props_original"],
            hidden.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Before_the_mansions_pack_the_original_houses_stand()
    {
        var maps = Named(
            "hei_ch1_mansion_original", "hei_ch1_mansion_original_lod", "hei_ch1_props_original", "hei_ch1_mansion_shared", "hei_ch1_mansion_private",
            "hei_ch1_mansion_generic", "hei_ch1_mansion_furniture", "hei_ch1_roads_mansion", "hei_ch1_roads_original", "hei_mansion_detail",
            "showroom_closed", "showroom_open");

        var hidden = ScriptedMapVariants.DefaultHidden(maps, [], withMansions: false);

        Assert.Equal(
            ["hei_ch1_mansion_furniture", "hei_ch1_mansion_generic", "hei_ch1_mansion_private", "hei_ch1_mansion_shared", "hei_ch1_roads_mansion", "showroom_open"],
            hidden.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Interiors_at_the_same_spot_keep_the_first_style()
    {
        var spot = new Vector3(-787.8f, 334.9f, 215.8f);
        var maps = new List<(string, MapData)>
        {
            ("apa_02_a", WithInterior(2, spot + new Vector3(0.1f, 0f, 0f))),
            ("apa_01_a", WithInterior(1, spot)),
            ("apa_03_a", WithInterior(3, spot)),
            ("yacht", WithInterior(4, new Vector3(-3500f, 1500f, 5f))),
        };

        var hidden = ScriptedMapVariants.DefaultHidden(maps, []);

        Assert.Equal(["apa_02_a", "apa_03_a"], hidden.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Interior_already_placed_by_the_regular_map_wins()
    {
        var spot = new Vector3(10f, 20f, 30f);
        var maps = new List<(string, MapData)> { ("variant", WithInterior(2, spot)) };

        var hidden = ScriptedMapVariants.DefaultHidden(maps, [WithInterior(1, spot)]);

        Assert.Equal(["variant"], hidden);
    }

    private static List<(string, MapData)> Named(params string[] names) =>
        names.Select(n => (n, new MapData { NameHash = 0, Entities = [] })).ToList();

    private static MapData WithInterior(uint archetype, Vector3 position) => new()
    {
        NameHash = archetype,
        Entities = [new MapEntity { ArchetypeHash = archetype, Position = position, IsInterior = true }],
    };
}
