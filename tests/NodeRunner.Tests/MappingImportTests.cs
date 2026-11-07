using NodeRunner.Data.FileSystem;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class MappingImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr_import_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
    }

    [Fact]
    public void CopiesStreamedFilesIntoANewProjectWithoutTouchingTheSource()
    {
        Touch(@"cayo\fxmanifest.lua");
        Touch(@"cayo\stream\a.ymap");
        Touch(@"cayo\stream\col\b.ybn");
        Touch(@"cayo\stream\other\a.ymap");
        Touch(@"cayo\readme.txt");

        var result = MappingImport.Import(Path.Combine(_root, "cayo"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal(2, result.Copied);
        Assert.Single(result.Skipped);
        Assert.Equal("cayo", result.Project.Name);
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "a.ymap")));
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "b.ybn")));
        Assert.True(File.Exists(Path.Combine(_root, @"cayo\stream\a.ymap")));
        Assert.False(File.Exists(Path.Combine(_root, @"cayo\projet.json")));
    }

    [Fact]
    public void AStreamFolderTakesItsParentsNameAndNeverOverwritesAProject()
    {
        Touch(@"pack\stream\a.ymap");
        Directory.CreateDirectory(Path.Combine(_root, "projects", "pack"));

        var result = MappingImport.Import(Path.Combine(_root, @"pack\stream"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal("pack", result.Project.Name);
        Assert.Equal("pack 2", Path.GetFileName(result.Project.Folder));
    }
}
