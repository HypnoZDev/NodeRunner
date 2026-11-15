using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Tests;

public class ScenarioPreviewTests
{
    private static ModelData Part(string texture, float z) => new()
    {
        NameHash = 0,
        Meshes =
        [
            new ModelMesh
            {
                Positions = [new Vector3(0, 0, z), new Vector3(0.1f, 0, z + 0.1f), new Vector3(0, 0.1f, z)],
                Normals = new Vector3[3],
                TexCoords = new Vector2[3],
                Colors = new Vector4[3],
                Indices = [0, 1, 2],
                DiffuseTextureName = texture,
            },
        ],
    };

    [Fact]
    public void PedTakesOnePartPerComponentWithTheHeadsSkin()
    {
        var parts = new Dictionary<uint, ModelData>
        {
            [Joaat.Hash("head_000_r")] = Part("head_diff_000_a_bla", 0.6f),
            [Joaat.Hash("uppr_000_r")] = Part("uppr_diff_000_a_whi", 0f),
            [Joaat.Hash("uppr_001_r")] = Part("uppr_diff_001_a_bla", 0f),
            [Joaat.Hash("lowr_000_m")] = Part("lowr_diff_000_a_uni", -1f),
            [Joaat.Hash("accs_000_u")] = Part("accs_diff_000_a_uni", 0f),
            [Joaat.Hash("accs_001_u")] = Part("accs_diff_001_a_uni", 0f),
        };
        for (int variant = 0; variant < 20; variant++)
        {
            var ped = PedModels.Compose(parts, 1, variant)!;
            var textures = ped.Meshes.Select(m => m.DiffuseTextureName).ToList();
            Assert.Equal(["head_diff_000_a_bla", "uppr_diff_001_a_bla", "lowr_diff_000_a_uni", "accs_diff_000_a_uni"], textures);
            Assert.Equal(-1f, ped.BoundsMin.Z);
        }
        Assert.Null(PedModels.Compose(new Dictionary<uint, ModelData>(), 1, 0));
    }

    [Fact]
    public void ModelComesFromThePointThenItsTypeThenAnExample()
    {
        var files = new GameContentTests.FakeGameFiles();
        files.SetContent(@"update\update.rpf\common\data\ai\scenarios.meta", """
            <CScenarioInfoManager><Scenarios>
              <Item type="CScenarioPlayAnimsInfo"><Name>WORLD_DEER_GRAZING</Name><Models ref="ANIMAL_DEER" /></Item>
              <Item type="CScenarioVehicleInfo"><Name>WORLD_VEHICLE_AMBULANCE</Name><Models ref="NULL" /><VehicleModelSet ref="ambulance" /></Item>
              <Item type="CScenarioPlayAnimsInfo"><Name>WORLD_HUMAN_SMOKING</Name><Models ref="NULL" /></Item>
            </Scenarios></CScenarioInfoManager>
            """);
        files.SetContent(@"update\update.rpf\common\data\ai\ambientpedmodelsets.meta", """
            <CAmbientModelSets><ModelSets>
              <Item><Name>ANIMAL_DEER</Name><Models><Item><Name>A_C_DEER</Name></Item></Models></Item>
              <Item><Name>COPS</Name><Models><Item><Name>S_M_Y_COP_01</Name></Item></Models></Item>
            </ModelSets></CAmbientModelSets>
            """);
        files.SetContent(@"update\update.rpf\common\data\ai\vehiclemodelsets.meta", """
            <CAmbientModelSets><ModelSets>
              <Item><Name>ambulance</Name><Models><Item><Name>AMBULANCE</Name></Item></Models></Item>
            </ModelSets></CAmbientModelSets>
            """);
        var scenarios = ScenarioSet.Load(files, new NoRegions());
        var peds = ModelSets.LoadPeds(files);
        var vehicles = ModelSets.LoadVehicles(files);
        string? Pick(string type, uint pedSet = 0, uint vehicleSet = 0)
        {
            var point = new ScenarioPoint
            {
                Position = Vector3.Zero,
                TypeHash = Joaat.Hash(type),
                Category = ScenarioCategories.FromTypeName(type),
                PedModelSetHash = pedSet,
                VehicleModelSetHash = vehicleSet,
            };
            return ScenarioModels.Choose(point, scenarios, peds, vehicles, _ => true, 0) is { } c ? $"{c.Name}/{c.Source}" : null;
        }
        Assert.Equal("a_c_deer/Type", Pick("WORLD_DEER_GRAZING", Joaat.Hash("none")));
        Assert.Equal("s_m_y_cop_01/Point", Pick("WORLD_HUMAN_SMOKING", Joaat.Hash("COPS")));
        Assert.Equal($"{ScenarioModels.ExamplePeds[0]}/Example", Pick("WORLD_HUMAN_SMOKING", Joaat.Hash("none")));
        Assert.Equal("ambulance/Type", Pick("WORLD_VEHICLE_AMBULANCE"));
    }

    private sealed class NoRegions : IScenarioRegionReader
    {
        public ScenarioRegion Read(ReadOnlySpan<byte> data, string name) => throw new InvalidDataException();
    }
}
