using NodeRunner.Data.Collisions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// One line of the check before an export: a problem (or a remark), the objects or files concerned, and what to do.
// Merge: the other versions of the same map file whose changes can be taken into ours.
internal sealed record ExportCheckItem(bool IsProblem, string Title, IReadOnlyList<string> Details, string? Advice = null,
    IReadOnlyList<MapConflict>? Merge = null);

// Checks a FiveM export before it is written: map files also shipped by another resource of the server or modified by
// another project (FiveM loads only one of them), deleted scenery whose collision stays in the game, deleted objects
// still seen from far away.
internal static class ExportChecker
{
    private const int MaxDetails = 12;

    // What the check needs, taken on the interface thread (the maps can change while the check runs).
    public sealed record Input(
        List<(string FileName, string? GamePath, bool IsCustom, List<MapEntity> Kept)> Maps,
        List<string> StillSeenFromFar,
        string? ExportFolder,
        string ResourceName,
        List<(string Name, string StreamFolder)> OtherProjects,
        bool RemoveGround = false);

    public sealed record Result(List<ExportCheckItem> Items, string Summary, List<CollisionFileCut> CollisionCuts, List<OccluderFileCut> OcclusionCuts);

    public static Input Prepare(AppState state)
    {
        var names = state.World.Names ?? new HashNames();
        var maps = new List<(string, string?, bool, List<MapEntity>)>();
        var stillSeen = new List<string>();
        foreach (var map in state.MapsToExport)
        {
            if (map.FilePath is not { } path)
                continue;
            maps.Add((Path.GetFileName(path), map.IsCustom ? null : path, map.IsCustom, map.KeptEntities.ToList()));
            // Deleted in this session while their simplified model stays (it also stands for other objects).
            foreach (var entity in map.Entities)
            {
                if (entity.IsDeleted && entity.Parent is { IsDeleted: false })
                    stillSeen.Add($"{names.Describe(entity.ArchetypeHash)} ({Path.GetFileName(path)})");
            }
        }

        var others = new List<(string, string)>();
        string? current = state.Project?.Folder;
        foreach (string folder in state.Settings.RecentProjects)
        {
            if (current is not null && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                continue;
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            try
            {
                if (MapProject.Exists(folder))
                    name = MapProject.Load(folder).Name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Unreadable project file: the folder name is enough.
            }
            others.Add((name, Path.Combine(folder, FiveMResource.StreamFolderName)));
        }
        return new Input(maps, stillSeen, state.ExportFolder, state.ExportResourceName, others);
    }

    public static Result Run(Input input, WorldLoader world)
    {
        var items = new List<ExportCheckItem>();
        var names = world.Names ?? new HashNames();
        var fileNames = input.Maps.Select(m => m.FileName).ToList();

        // Same map file elsewhere: in the server's resources, then in the user's other projects.
        int scanned = 0;
        string? scanRoot = null;
        var ownFolders = new List<string>();
        string summary;
        if (input.ExportFolder is { } exportFolder && Directory.Exists(exportFolder))
        {
            string root = scanRoot = ConflictScanner.ScanRoot(exportFolder);
            string target = Path.Combine(exportFolder, input.ResourceName);
            if (ConflictScanner.NonCategoryFolders(root, exportFolder) is { Count: > 0 } plain)
            {
                items.Add(new ExportCheckItem(true,
                    $"FiveM ne trouvera pas la ressource : « {string.Join("\\", plain)} » n'est pas un dossier de catégorie",
                    [target],
                    "Dans le dossier « resources », FiveM n'ouvre que les dossiers entre crochets (ex. [maps]) : renommez "
                    + $"le dossier en « [{plain[^1]}] », ou exportez directement dans « resources »."));
            }
            // An earlier export of this project to another folder: one remark, not a conflict per file.
            var copies = ConflictScanner.FindOtherCopies(root, target, input.ResourceName);
            if (copies.Count > 0)
            {
                items.Add(new ExportCheckItem(true,
                    copies.Count == 1
                        ? $"Une ancienne exportation de « {input.ResourceName} » se trouve dans un autre dossier"
                        : $"{copies.Count} anciennes exportations de « {input.ResourceName} » se trouvent dans d'autres dossiers",
                    copies,
                    "Le dossier d'export a changé depuis la dernière exportation : le serveur aurait deux ressources du même nom. "
                    + "Supprimez l'ancienne copie, ou choisissez le même dossier d'export qu'avant."));
            }
            ownFolders = [target, .. copies];
            var conflicts = ConflictScanner.ScanResources(root, ownFolders, fileNames);
            foreach (var group in conflicts.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ExportCheckItem(true,
                    $"{group.Key} est aussi livré par {Owners(group, "la ressource", "les ressources")}",
                    group.Select(c => c.Path).ToList(),
                    "FiveM ne charge qu'une version d'un même YMAP : les changements de l'une des deux ressources seront perdus "
                    + "(objets en double, suppression annulée...). Reprenez ses changements dans ce projet, puis retirez le "
                    + "fichier de l'autre ressource.", group.ToList()));
            }
            scanned = conflicts.Count;
            summary = $"Dossier analysé : {root}";
        }
        else
        {
            summary = "Choisissez le dossier « resources » du serveur pour y chercher les conflits.";
        }

        foreach (var group in ConflictScanner.ScanProjects(input.OtherProjects, fileNames).GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new ExportCheckItem(true,
                $"{group.Key} est aussi modifié par {Owners(group, "le projet", "les projets")}",
                group.Select(c => c.Path).ToList(),
                "Si les deux projets sont installés sur le même serveur, un seul des deux fichiers sera chargé. "
                + "Reprenez ses changements dans ce projet, puis retirez le fichier de l'autre projet.", group.ToList()));
        }

