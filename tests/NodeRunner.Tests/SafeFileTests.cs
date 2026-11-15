using NodeRunner.Data.FileSystem;

namespace NodeRunner.Tests;

public class SafeFileTests
{
    [Fact]
    public void Replaces_the_file_and_leaves_no_temporary_file()
    {
        string folder = Directory.CreateTempSubdirectory("nr-safefile").FullName;
        try
        {
            string path = Path.Combine(folder, "settings.json");
            SafeFile.WriteAllText(path, "first");
            SafeFile.WriteAllText(path, "second é");

            Assert.Equal("second é", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Keeps_the_previous_file_when_the_write_fails()
    {
        string folder = Directory.CreateTempSubdirectory("nr-safefile").FullName;
        try
        {
            string path = Path.Combine(folder, "collisions.json");
            SafeFile.WriteAllText(path, "kept");
            // The temporary name is taken by a folder: the write cannot start.
            Directory.CreateDirectory(path + ".tmp");

            Assert.ThrowsAny<Exception>(() => SafeFile.WriteAllText(path, "lost"));
            Assert.Equal("kept", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
