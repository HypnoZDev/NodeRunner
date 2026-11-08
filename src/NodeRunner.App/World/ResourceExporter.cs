using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Exports the modified map files as a FiveM resource, ready to be copied into a server's "resources" folder:
// &lt;folder&gt;\&lt;name&gt;\fxmanifest.lua and &lt;folder&gt;\&lt;name&gt;\stream\*.ymap.
internal static class ResourceExporter
{
    public enum TargetState { New, Ours, Foreign }

    // Whether the resource folder can be written: new, a previous NodeRunner export, or someone else's folder.
    public static TargetState Inspect(string resourceFolder)
    {
        if (!Directory.Exists(resourceFolder) || !Directory.EnumerateFileSystemEntries(resourceFolder).Any())
            return TargetState.New;
        string manifest = Path.Combine(resourceFolder, FiveMResource.ManifestFileName);
        try
        {
            return File.Exists(manifest) && FiveMResource.IsGeneratedManifest(File.ReadAllText(manifest))
                ? TargetState.Ours
                : TargetState.Foreign;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TargetState.Foreign;
        }
    }

    // Files of the project's stream folder (models, textures, collisions of an imported mapping or mod, files saved in an
    // earlier session): those the export does not write again are copied as they are.
    public static List<string> ProjectFiles(string? projectStream)
    {
        if (projectStream is null || !Directory.Exists(projectStream))
            return [];
        try
        {
            return MappingImport.FindFiles(projectStream).Files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static MapFileSaver.Report Export(WorldLoader world, IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string resourceFolder,
        IReadOnlyList<CollisionFileCut> collisionCuts, IReadOnlyList<OccluderFileCut> occlusionCuts, CollisionEdits collisionEdits,
        string occluderFileName, IReadOnlyList<string> interiorFiles, string? projectStream, out List<string> copied)
    {
        copied = [];
        if (Inspect(resourceFolder) == TargetState.Foreign)
            return new MapFileSaver.Report([], [L("ce dossier existe déjà et n'a pas été créé par NodeRunner")]);

        string stream = Path.Combine(resourceFolder, FiveMResource.StreamFolderName);
        try
        {
            // A previous export: its map and collision files are replaced, so that a file no longer modified does not stay in it.
            if (Directory.Exists(stream))
                foreach (var old in Directory.EnumerateFiles(stream).Where(IsStreamedFile))
                    File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MapFileSaver.Report([], [L($"impossible de vider {stream} : {ex.Message}")]);
        }

        var report = maps.Count > 0 ? MapFileSaver.Save(world, maps, stream, markSaved: false) : new MapFileSaver.Report([], []);
        PathFileSaver.Save(world, pathAreas, stream, report, markSaved: false);
        InteriorFileSaver.Save(world, interiorFiles, stream, report);
        var own = ProjectFiles(projectStream);
        if (report.Saved.Count == 0 && collisionEdits.Files.Count == 0 && OccluderMaps.Capture(world, occluderFileName).IsEmpty && own.Count == 0)
            return report;
        CollisionFileSaver.Save(world, collisionEdits, collisionCuts, stream, report);
        OccluderFileSaver.Save(world, occlusionCuts, occluderFileName, stream, report);
        CopyProjectFiles(own, stream, report, copied);
        var files = report.Saved.Concat(copied).ToList();
        try
        {
            File.WriteAllText(Path.Combine(resourceFolder, FiveMResource.ManifestFileName),
                FiveMResource.BuildManifest(Program.Author, Program.Version, files, NewTypeFiles(world, files)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{FiveMResource.ManifestFileName} : {ex.Message}");
        }
        return report;
    }

    // A file written by the export (modified map, collision...) wins over the project's copy of the same name.
    private static void CopyProjectFiles(List<string> own, string stream, MapFileSaver.Report report, List<string> copied)
    {
        var written = report.Saved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in own)
        {
            string name = Path.GetFileName(file);
            if (written.Contains(name))
                continue;
            try
            {
                Directory.CreateDirectory(stream);
                File.Copy(file, Path.Combine(stream, name), overwrite: true);
                copied.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    // The .ytyp files written that the game does not have (interiors made by the user).
    private static List<string> NewTypeFiles(WorldLoader world, IEnumerable<string> saved)
    {
        var types = saved.Where(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)).ToList();
        if (types.Count == 0 || world.Content is not { } content)
            return [];
        var game = content.EnumerateFiles(".ytyp", withInactiveFallback: true)
            .Where(p => !p.StartsWith(Data.FileSystem.GameContent.LooseFilesRoot + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return types.Where(t => !game.Contains(t)).ToList();
    }

    private static bool IsStreamedFile(string path) => MappingImport.IsStreamed(path);
}