        // Deleted scenery (now or in an earlier session): compared with the game's file.
        var scenery = new List<string>();
        var sceneryEntities = new List<(MapEntity, Archetype?)>();
        int removedTotal = 0;
        if (world.Content is { } content)
        {
            var reader = new YmapReader();
            foreach (var (fileName, gamePath, isCustom, kept) in input.Maps)
            {
                if (isCustom || gamePath is null || content.ReadFile(gamePath) is not { } bytes)
                    continue;
                MapData original;
                try
                {
                    original = reader.Read(bytes);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                var removed = RemovedEntities.Find(original.Entities, kept);
                removedTotal += removed.Count;
                foreach (var entity in removed)
                {
                    var archetype = world.Maps?.GetArchetype(entity.ArchetypeHash);
                    if (!RemovedEntities.IsScenery(entity, archetype))
                        continue;
                    scenery.Add($"{names.Describe(entity.ArchetypeHash)} ({fileName})");
                    sceneryEntities.Add((entity, archetype));
                }
            }
        }
        // Their collision, in the area's .ybn files: removed polygon by polygon (the files are shared with the neighbours).
        var cuts = new List<CollisionFileCut>();
        var collisionErrors = new List<string>();
        if (sceneryEntities.Count > 0 && world.Collisions is { } collisions)
            cuts = CollisionRemoval.Plan(collisions, sceneryEntities, collisionErrors, input.RemoveGround);
        if (cuts.Count > 0 && scanRoot is not null)
        {
            // A collision file covers a whole block: another resource changing anything in it ships the same file.
            var collisionConflicts = ConflictScanner.ScanResources(scanRoot, ownFolders, cuts.Select(c => c.FileName).ToList());
            foreach (var group in collisionConflicts.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ExportCheckItem(true,
                    $"{group.Key} (collision) est aussi livré par {Owners(group, "la ressource", "les ressources")}",
                    group.Select(c => c.Path).ToList(),
                    "FiveM ne charge qu'une version de ce fichier de collision : décochez-le plus bas, ou retirez-le de "
                    + "l'autre ressource."));
            }
            scanned += collisionConflicts.Count;
        }
        // Their occluders, in the game's occluder map files (one mesh stands for a whole block): removed triangle by triangle.
        var occlusionCuts = new List<OccluderFileCut>();
        if (sceneryEntities.Count > 0)
            occlusionCuts = OcclusionRemoval.Plan(OccluderMaps(world), sceneryEntities);
        if (occlusionCuts.Count > 0 && scanRoot is not null)
        {
            var occlusionConflicts = ConflictScanner.ScanResources(scanRoot, ownFolders, occlusionCuts.Select(c => c.FileName).ToList());
            foreach (var group in occlusionConflicts.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ExportCheckItem(true,
                    $"{group.Key} (occlusion) est aussi livré par {Owners(group, "la ressource", "les ressources")}",
                    group.Select(c => c.Path).ToList(),
                    "FiveM ne charge qu'une version de ce fichier d'occlusion : décochez-le plus bas, ou retirez-le de "
                    + "l'autre ressource."));
            }
            scanned += occlusionConflicts.Count;
        }
        if (occlusionCuts.Count > 0)
        {
            items.Add(new ExportCheckItem(false,
                $"Occlusion retirée de {occlusionCuts.Count} fichier(s) (voir plus bas)",
                Limit(occlusionCuts.Select(c => c.FileName).ToList()),
                "Le jeu ne dessine pas ce qui est caché derrière un « occluder » : laissé à la place d'un bâtiment supprimé, "
                + "il ferait des trous dans le décor derrière lui. Ceux qui touchent ces bâtiments sont retirés."));
        }
        if (scenery.Count > 0)
        {
            string objects = scenery.Count == 1 ? "1 objet du décor supprimé" : $"{scenery.Count} objets du décor supprimés";
            items.Add(cuts.Count > 0
                ? new ExportCheckItem(false,
                    $"{objects} : collision retirée de {cuts.Count} fichier(s) .ybn (voir plus bas)",
                    Limit(scenery),
                    "Leur collision est dans les fichiers .ybn de la zone, partagés avec les voisins : seuls les morceaux "
                    + "situés dans la boîte de ces objets sont retirés, le sol est gardé. Vérifiez l'aperçu dans la vue 3D.")
                : new ExportCheckItem(true,
                    $"{objects} : leur collision reste en jeu",
                    Limit([.. scenery, .. collisionErrors]),
                    "Aucun morceau de collision n'a été trouvé dans leur boîte (collisions non chargées ou fichiers illisibles) : "
                    + "un mur invisible peut rester à leur place."));
        }
        if (input.StillSeenFromFar.Count > 0)
        {
            items.Add(new ExportCheckItem(false,
                input.StillSeenFromFar.Count == 1 ? "1 objet supprimé reste visible de loin" : $"{input.StillSeenFromFar.Count} objets supprimés restent visibles de loin",
                Limit(input.StillSeenFromFar),
                "Leur version simplifiée (LOD) représente aussi d'autres objets : elle est gardée."));
        }

        summary = $"{input.Maps.Count} YMAP vérifiés ({removedTotal} objets retirés du jeu, {scanned} conflits). {summary}";
        return new Result(items, summary, cuts, occlusionCuts);
    }

    private static readonly object OccluderLock = new();
    private static (MapLibrary Library, List<(string GamePath, MapOccluders Occluders)> Files)? _occluderMaps;

    // The occluders of the loaded map, by file: map files without objects (the game keeps occluders apart,
    // *_occl_*.ymap).
    private static List<(string GamePath, MapOccluders Occluders)> OccluderMaps(WorldLoader world)
    {
        if (world.Maps is not { } library || world.Content is not { } content)
            return [];
        lock (OccluderLock)
        {
            if (_occluderMaps is { } cached && cached.Library == library)
                return cached.Files;
            var reader = new OccluderFile();
            var files = new List<(string, MapOccluders)>();
            foreach (var map in library.Maps)
            {
                if (map.IsCustom || map.FilePath is not { } path || map.Entities.Count > 0 || content.ReadFile(path) is not { } bytes)
                    continue;
                try
                {
                    if (reader.ReadOccluders(bytes) is { IsEmpty: false } occluders)
                        files.Add((path, occluders));
                }
                catch (InvalidDataException)
                {
                    // Grass maps (data in the graphics part) and damaged files: no occluders to read.
                }
            }
            _occluderMaps = (library, files);
            return files;
        }
    }

    private static string Owners(IEnumerable<MapConflict> conflicts, string one, string several)
    {
        var owners = conflicts.Select(c => $"« {c.Owner} »").Distinct().ToList();
        return $"{(owners.Count == 1 ? one : several)} {string.Join(", ", owners)}";
    }

    private static List<string> Limit(List<string> lines) =>
        lines.Count <= MaxDetails ? lines : [.. lines.Take(MaxDetails), $"... et {lines.Count - MaxDetails} autres"];
}
