using NodeRunner.Data.Collisions;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes the collision files (.ybn) changed by hand, and those cut under deleted buildings at export, into a stream
// folder. Each file is rebuilt from the game's file, read back and checked before it replaces the previous one.
internal static class CollisionFileSaver
{
    public static void Save(WorldLoader world, CollisionEdits edits, IReadOnlyList<CollisionFileCut> cuts, string folder,
        MapFileSaver.Report report)
    {
        var files = new Dictionary<string, (HashSet<int> Removed, Dictionary<int, byte> Materials)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, edit) in edits.Files)
            files[path] = ([.. edit.Removed], new(edit.Materials));
        foreach (var cut in cuts)
        {
            if (!files.TryGetValue(cut.File.Path, out var file))
                files[cut.File.Path] = file = ([], []);
            file.Removed.UnionWith(cut.Polygons);
        }
        if (files.Count == 0)
            return;
        if (world.Collisions is not { } catalog)
        {
            report.Errors.Add("collisions non chargées : fichiers .ybn non écrits");
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var (path, (removed, materials)) in files)
        {
            string name = Path.GetFileName(path);
            try
            {
                if (catalog.Edit(path, removed, materials) is not { } bytes)
                {
                    report.Errors.Add($"{name} : fichier du jeu introuvable");
                    continue;
                }
                // Read back: a file the game could not read must never replace a good one.
                var (_, content) = ResourceFile.Read(bytes);
                _ = YbnReader.Read(content);
                string target = Path.Combine(folder, name);
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
}
