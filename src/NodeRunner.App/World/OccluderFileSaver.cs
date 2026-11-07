using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes the occluder map files changed by hand (moved, resized, deleted occluders, and the project's own file of
// created boxes), and those cut under deleted buildings at export, into a stream folder. Each file is rebuilt from
// the game's file, read back and checked before it replaces the previous one.
internal static class OccluderFileSaver
{
    // The names of the files written (added to report.Saved too).
    public static List<string> Save(WorldLoader world, IReadOnlyList<OccluderFileCut> cuts, string projectFileName, string folder,
        MapFileSaver.Report report)
    {
        var written = new List<string>();
        var files = OccluderMaps.Get(world);
        var toWrite = files.Where(f => f.IsProject ? f.BoxCount > 0 : f.HasChanges || cuts.Any(c => Same(c, f))).ToList();
        if (toWrite.Count == 0)
            return written;
        if (world.Content is not { } content)
        {
            report.Errors.Add("carte non chargée : fichiers d'occlusion non écrits");
            return written;
        }

        Directory.CreateDirectory(folder);
        var writer = new OccluderFile();
        foreach (var file in toWrite)
        {
            string name = file.IsProject ? projectFileName : file.FileName;
            try
            {
                var cut = cuts.FirstOrDefault(c => Same(c, file));
                var boxes = file.Boxes.Where(b => !file.DeletedBoxes.Contains(b.Index) && cut?.Boxes.Contains(b.Index) != true).ToList();
                var triangles = new Dictionary<int, IReadOnlySet<int>>();
                foreach (int model in file.DeletedModels)
                    triangles[model] = Enumerable.Range(0, file.Occluders.Models[model].TriangleCount).ToHashSet();
                foreach (var (model, gone) in cut?.Triangles ?? new Dictionary<int, IReadOnlySet<int>>())
                {
                    if (!triangles.ContainsKey(model))
                        triangles[model] = gone;
                }
                int expectedTriangles = file.Occluders.Models.Sum(m => m.TriangleCount) - triangles.Values.Sum(t => t.Count);

                MapFileContent output;
                ResourceHeader header;
                if (file.IsProject)
                {
                    if (boxes.Count == 0)
                        continue; // Created boxes all cut under deleted buildings.
                    var template = files.FirstOrDefault(f => !f.IsProject && f.Occluders.Boxes.Count > 0)
                        ?? throw new InvalidDataException("aucun fichier d'occlusion du jeu utilisable comme modèle");
                    var source = content.ReadFile(template.GamePath) ?? throw new InvalidDataException("modèle introuvable");
                    header = content.GetResourceHeader(template.GamePath) ?? throw new InvalidDataException("en-tête du modèle introuvable");
                    output = writer.BuildOccluders(source, Path.GetFileNameWithoutExtension(name), boxes);
                }
                else
                {
                    var source = content.ReadFile(file.GamePath) ?? throw new InvalidDataException("fichier du jeu introuvable");
                    header = content.GetResourceHeader(file.GamePath) ?? throw new InvalidDataException("en-tête de ressource introuvable");
                    output = writer.WriteOccluders(source, boxes, triangles);
                }
                var bytes = ResourceFile.Write(header with { SystemFlags = (header.SystemFlags & 0xF0000000) | output.SystemPageFlags }, output.Content);

                // Read back: as many boxes and triangles as expected.
                var check = writer.ReadOccluders(ResourceFile.Read(bytes).Content);
                if (check.Boxes.Count != boxes.Count || check.Models.Sum(m => m.TriangleCount) != expectedTriangles)
                    throw new InvalidDataException("vérification échouée");

                string target = Path.Combine(folder, name);
                File.WriteAllBytes(target + ".tmp", bytes);
                File.Move(target + ".tmp", target, overwrite: true);
                report.Saved.Add(name);
                written.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
        return written;
    }

    private static bool Same(OccluderFileCut cut, OccluderMapFile file) =>
        string.Equals(cut.GamePath, file.GamePath, StringComparison.OrdinalIgnoreCase);
}
