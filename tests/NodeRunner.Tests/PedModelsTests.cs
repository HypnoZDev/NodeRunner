using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.Tests;

// A ped put together from named parts, dressed by default or with chosen clothes.
public class PedModelsTests
{
    private static ModelData Part(string name, string texture) => new()
    {
        NameHash = Joaat.Hash(name),
        Meshes =
        [
            new ModelMesh
            {
                Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
                Colors = [Vector4.One, Vector4.One, Vector4.One],
                Indices = [0, 1, 2],
                DiffuseTextureName = texture,
            },
        ],
    };

    private static readonly Dictionary<uint, ModelData> Parts = new[]
    {
        Part("head_000_r", "head_diff_000_a_whi"),
        Part("uppr_000_u", "uppr_diff_000_a_uni"),
        Part("uppr_008_r", "uppr_diff_008_a_whi"),
        Part("lowr_000_u", "lowr_diff_000_a_uni"),
    }.ToDictionary(p => p.NameHash);

    private static IEnumerable<string?> Textures(ModelData? ped) => ped!.Meshes.Select(m => m.DiffuseTextureName);

    [Fact]
    public void WithoutOutfit_TakesTheFirstOfEachComponent()
    {
        var ped = PedModels.Compose(Parts.ContainsKey, Parts.GetValueOrDefault, 1, null);

        Assert.Equal(["head_diff_000_a_whi", "uppr_diff_000_a_uni", "lowr_diff_000_a_uni"], Textures(ped));
    }

    [Fact]
    public void Outfit_PicksTheDrawableAndItsColour()
    {
        var outfit = new Dictionary<int, (int, int)> { [3] = (8, 1) };

        var ped = PedModels.Compose(Parts.ContainsKey, Parts.GetValueOrDefault, 1, null, outfit);

        Assert.Equal(["head_diff_000_a_whi", "uppr_diff_008_b_whi", "lowr_diff_000_a_uni"], Textures(ped));
        // The part as read is left as it was.
        Assert.Equal("uppr_diff_008_a_whi", Parts[Joaat.Hash("uppr_008_r")].Meshes[0].DiffuseTextureName);
    }

    [Fact]
    public void Outfit_KeepsTheFirstColourWhenTheOtherIsMissing_AndTheDefaultWhenTheDrawableIsMissing()
    {
        var outfit = new Dictionary<int, (int, int)> { [3] = (8, 2), [4] = (5, 0) };

        var ped = PedModels.Compose(Parts.ContainsKey, Parts.GetValueOrDefault, 1, null, outfit, _ => false);

        Assert.Equal(["head_diff_000_a_whi", "uppr_diff_008_a_whi", "lowr_diff_000_a_uni"], Textures(ped));
    }
}
