using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.FileSystem;

// Indexes the game files of one type by name hash.
public static class GameFileIndex
{
    // Path of every file with this extension, keyed by the hash of its name without extension: the version the game
    // uses, or another one when only inactive archives hold it.
    public static Dictionary<uint, string> Build(GameContent content, string extension)
    {
        var index = new Dictionary<uint, string>();
        foreach (var path in content.EnumerateFiles(extension, withInactiveFallback: true))
            index[Joaat.Hash(Path.GetFileNameWithoutExtension(path))] = path;
        return index;
    }
}
