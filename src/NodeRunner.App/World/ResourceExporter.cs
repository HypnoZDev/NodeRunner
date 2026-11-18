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
        string occluderFileName, IReadOnlyList<string> interiorFiles, string? projectStream, out List<string> copied, out List<string> fromLibrary)
    {
        copied = [];
        fromLibrary = [];
        var state = Inspect(resourceFolder);
        if (state == TargetState.Foreign)
            return new MapFileSaver.Report([], [L("ce dossier existe déjà et n'a pas été créé par NodeRunner")]);

        // Everything is written in a work folder first (same disk, so the files are then moved, not copied): the previous
        // export stays untouched until the new one is complete. The staged folder keeps the resource's name, used in file names.
        bool created = !Directory.Exists(resourceFolder);
        string work = Path.Combine(resourceFolder, WorkFolderName);
        string staging = Path.Combine(work, "new", Path.GetFileName(Path.TrimEndingDirectorySeparator(resourceFolder)));
        bool keepWork = false;
        try
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(staging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new MapFileSaver.Report([], [L($"impossible de préparer l'export dans {work} : {ex.Message}")]);
            }
            MapFileSaver.Report report;
            try
            {
                report = Write(world, maps, pathAreas, staging, collisionCuts, occlusionCuts, collisionEdits, occluderFileName,
                    interiorFiles, projectStream, copied, out fromLibrary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An unexpected error in a writer: the export stops, the previous one stays as it was.
                Diagnostics.ErrorLog.Add(L("Export"), ex);
                report = new MapFileSaver.Report([], [L($"erreur inattendue ({ex.GetType().Name}) : {ex.Message}")]);
            }
            if (report.Errors.Count > 0)
            {
                if (state == TargetState.Ours)
                    report.Errors.Add(L("l'export précédent n'a pas été modifié"));
                return report;
            }
            string? error = Commit(staging, resourceFolder, Path.Combine(work, "old"), out keepWork, out var kept);
            if (error is not null)
                report.Errors.Add(error);
            report.Kept.AddRange(kept);
            return report;
        }
        finally
        {
            try
            {
                if (!keepWork && Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
                if (created && Directory.Exists(resourceFolder) && !Directory.EnumerateFileSystemEntries(resourceFolder).Any())
                    Directory.Delete(resourceFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private const string WorkFolderName = ".noderunner-export";

    // Files written by the last export, so that the next one removes only those (relative paths, one per line).
    internal const string InventoryFileName = ".noderunner-files";

    // Puts the staged files in place of the previous export's (a file no longer exported does not stay in it). Only the
    // files listed by the previous export's inventory are removed: those the user added are kept. An export made before
    // the inventory existed has none: its streamed files no longer written are kept and listed in kept. On failure, the
    // previous files are put back. Returns the error, if any; keepWork when the previous files could not all be put back.
    internal static string? Commit(string staging, string resourceFolder, string backup, out bool keepWork, out List<string> kept)
    {
        keepWork = false;
        kept = [];
        var added = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f)).ToList();
        var addedSet = added.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        if (ReadInventory(resourceFolder) is { } previous)
            removed.AddRange(previous.Where(r => File.Exists(Path.Combine(resourceFolder, r))));
        else
        {
            var old = new List<string>();
            string stream = Path.Combine(resourceFolder, FiveMResource.StreamFolderName);
            if (Directory.Exists(stream))
                old.AddRange(Directory.EnumerateFiles(stream).Where(IsStreamedFile).Select(f => Path.GetRelativePath(resourceFolder, f)));
            string audio = Path.Combine(resourceFolder, FiveMResource.AudioFolderName);
            if (Directory.Exists(audio))
                old.AddRange(Directory.EnumerateFiles(audio, "*_game.dat151.rel").Select(f => Path.GetRelativePath(resourceFolder, f)));
            kept.AddRange(old.Where(r => !addedSet.Contains(r)).Select(Path.GetFileName).OfType<string>());
        }
        removed.AddRange(added.Where(r => File.Exists(Path.Combine(resourceFolder, r))));
        removed = removed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var movedAway = new List<string>();
        var placed = new List<string>();
        try
        {
            foreach (var file in removed)
            {
                MoveFile(Path.Combine(resourceFolder, file), Path.Combine(backup, file));
                movedAway.Add(file);
            }
            foreach (var file in added)
            {
                MoveFile(Path.Combine(staging, file), Path.Combine(resourceFolder, file));
                placed.Add(file);
            }
            try
            {
                File.WriteAllLines(Path.Combine(resourceFolder, InventoryFileName), added);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without it, the next export keeps the files it does not write: nothing is lost.
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                foreach (var file in placed)
                    File.Delete(Path.Combine(resourceFolder, file));
                foreach (var file in movedAway)
                    MoveFile(Path.Combine(backup, file), Path.Combine(resourceFolder, file));
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                keepWork = true;
                return L($"impossible de remplacer l'export précédent ({ex.Message}) ni de le remettre en place ({restore.Message}) : ses fichiers sont dans {backup}");
            }
            return L($"impossible de remplacer l'export précédent, il est conservé : {ex.Message}");
        }
    }

    // The previous export's files, null when it has no inventory (none yet, or made before it existed).
    private static List<string>? ReadInventory(string resourceFolder)
    {
        string path = Path.Combine(resourceFolder, InventoryFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            string root = Path.GetFullPath(resourceFolder) + Path.DirectorySeparatorChar;
            return File.ReadAllLines(path).Where(l => l.Length > 0)
                .Where(l => Path.GetFullPath(Path.Combine(root, l)).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && !l.Equals(InventoryFileName, StringComparison.OrdinalIgnoreCase)
                    && !l.StartsWith(WorkFolderName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
    }

    private static MapFileSaver.Report Write(WorldLoader world, IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string resourceFolder,
        IReadOnlyList<CollisionFileCut> collisionCuts, IReadOnlyList<OccluderFileCut> occlusionCuts, CollisionEdits collisionEdits,
        string occluderFileName, IReadOnlyList<string> interiorFiles, string? projectStream, List<string> copied, out List<string> fromLibrary)
    {
        fromLibrary = [];
        string stream = Path.Combine(resourceFolder, FiveMResource.StreamFolderName);
        var report = maps.Count > 0 ? MapFileSaver.Save(world, maps, stream, markSaved: false) : new MapFileSaver.Report([], []);
        PathFileSaver.Save(world, pathAreas, stream, report, markSaved: false);
        if (world.Scenarios is { } scenarios)
            ScenarioFileSaver.Save(world, scenarios.RegionsToSave.ToList(), stream, report, markSaved: false);
        InteriorFileSaver.Save(world, interiorFiles, stream, report);
        var own = ProjectFiles(projectStream);
        if (report.Saved.Count == 0 && collisionEdits.Files.Count == 0 && OccluderMaps.Capture(world, occluderFileName).IsEmpty && own.Count == 0)
            return report;
        CollisionFileSaver.Save(world, collisionEdits, collisionCuts, stream, report);
        OccluderFileSaver.Save(world, occlusionCuts, occluderFileName, stream, report);
        CopyProjectFiles(own, stream, report, copied);
        fromLibrary = LibraryFileSaver.Copy(world, stream, report.Saved.Concat(copied).ToList(), report);
        var files = report.Saved.Concat(copied).Concat(fromLibrary).ToList();
        AudioOcclusionFileSaver.Save(world, files, interiorFiles, stream, report);
        files = report.Saved.Concat(copied).Concat(fromLibrary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var newTypes = NewTypeFiles(world, files);
        ManifestFileSaver.Save(world, files, newTypes, stream, report);
        string? audioGameData = AudioGameDataFileSaver.Save(world, files, interiorFiles, resourceFolder, report);
        files = report.Saved.Concat(copied).Concat(fromLibrary).ToList();
        try
        {
            File.WriteAllText(Path.Combine(resourceFolder, FiveMResource.ManifestFileName),
                FiveMResource.BuildManifest(Program.Author, Program.Version, files, newTypes, audioGameData));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{FiveMResource.ManifestFileName} : {ex.Message}");
        }
        if (audioGameData is not null)
            report.Saved.Add(audioGameData);
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
    public static List<string> NewTypeFiles(WorldLoader world, IEnumerable<string> saved)
    {
        var types = saved.Where(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)).ToList();
        if (types.Count == 0 || world.Content is not { } content)
            return [];
        var game = content.EnumerateFiles(".ytyp", withInactiveFallback: true)
            .Where(p => !Data.FileSystem.GameContent.IsLooseFile(p))
            .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return types.Where(t => !game.Contains(t)).ToList();
    }

    private static bool IsStreamedFile(string path) => MappingImport.IsStreamed(path);
}
