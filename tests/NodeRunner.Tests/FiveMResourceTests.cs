using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class FiveMResourceTests
{
    [Theory]
    [InlineData("noderunner_map", true)]
    [InlineData("my-map2", true)]
    [InlineData("2map", true)]
    [InlineData("", false)]
    [InlineData("My_Map", false)]
    [InlineData("my map", false)]
    [InlineData("_map", false)]
    [InlineData("carte_modifiée", false)]
    public void Checks_resource_names(string name, bool valid) =>
        Assert.Equal(valid, FiveMResource.IsValidName(name));

    [Theory]
    [InlineData("Carte modifiée", "carte_modifiee")]
    [InlineData("  Pillbox Hill (v2) ", "pillbox_hill_v2")]
    [InlineData("!!!", FiveMResource.DefaultName)]
    [InlineData("my-map", "my-map")]
    public void Makes_valid_names(string text, string expected)
    {
        string name = FiveMResource.ToValidName(text);
        Assert.Equal(expected, name);
        Assert.True(FiveMResource.IsValidName(name));
    }

    [Fact]
    public void Manifest_marks_a_map_resource()
    {
        string manifest = FiveMResource.BuildManifest("Inside Rockstar", "0.1.0", ["dt1_02.ymap", "cs1_01.ymap"]);
        Assert.True(FiveMResource.IsGeneratedManifest(manifest));
        Assert.Contains("fx_version 'cerulean'", manifest);
        Assert.Contains("game 'gta5'", manifest);
        Assert.Contains("this_is_a_map 'yes'", manifest);
        Assert.True(manifest.IndexOf("cs1_01.ymap", StringComparison.Ordinal) < manifest.IndexOf("dt1_02.ymap", StringComparison.Ordinal));
    }

    [Fact]
    public void Other_manifests_are_not_ours() =>
        Assert.False(FiveMResource.IsGeneratedManifest("fx_version 'cerulean'\ngame 'gta5'\n"));
}
