using System.IO.Compression;
using NodeRunner.App;

namespace NodeRunner.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr-update-" + Guid.NewGuid().ToString("N"));

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

    private string Folder(string name, params (string Path, string Text)[] files)
    {
        string folder = Path.Combine(_root, name);
        foreach (var (path, text) in files)
        {
            string file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        return folder;
    }

    private string Zip(params (string Path, string Text)[] files)
    {
        string source = Folder("source", files);
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(source, "NodeRunner.dll"));
        string zip = Path.Combine(_root, "update.zip");
        ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

    [Fact]
    public void AnUpdateReplacesTheProgramAndKeepsTheUsersFiles()
    {
        string app = Folder("app", ("NodeRunner.exe", "old"), ("settings.json", "mine"), ("data/strings.txt", "mine"));
        string zip = Zip(("NodeRunner.exe", "new"), ("settings.json", "theirs"), ("data/strings.txt", "theirs"), ("data/added.txt", "added"));

        Updater.Install(zip, app, Program.Version);

        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "NodeRunner.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "NodeRunner.exe.old")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "settings.json")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "data", "strings.txt")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(app, "data", "added.txt")));

        Updater.CleanUp(app);
        Assert.False(File.Exists(Path.Combine(app, "NodeRunner.exe.old")));
    }

    [Fact]
    public void AnArchiveOfAnotherVersionOrFromElsewhereIsRefused()
    {
        string app = Folder("app", ("NodeRunner.exe", "old"));
        string zip = Zip(("NodeRunner.exe", "new"));

        Assert.Throws<InvalidDataException>(() => Updater.Install(zip, app, "99.0.0"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "NodeRunner.exe")));
        Assert.False(Updater.IsTrustedUrl("https://example.com/NodeRunner-9.9.9-win-x64.zip"));
        Assert.False(Updater.IsTrustedUrl("https://github.com/HypnoZDev/NodeRunner/releases/download/../../x.zip"));
        Assert.True(Updater.IsTrustedUrl("https://github.com/HypnoZDev/NodeRunner/releases/download/v1.6.6/NodeRunner-1.6.6-win-x64.zip"));
    }
}
