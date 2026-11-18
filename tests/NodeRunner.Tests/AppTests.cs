using NodeRunner.App.Configuration;
using NodeRunner.App.World;

namespace NodeRunner.Tests;

// The FiveM export puts the new files in place of the previous ones in one go, or not at all.
public class ResourceCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr-commit-" + Guid.NewGuid().ToString("N"));
    private readonly string _resource;
    private readonly string _staging;
    private readonly string _backup;

    public ResourceCommitTests()
    {
        _resource = Path.Combine(_root, "my_map");
        _staging = Path.Combine(_root, "staging");
        _backup = Path.Combine(_root, "old");
        Write(_resource, "fxmanifest.lua", "old manifest");
        Write(_resource, "stream/a.ymap", "old a");
        Write(_resource, "stream/b.ymap", "old b");
        Write(_resource, "stream/readme.txt", "user notes");
        Write(_resource, "client.lua", "user script");
        Write(_staging, "fxmanifest.lua", "new manifest");
        Write(_staging, "stream/a.ymap", "new a");
        Write(_staging, "stream/c.ytyp", "new c");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Commit_ReplacesTheExportedFiles_AndKeepsTheUsersOwn()
    {
        string? error = ResourceExporter.Commit(_staging, _resource, _backup, out bool keepWork);

        Assert.Null(error);
        Assert.False(keepWork);
        Assert.Equal("new manifest", Read("fxmanifest.lua"));
        Assert.Equal("new a", Read("stream/a.ymap"));
        Assert.Equal("new c", Read("stream/c.ytyp"));
        Assert.False(File.Exists(Path.Combine(_resource, "stream", "b.ymap"))); // No longer exported.
        Assert.Equal("user notes", Read("stream/readme.txt"));
        Assert.Equal("user script", Read("client.lua"));
    }

    [Fact]
    public void Commit_ThatFailsWhileSettingAsideTheOldFiles_LeavesThePreviousExport()
    {
        // b.ymap is open in another program: it cannot be moved, after a.ymap already was.
        using (File.Open(Path.Combine(_resource, "stream", "b.ymap"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? error = ResourceExporter.Commit(_staging, _resource, _backup, out bool keepWork);
            Assert.NotNull(error);
            Assert.False(keepWork);
        }

        AssertPreviousExport();
    }

    [Fact]
    public void Commit_ThatFailsWhilePlacingTheNewFiles_LeavesThePreviousExport()
    {
        // The old files are already set aside and some new ones placed when c.ytyp cannot be moved.
        using (File.Open(Path.Combine(_staging, "stream", "c.ytyp"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? error = ResourceExporter.Commit(_staging, _resource, _backup, out bool keepWork);
            Assert.NotNull(error);
            Assert.False(keepWork);
        }

        AssertPreviousExport();
        Assert.False(File.Exists(Path.Combine(_resource, "stream", "c.ytyp")));
    }

    private void AssertPreviousExport()
    {
        Assert.Equal("old manifest", Read("fxmanifest.lua"));
        Assert.Equal("old a", Read("stream/a.ymap"));
        Assert.Equal("old b", Read("stream/b.ymap"));
        Assert.Equal("user notes", Read("stream/readme.txt"));
        Assert.Equal("user script", Read("client.lua"));
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_resource, relative));

    private static void Write(string folder, string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}

public class AppSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nr-settings-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_folder, "settings.json");

    public AppSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Settings_AreReadBackAsSaved()
    {
        var settings = new AppSettings { MaxFps = 75, FieldOfView = 70f, InvertMouseY = true };
        settings.Save(File_);

        var read = AppSettings.Load(File_);

        Assert.Equal(75, read.MaxFps);
        Assert.Equal(70f, read.FieldOfView);
        Assert.True(read.InvertMouseY);
    }

    [Fact]
    public void MissingOrDamagedSettings_StartWithTheDefaults()
    {
        var defaults = new AppSettings();
        Assert.Equal(defaults.MaxFps, AppSettings.Load(File_).MaxFps);

        File.WriteAllText(File_, "{ \"MaxFps\": 75, ");
        Assert.Equal(defaults.MaxFps, AppSettings.Load(File_).MaxFps);
    }
}

// GTA V offers everything; the other games are browsed without being changed.
public class GameFeaturesTests
{
    [Fact]
    public void Gta5HasEveryFeature()
    {
        var features = GameFeatures.Of(GameKind.Gta5);
        Assert.True(features.Editing && features.Gta5Exports && features.Online && features.PedViewer && features.CutsceneViewer);
    }

    [Fact]
    public void Gta4IsBrowseOnly()
    {
        var features = GameFeatures.Of(GameKind.Gta4);
        Assert.False(features.Editing || features.Gta5Exports || features.Online || features.PedViewer || features.CutsceneViewer);
    }
}

public class OccluderFileNamesTests
{
    [Fact]
    public void The_project_boxes_are_split_into_files_that_hold_them()
    {
        int max = NodeRunner.Data.Native.Maps.OccluderFile.MaxBoxesPerFile;
        Assert.Equal(["res_occl.ymap"], OccluderEdits.ProjectFileNames("res_occl.ymap", max));
        Assert.Equal(["res_occl.ymap", "res_occl_2.ymap", "res_occl_3.ymap"], OccluderEdits.ProjectFileNames("res_occl.ymap", 2 * max + 1));
    }
}
