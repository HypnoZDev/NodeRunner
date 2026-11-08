using NodeRunner.Data.Collisions;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes the collision files (.ybn) changed by hand, and those cut under deleted buildings at export, into a stream
// folder. Each file is rebuilt from the game's file (or the project's original one), read back and checked before it
// replaces the previous one.
internal static class CollisionFileSaver
{
    public static void Save(WorldLoader world, CollisionEdits edits, IReadOnlyList<CollisionFileCut> cuts, string folder,
        MapFileSaver.Report report)
    {
        var files = new Dictionary<string, (HashSet<int> Removed, Dictionary<int, byte> Materials, List<CollisionMove> Moves)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, edit) in edits.Files)
            files[path] = ([.. edit.Removed], new(edit.Materials), [.. edit.Moves]);
        foreach (var cut in cuts)
        {
            if (!files.TryGetValue(cut.File.Path, out var file))
                files[cut.File.Path] = file = ([], [], []);
            file.Removed.UnionWith(cut.Polygons);
        }
        if (files.Count == 0)
            return;
        if (world.Collisions is not { } catalog)
        {
            report.Errors.Add(L("collisions non chargées : fichiers .ybn non écrits"));
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var (path, (removed, materials, moves)) in files)
        {
            string name = Path.GetFileName(path);
            try
            {
                if (catalog.Edit(path, removed, materials, moves) is not { } bytes)
                {
                    report.Errors.Add(L($"{name} : fichier du jeu introuvable"));
                    continue;
                }
                // Read back: a file the game could not read must never replace a good one.
                var (_, content) = ResourceFile.Read(bytes);
                _ = YbnReader.Read(content);
                string target = Path.Combine(folder, name);
                if (world.ProjectFiles?.StreamFileOf(path) is { } own && IsInside(own, folder))
                {
                    // The project's own file is replaced in place; its first version is kept to rebuild it next time.
                    target = own;
                    string original = world.ProjectFiles.OriginalOf(own);
                    if (!File.Exists(original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                        File.Copy(own, original);
                    }
                }
                File.WriteAllBytes(target + ".tmp", bytes);
                File.Move(target + ".tmp", target, overwrite: true);
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    private static bool IsInside(string file, string folder) =>
        Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
