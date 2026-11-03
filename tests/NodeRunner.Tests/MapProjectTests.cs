using NodeRunner.Data.FileSystem;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class MapProjectTests
{
    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "noderunner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Saves_and_reads_a_project()
    {
        string folder = NewFolder();
        var created = MapProject.Create(folder, "Hôpital Pillbox", MapVersion.SinglePlayer, "mpheist");
        created.ExportFolder = @"C:\server\resources";
        created.Save();

        var project = MapProject.Load(folder);
        Assert.Equal("Hôpital Pillbox", project.Name);
        Assert.Equal("hopital_pillbox", project.ResourceName);
        Assert.Equal(MapVersion.SinglePlayer, project.MapVersion);
        Assert.Equal("mpheist", project.LastDlcPack);
        Assert.Equal(@"C:\server\resources", project.ExportFolder);
        Assert.Equal(Path.Combine(Path.GetFullPath(folder), "stream"), project.StreamFolder);
        Assert.True(Directory.Exists(project.StreamFolder));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Moves_loose_map_files_into_stream()
    {
        string folder = NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "stream"));
        File.WriteAllText(Path.Combine(folder, "a.ymap"), "loose");
        File.WriteAllText(Path.Combine(folder, "b.ymap"), "loose");
        File.WriteAllText(Path.Combine(folder, "stream", "b.ymap"), "kept");

        var project = MapProject.Create(folder, "", MapVersion.Multiplayer, null);

        Assert.Equal(Path.GetFileName(folder), project.Name);
        Assert.Equal("loose", File.ReadAllText(Path.Combine(project.StreamFolder, "a.ymap")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(project.StreamFolder, "b.ymap")));
        Assert.False(File.Exists(Path.Combine(folder, "a.ymap")));
        Assert.True(File.Exists(Path.Combine(folder, "b.ymap"))); // Not overwritten: left where it was.
        Directory.Delete(folder, recursive: true);
    }

    [Theory]
    [InlineData("Garage: Mosley's", "Garage_ Mosley's")]
    [InlineData("  ...  ", "Projet")]
    public void Makes_folder_names(string name, string expected) =>
        Assert.Equal(expected, MapProject.ToFolderName(name));
}
