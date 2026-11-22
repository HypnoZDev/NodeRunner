using NodeRunner.App.World;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // Map files to write when saving: those changed in this session (even if the change was undone since, so that a
    // file saved earlier is put back) and those saved before.
    public IReadOnlyList<MapData> MapsToSave
    {
        get
        {
            var history = History; // Also forgets the saved maps of a previous map loading.
            if (_savedMapsLibrary != World.Maps)
            {
                _savedMaps.Clear();
                _savedMapsLibrary = World.Maps;
            }
            var maps = history.All
                .SelectMany(e => e.Entities)
                .Where(entity => entity.Interior is null).Select(entity => entity.Map)
                // The project's maps placing an interior whose portals changed: the instance stores its number of exits.
                .Concat(history.All.OfType<IInteriorEdit>().Select(e => e.Interior.Map).Where(m => m is { IsCustom: true }))
                .Concat(history.All.OfType<ICarGeneratorEdit>().Select(e => e.Map))
                .Concat(history.All.OfType<ITimecycleEdit>().Select(e => e.Map))
                // Both files of a light: a moved light also changes the boxes of its lodlights file.
                .Concat(history.All.OfType<ILodLightEdit>().SelectMany(e => e.Lod is { } lod ? [e.Distant, lod] : new[] { e.Distant }))
                .OfType<MapData>()
                .Concat(_savedMaps)
                .Distinct()
                .ToList();

            // Deleting objects renumbers the ones left in their map: the maps whose objects point to them (to their
            // lower-detail parent) are rewritten too, with the new numbers.
            if (World.Maps is { } library)
            {
                var withDeletions = maps.Where(m => m.Entities.Any(e => e.IsDeleted) || history.All.OfType<DeleteEdit>().SelectMany(d => d.Entities).Any(e => e.Map == m)).ToList();
                maps.AddRange(withDeletions.SelectMany(library.ChildMaps).Where(m => !maps.Contains(m)).Distinct().ToList());

                // Their parent maps too when a simplified model lost children (now or in an earlier session): the number of
                // children it declares is rewritten, otherwise the game shows it even up close. Up to the coarsest level.
                var edited = maps.Concat(library.Maps.Where(m => m.IsSaved)).Distinct().ToList();
                while (edited.Count > 0)
                {
                    var parents = edited.Select(library.ParentMap).OfType<MapData>().Distinct()
                        .Where(p => !maps.Contains(p) && MapLibrary.HasLostChildren(p)).ToList();
                    maps.AddRange(parents);
                    edited = parents;
                }
            }
            return maps;
        }
    }

    public ModelViewer Viewer { get; } = new();

    // The model open in the workshop tab (the viewer above stays a catalogue).
    public ModelViewer WorkshopViewer { get; } = new();

    // The ped of the ped viewer tab.
    public ModelViewer PedViewer { get; } = new();

    // The game cutscene played on the map (cutscene viewer).
    public CutscenePlayer Cutscenes { get; } = new();

    // The car drawn on the selected parking spot.
    public CarGeneratorPreview SpotPreview { get; } = new();

    public ScenarioPreview ScenarioPreview { get; } = new();

    public ScenarioLife ScenarioLife { get; } = new();

    // Play mode: a ped walked on the map. PlayRequest: started by the main form at the next frame (it needs the
    // centre of the 3D view).
    public PlayMode Play { get; } = new();

    public bool PlayRequest { get; set; }

    // Clips played by the peds of the selected point and of the simulation.
    public ScenarioClips ScenarioClips { get; } = new();

    // Map files of a FiveM export: those to save, plus the saved versions taken back when the map was loaded
    // (modified in an earlier session).
    // Those of the other open projects are left out: each project is exported on its own.
    public IReadOnlyList<MapData> MapsToExport =>
        MapsToSave.Concat(World.Maps?.Maps.Where(m => m.IsSaved) ?? []).Distinct()
            .Where(m => ExtraProjects.Count == 0 || SaveOwner(m) is null).ToList();

    // The open project a map is saved into: the one it was loaded from; for a map not saved before (a lower-detail map
    // rewritten after a deletion...), the one of a related map; null for the active project.
    private MapProject? SaveOwner(MapData map)
    {
        if (ExtraProjects.Count == 0)
            return null;
        if (ExtraProjectOf(map) is { } owner)
            return owner;
        if (map.IsSaved || World.Maps is not { } library)
            return null;
        if (library.ParentMap(map) is { } parent && ExtraProjectOf(parent) is { } parentOwner)
            return parentOwner;
        var children = library.ChildMaps(map).ToList();
        for (int depth = 0; depth < 4 && children.Count > 0; depth++)
        {
            if (children.Select(ExtraProjectOf).OfType<MapProject>().FirstOrDefault() is { } childOwner)
                return childOwner;
            children = children.SelectMany(library.ChildMaps).ToList();
        }
        return null;
    }

    private readonly HashSet<MapData> _savedMaps = [];
    private MapLibrary? _savedMapsLibrary;

    public (string Text, bool Success, DateTime Time)? LastSave { get; private set; }

    // Road network files to write when saving (and exporting): areas changed since the map was loaded, and those taken
    // back from the project.
    public IReadOnlyList<int> PathAreasToSave => World.PathNetwork is { } network
        ? network.ChangedAreas.Concat(network.SavedAreas).Distinct().Order().ToList()
        : [];

    // Archetype files (.ytyp) to write when saving: those of the interiors whose objects were moved in this session.
    public IReadOnlyList<string> InteriorFilesToSave => World.Maps is { } library
        ? History.All.SelectMany(e => e.Entities).Select(e => e.Interior).OfType<MapEntity>()
            .Concat(History.All.OfType<IInteriorEdit>().Select(e => e.Interior))
            .Select(i => library.GetArchetypeFile(i.ArchetypeHash)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    // Archetype files of a FiveM export: those to save, plus the project's versions taken back when the map was loaded.
    public IReadOnlyList<string> InteriorFilesToExport => World.Maps is { } library
        ? InteriorFilesToSave.Concat(ProjectInteriorFiles(library)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => ExtraProjectOfFile(f) is null).ToList()
        : [];

    private (MapLibrary Library, List<string> Files)? _projectInteriorFiles;

    // The interiors' archetype files taken from the project's stream folder when the map was loaded.
    private List<string> ProjectInteriorFiles(MapLibrary library)
    {
        if (_projectInteriorFiles is not { } cached || cached.Library != library)
        {
            var files = library.GetInteriorArchetypeHashes().Select(library.GetArchetypeFile).OfType<string>()
                .Where(p => p.StartsWith(Data.FileSystem.GameContent.LooseFilesRoot + "\\", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _projectInteriorFiles = cached = (library, files);
        }
        return cached.Files;
    }

    // Scenario region files (.ymt) to write when saving: changed since loading, or taken back from the project.
    public IReadOnlyList<Data.Scenarios.ScenarioRegion> ScenarioRegionsToSave => World.Scenarios?.RegionsToSave.ToList() ?? [];

    public int PendingSaveCount => MapsToSave.Count + PathAreasToSave.Count + ScenarioRegionsToSave.Count + InteriorFilesToSave.Count + (CollisionsPending ? Math.Max(1, CollisionEdits.Files.Count) : 0)
        + (OccludersPending ? Math.Max(1, OccluderEdits.StreamFileNames.Count()) : 0);

    // Occluder changes not written to the project yet.
    public bool OccludersPending => OccluderMaps.HasUnsavedChanges(World);

    // The occluder changes as they would be saved now.
    public OccluderEdits OccluderEdits => OccluderMaps.Capture(World, OccluderFileName);

    // The project's occluder changes (occlusion.json next to projet.json), put back at the next map loading.
    private void LoadOccluderEdits()
    {
        var edits = OccluderEdits.Empty;
        try
        {
            edits = OccluderEdits.Load(Project?.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Diagnostics.ErrorLog.Add(L("Projet"), L($"{OccluderEdits.FileName} illisible : {ex.Message}"), ex);
        }
        OccluderMaps.UseSaved(edits);
        World.OccluderFileNames = edits.StreamFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Writes occlusion.json and the changed occluder map files into the stream folder (files no longer changed are removed).
    private void SaveOccluderEdits(string stream, MapFileSaver.Report report)
    {
        if (Project is not { } project)
            return;
        var edits = OccluderEdits;
        int errors = report.Errors.Count;
        var written = OccluderFileSaver.Save(World, [], OccluderFileName, stream, report);
        if (report.Errors.Count > errors)
            return;
        string file = Path.Combine(project.Folder, OccluderEdits.FileName);
        try
        {
            foreach (var old in OccluderMaps.Saved.StreamFileNames.Except(written, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(stream, old);
                if (File.Exists(path))
                    File.Delete(path);
            }
            if (edits.IsEmpty)
                File.Delete(file);
            else
                SafeFile.WriteAllText(file, edits.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{OccluderEdits.FileName} : {ex.Message}");
            return;
        }
        OccluderMaps.MarkSaved(edits);
        World.OccluderFileNames = written.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Collision changes not written to the project yet.
    public bool CollisionsPending => CollisionEdits.Revision != _savedCollisionRevision;

    private int _savedCollisionRevision;

    private const string CollisionEditsFileName = Data.Collisions.CollisionEdits.FileName;

    // The project's collision changes (collisions.json next to projet.json); none without a project.
    private void LoadProjectEdits()
    {
        LoadOccluderEdits();
        CollisionEdits = new Data.Collisions.CollisionEdits();
        if (Project is { } project && Path.Combine(project.Folder, CollisionEditsFileName) is var file && File.Exists(file))
        {
            try
            {
                CollisionEdits = Data.Collisions.CollisionEdits.FromJson(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                Diagnostics.ErrorLog.Add(L("Projet"), L($"{CollisionEditsFileName} illisible : {ex.Message}"), ex);
            }
        }
        _savedCollisionRevision = CollisionEdits.Revision;
        SelectCollision([], CollisionSelectMode.Replace);
    }

    // Writes collisions.json and the changed .ybn files into the stream folder (files no longer changed are removed).
    private void SaveCollisionEdits(string stream, MapFileSaver.Report report)
    {
        if (Project is not { } project)
            return;
        string file = Path.Combine(project.Folder, CollisionEditsFileName);
        try
        {
            if (File.Exists(file))
            {
                var before = Data.Collisions.CollisionEdits.FromJson(File.ReadAllText(file));
                foreach (var path in before.Files.Keys.Where(p => CollisionEdits.For(p) is null))
                {
                    // A file of the imported mapping gets its first version back; a game's file is no longer needed.
                    if (World.ProjectFiles?.StreamFileOf(path) is { } own)
                    {
                        string original = World.ProjectFiles.OriginalOf(own);
                        if (File.Exists(original))
                            File.Move(original, own, overwrite: true);
                        continue;
                    }
                    string old = Path.Combine(stream, Path.GetFileName(path));
                    if (File.Exists(old))
                        File.Delete(old);
                }
            }
            if (CollisionEdits.Files.Count == 0)
                File.Delete(file);
            else
                SafeFile.WriteAllText(file, CollisionEdits.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            report.Errors.Add($"{CollisionEditsFileName} : {ex.Message}");
            return;
        }
        CollisionFileSaver.Save(World, CollisionEdits, [], stream, report);
    }

    // "2 YMAP", "2 YMAP, 1 YND".
    public string PendingSaveText => FilesText(MapsToSave.Count, PathAreasToSave.Count)
        + (InteriorFilesToSave.Count is var interiors and > 0 ? L($", {interiors} YTYP") : "")
        + (ScenarioRegionsToSave.Count is var scenarios and > 0 ? L($", {scenarios} YMT") : "")
        + (CollisionsPending ? L($", {CollisionEdits.Files.Count} YBN") : "")
        + (OccludersPending ? L($", {OccluderEdits.StreamFileNames.Count()} YMAP d'occlusion") : "");

    public static string FilesText(int maps, int paths) =>
        paths == 0 ? $"{maps} YMAP" : maps == 0 ? $"{paths} YND" : L($"{maps} YMAP, {paths} YND");

    public void SaveMaps()
    {
        if (Online.IsGuest)
        {
            LastSave = (L("Session Online : seul l'hôte enregistre le projet."), false, DateTime.Now);
            return;
        }
        if (SaveFolder is not { } folder)
            return;
        var maps = MapsToSave;
        var paths = PathAreasToSave;
        var scenarioRegions = ScenarioRegionsToSave;
        bool collisions = CollisionsPending;
        bool occluders = OccludersPending;
        var interiorFiles = InteriorFilesToSave;
        if (maps.Count == 0 && paths.Count == 0 && scenarioRegions.Count == 0 && !collisions && !occluders && interiorFiles.Count == 0)
            return;
        // Each file goes back to the open project it was loaded from.
        var report = new MapFileSaver.Report([], []);
        try
        {
            foreach (var group in maps.GroupBy(SaveOwner))
            {
                var saved = MapFileSaver.Save(World, group, group.Key?.StreamFolder ?? folder);
                report.Saved.AddRange(saved.Saved);
                report.Errors.AddRange(saved.Errors);
            }
            foreach (var map in maps.Where(m => m.FilePath is { } p && report.Saved.Contains(Path.GetFileName(p))))
                _savedMaps.Add(map);
            PathFileSaver.Save(World, paths, folder, report);
            ScenarioFileSaver.Save(World, scenarioRegions, folder, report);
            foreach (var group in interiorFiles.GroupBy(ExtraProjectOfFile))
                InteriorFileSaver.Save(World, group, group.Key?.StreamFolder ?? folder, report);
            if (collisions)
                SaveCollisionEdits(folder, report);
            if (occluders)
                SaveOccluderEdits(folder, report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unexpected error in a writer: reported like the others, the changes stay in memory to be saved again.
            report.Errors.Add(L($"erreur inattendue ({ex.GetType().Name}) : {ex.Message}"));
            Diagnostics.ErrorLog.Add(L("Enregistrement"), ex);
        }
        if (report.Errors.Count == 0)
        {
            _savedCollisionRevision = CollisionEdits.Revision;
            History.MarkSaved();
            int pathFiles = report.Saved.Count(f => f.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase));
            int typeFiles = report.Saved.Count(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase));
            int scenarioFiles = report.Saved.Count(f => f.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase));
            string text = FilesText(report.Saved.Count - pathFiles - typeFiles - scenarioFiles, pathFiles) + (typeFiles > 0 ? L($", {typeFiles} YTYP") : "")
                + (scenarioFiles > 0 ? L($", {scenarioFiles} YMT") : "");
            LastSave = (ExtraProjects.Count > 0
                ? L($"{text} enregistré(s) dans les projets ouverts")
                : L($"{text} enregistré(s) dans le projet « {Project!.Name} »"), true, DateTime.Now);
        }
        else
        {
            LastSave = (L($"Échec de l'enregistrement : {string.Join(" ; ", report.Errors)}"), false, DateTime.Now);
            Diagnostics.ErrorLog.Add(L("Enregistrement"), string.Join(" ; ", report.Errors));
        }
    }
}
