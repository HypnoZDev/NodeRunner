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

    public static MapFileSaver.Report Export(WorldLoader world, IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string resourceFolder,
        IReadOnlyList<CollisionFileCut> collisionCuts, IReadOnlyList<OccluderFileCut> occlusionCuts, CollisionEdits collisionEdits,
        string occluderFileName, IReadOnlyList<string> interiorFiles)
    {
        if (Inspect(resourceFolder) == TargetState.Foreign)
            return new MapFileSaver.Report([], ["ce dossier existe déjà et n'a pas été créé par NodeRunner"]);

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
            return new MapFileSaver.Report([], [$"impossible de vider {stream} : {ex.Message}"]);
        }

        var report = maps.Count > 0 ? MapFileSaver.Save(world, maps, stream, markSaved: false) : new MapFileSaver.Report([], []);
        PathFileSaver.Save(world, pathAreas, stream, report, markSaved: false);
        InteriorFileSaver.Save(world, interiorFiles, stream, report);
        if (report.Saved.Count == 0 && collisionEdits.Files.Count == 0 && OccluderMaps.Capture(world, occluderFileName).IsEmpty)
            return report;
        CollisionFileSaver.Save(world, collisionEdits, collisionCuts, stream, report);
        OccluderFileSaver.Save(world, occlusionCuts, occluderFileName, stream, report);
        try
        {
            File.WriteAllText(Path.Combine(resourceFolder, FiveMResource.ManifestFileName),
                FiveMResource.BuildManifest(Program.Author, Program.Version, report.Saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{FiveMResource.ManifestFileName} : {ex.Message}");
        }
        return report;
    }

    private static bool IsStreamedFile(string path) =>
        path.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase);
}
