using NodeRunner.Data.Collisions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// One line of the check before an export: a problem (or a remark), the objects or files concerned, and what to do.
// Merge: the other versions of the same map file whose changes can be taken into ours. Occluders: the box of each
// detail line, shown on click. Entities: the object of each detail line, selected on click.
internal sealed record ExportCheckItem(bool IsProblem, string Title, IReadOnlyList<string> Details, string? Advice = null,
    IReadOnlyList<MapConflict>? Merge = null, IReadOnlyList<OccluderSelection>? Occluders = null,
    IReadOnlyList<MapEntity>? Entities = null);

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
        // Occluder map files of the game changed by hand ship like the other map files.
        var fileNames = input.Maps.Select(m => m.FileName)
            .Concat(OccluderMaps.Get(world).Where(f => f.HasChanges).Select(f => f.FileName))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
                    L($"FiveM ne trouvera pas la ressource : « {string.Join("\\", plain)} » n'est pas un dossier de catégorie"),
                    [target],
                    L("Dans le dossier « resources », FiveM n'ouvre que les dossiers entre crochets (ex. [maps]) : renommez ")
                    + L($"le dossier en « [{plain[^1]}] », ou exportez directement dans « resources ».")));
            }
            // An earlier export of this project to another folder: one remark, not a conflict per file.
            var copies = ConflictScanner.FindOtherCopies(root, target, input.ResourceName);
            if (copies.Count > 0)
            {
                items.Add(new ExportCheckItem(true,
                    copies.Count == 1
                        ? L($"Une ancienne exportation de « {input.ResourceName} » se trouve dans un autre dossier")
                        : L($"{copies.Count} anciennes exportations de « {input.ResourceName} » se trouvent dans d'autres dossiers"),
                    copies,
                    L("Le dossier d'export a changé depuis la dernière exportation : le serveur aurait deux ressources du même nom. ")
                    + L("Supprimez l'ancienne copie, ou choisissez le même dossier d'export qu'avant.")));
            }
            ownFolders = [target, .. copies];
            var conflicts = ConflictScanner.ScanResources(root, ownFolders, fileNames);
            foreach (var group in conflicts.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ExportCheckItem(true,
                    L($"{group.Key} est aussi livré par {Owners(group, L("la ressource"), L("les ressources"))}"),
                    group.Select(c => c.Path).ToList(),
                    L("FiveM ne charge qu'une version d'un même YMAP : les changements de l'une des deux ressources seront perdus ")
                    + L("(objets en double, suppression annulée...). Reprenez ses changements dans ce projet, puis retirez le ")
                    + L("fichier de l'autre ressource."), group.ToList()));
            }
            scanned = conflicts.Count;
            summary = L($"Dossier analysé : {root}");
        }
        else
        {
            summary = L("Choisissez le dossier « resources » du serveur pour y chercher les conflits.");
        }

        foreach (var group in ConflictScanner.ScanProjects(input.OtherProjects, fileNames).GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new ExportCheckItem(true,
                L($"{group.Key} est aussi modifié par {Owners(group, L("le projet"), L("les projets"))}"),
                group.Select(c => c.Path).ToList(),
                L("Si les deux projets sont installés sur le même serveur, un seul des deux fichiers sera chargé. ")
                + L("Reprenez ses changements dans ce projet, puis retirez le fichier de l'autre projet."), group.ToList()));
        }

        var duplicates = DuplicateGuids.Find(input.Maps.Select(m => (m.FileName, (IEnumerable<MapEntity>)m.Kept)));
        if (duplicates.Count > 0)
        {
            items.Add(new ExportCheckItem(false,
                L($"{duplicates.Count} identifiant(s) (guid) utilisé(s) par plusieurs objets"),
                duplicates.Take(MaxDetails).Select(d => L($"0x{d.Guid:X8} : {names.Describe(d.ArchetypeHash)} ({string.Join(", ", d.Files.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key))})"))
                    .Concat(duplicates.Count > MaxDetails ? [L($"... et {duplicates.Count - MaxDetails} autre(s)")] : [])
                    .ToList(),
                L("Souvent des objets copiés à la main dans un autre éditeur. Le jeu l'accepte, mais un script ou un outil qui ")
                + L("cherche un objet par son guid peut tomber sur le mauvais. Pour corriger : supprimer la copie et la recréer ")
                + L("(Ctrl+C / Ctrl+V dans NodeRunner donne un nouveau guid).")));
        }

        // Deleted scenery (now or in an earlier session): compared with the game's file.
        var scenery = new List<string>();
        var sceneryEntities = new List<(MapEntity, Archetype?)>();
        int removedTotal = 0;
        var removedDoors = new List<string>();
        var movedDoors = new List<DoorCheck.MovedDoor>();
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
                var doors = DoorCheck.Changed(original.Entities, kept, names.TryGet);
                removedDoors.AddRange(doors.Removed.Select(d => $"{names.Describe(d.ArchetypeHash)} ({fileName})"));
                movedDoors.AddRange(doors.Moved);
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
                    L($"{group.Key} (collision) est aussi livré par {Owners(group, L("la ressource"), L("les ressources"))}"),
                    group.Select(c => c.Path).ToList(),
                    L("FiveM ne charge qu'une version de ce fichier de collision : décochez-le plus bas, ou retirez-le de ")
                    + L("l'autre ressource.")));
            }
            scanned += collisionConflicts.Count;
        }
        // Their occluders, in the game's occluder map files (one mesh stands for a whole block): removed triangle by triangle.
        var occlusionCuts = new List<OccluderFileCut>();
        if (sceneryEntities.Count > 0)
            occlusionCuts = OcclusionRemoval.Plan(OccluderMaps.Get(world).Select(f => (f.GamePath, f.Current)), sceneryEntities);
        if (occlusionCuts.Count > 0 && scanRoot is not null)
        {
            var occlusionConflicts = ConflictScanner.ScanResources(scanRoot, ownFolders, occlusionCuts.Select(c => c.FileName).ToList());
            foreach (var group in occlusionConflicts.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new ExportCheckItem(true,
                    L($"{group.Key} (occlusion) est aussi livré par {Owners(group, L("la ressource"), L("les ressources"))}"),
                    group.Select(c => c.Path).ToList(),
                    L("FiveM ne charge qu'une version de ce fichier d'occlusion : décochez-le plus bas, ou retirez-le de ")
                    + L("l'autre ressource.")));
            }
            scanned += occlusionConflicts.Count;
        }
        if (occlusionCuts.Count > 0)
        {
            items.Add(new ExportCheckItem(false,
                L($"Occlusion retirée de {occlusionCuts.Count} fichier(s) (voir plus bas)"),
                Limit(occlusionCuts.Select(c => c.FileName).ToList()),
                L("Le jeu ne dessine pas ce qui est caché derrière un « occluder » : laissé à la place d'un bâtiment supprimé, ")
                + L("il ferait des trous dans le décor derrière lui. Ceux qui touchent ces bâtiments sont retirés.")));
        }
        if (scenery.Count > 0)
        {
            string objects = scenery.Count == 1 ? L("1 objet du décor supprimé") : L($"{scenery.Count} objets du décor supprimés");
            items.Add(cuts.Count > 0
                ? new ExportCheckItem(false,
                    L($"{objects} : collision retirée de {cuts.Count} fichier(s) .ybn (voir plus bas)"),
                    Limit(scenery),
                    L("Leur collision est dans les fichiers .ybn de la zone, partagés avec les voisins : seuls les morceaux ")
                    + L("situés dans la boîte de ces objets sont retirés, le sol est gardé. Vérifiez l'aperçu dans la vue 3D."))
                : new ExportCheckItem(true,
                    L($"{objects} : leur collision reste en jeu"),
                    Limit([.. scenery, .. collisionErrors]),
                    L("Aucun morceau de collision n'a été trouvé dans leur boîte (collisions non chargées ou fichiers illisibles) : ")
                    + L("un mur invisible peut rester à leur place.")));
        }
        if (input.StillSeenFromFar.Count > 0)
        {
            items.Add(new ExportCheckItem(false,
                input.StillSeenFromFar.Count == 1 ? L("1 objet supprimé reste visible de loin") : L($"{input.StillSeenFromFar.Count} objets supprimés restent visibles de loin"),
                Limit(input.StillSeenFromFar),
                L("Leur version simplifiée (LOD) représente aussi d'autres objets : elle est gardée.")));
        }

        CheckDoors(input, world, names, sceneryEntities.Select(s => s.Item1).ToList(), removedDoors, movedDoors, items);
        CheckOccluders(world, names, items);

        summary = L($"{input.Maps.Count} YMAP vérifiés ({removedTotal} objets retirés du jeu, {scanned} conflits). {summary}");
        return new Result(items, summary, cuts, occlusionCuts);
    }

    // Doors left where a building was deleted, doors at the same place, game doors moved or removed (door lock scripts
    // find doors by their position), door models of the project that will not open.
    private static void CheckDoors(Input input, WorldLoader world, HashNames names, List<MapEntity> deletedScenery,
        List<string> removedDoors, List<DoorCheck.MovedDoor> movedDoors, List<ExportCheckItem> items)
    {
        if (world.Maps is not { } library)
            return;
        string Describe(MapEntity e) => L($"{names.Describe(e.ArchetypeHash)} ({e.Position.X:0.00}, {e.Position.Y:0.00}, {e.Position.Z:0.00})");
        void Add(bool problem, string title, List<MapEntity> entities, string advice) =>
            items.Add(new ExportCheckItem(problem, title, Limit(entities.Select(Describe).ToList()), advice, Entities: entities.Take(MaxDetails).ToList()));

        var left = deletedScenery.Where(e => !Doors.IsDoor(names.TryGet(e.ArchetypeHash)))
            .SelectMany(e => Doors.Of(e, library, names.TryGet)).Distinct().ToList();
        if (left.Count > 0)
            Add(true, left.Count == 1 ? L("1 porte reste à la place d'un bâtiment supprimé") : L($"{left.Count} portes restent à la place d'un bâtiment supprimé"), left,
                L("Les portes sont des objets à part : elles flotteront dans le vide en jeu. Cliquez sur « Sélectionner » puis Suppr, ")
                + L("ou gardez-les si un autre bâtiment les utilise."));

        var kept = input.Maps.SelectMany(m => m.Kept)
            .Where(e => e.LodLevel is LodLevel.Hd or LodLevel.OrphanHd && Doors.IsDoor(names.TryGet(e.ArchetypeHash))).ToList();
        var pairs = DoorCheck.Duplicates(kept, library.EntitiesAround, names.TryGet);
        if (pairs.Count > 0)
            Add(true, pairs.Count == 1 ? L("2 portes au même endroit") : L($"{pairs.Count} paires de portes au même endroit"),
                pairs.SelectMany(p => new[] { p.A, p.B }).Distinct().ToList(),
                L("Souvent une nouvelle porte posée sans supprimer celle du jeu : les deux se chevauchent et la porte semble ")
                + L("bloquée ou clignote. Supprimez celle qui est en trop."));

        if (movedDoors.Count > 0 || removedDoors.Count > 0)
        {
            var lines = movedDoors.Select(m => L($"{Describe(m.Current)} : déplacée de {System.Numerics.Vector3.Distance(m.Original.Position, m.Current.Position):0.00} m"))
                .Concat(removedDoors.Select(r => L($"{r} : supprimée"))).ToList();
            int count = lines.Count;
            items.Add(new ExportCheckItem(false,
                count == 1 ? L("1 porte du jeu déplacée ou supprimée") : L($"{count} portes du jeu déplacées ou supprimées"),
                Limit(lines),
                L("Les scripts de serrure (qb-doorlock, ox_doorlock...) trouvent une porte par son modèle et sa position exacte : ")
                + L("si votre serveur verrouille ces portes, mettez sa configuration à jour (sélectionnez-les, puis Propriétés > ... > ")
                + L("Copier pour doorlock)."),
                Entities: movedDoors.Take(MaxDetails).Select(m => m.Current).ToList()));
        }

        var frozen = kept.Where(e => library.GetArchetype(e.ArchetypeHash) is { } a && DoorCheck.IsFrozen(a, library.GetArchetypeFile(e.ArchetypeHash)))
            .GroupBy(e => e.ArchetypeHash).Select(g => g.First()).ToList();
        if (frozen.Count > 0)
            Add(false, frozen.Count == 1 ? L("1 modèle de porte du projet ne s'ouvrira pas") : L($"{frozen.Count} modèles de porte du projet ne s'ouvriront pas"), frozen,
                L($"Leur type (archétype, dans le .ytyp) n'a pas le drapeau « Dynamic » (0x{DoorCheck.DynamicFlag:X}) : le jeu les garde figées. ")
                + L($"Dans Sollumz, cochez « Dynamic » dans les drapeaux de l'archétype (les portes du jeu ont aussi 0x{DoorCheck.DoorFlag:X}), ")
                + L("puis réexportez le .ytyp. Ignorez ce message si la porte doit rester fermée."));
    }

    // The boxes made or changed by hand, against the buildings, interiors, roads and the other boxes around them.
    private static void CheckOccluders(WorldLoader world, HashNames names, List<ExportCheckItem> items)
    {
        var checkedBoxes = new List<OccluderSelection>();
        var others = new List<OccluderSelection>();
        foreach (var file in OccluderMaps.Get(world))
        {
            for (int i = 0; i < file.Boxes.Count; i++)
            {
                if (!file.DeletedBoxes.Contains(i))
                    (file.IsProject || file.IsBoxChanged(i) ? checkedBoxes : others).Add(new OccluderSelection(file, OcclusionLayerPart.Boxes, i));
            }
        }
        if (checkedBoxes.Count == 0)
            return;
        var boxes = checkedBoxes.Select(s => s.Box!).ToList();

        var buildings = new List<PlacedVolume>();
        var interiors = new List<PlacedVolume>();
        if (world.Maps is { } library)
        {
            var seen = new HashSet<MapEntity>();
            foreach (var box in boxes)
            {
                float reach = new System.Numerics.Vector2(box.Size.X, box.Size.Y).Length() / 2 + 200f;
                foreach (var entity in library.EntitiesAround(new System.Numerics.Vector2(box.Center.X, box.Center.Y), reach))
                {
                    if (entity.Interior is not null || !seen.Add(entity)
                        || GroundSnap.LocalBox(library.GetArchetype(entity.ArchetypeHash)) is not var (min, max))
                        continue;
                    if (entity.IsInterior)
                        interiors.AddRange(OccluderCheck.Rooms(entity, library.GetArchetype(entity.ArchetypeHash)!, min, max));
                    else if (entity.LodLevel is LodLevel.Hd or LodLevel.OrphanHd && System.Numerics.Vector3.Distance(min, max) >= 3f)
                        buildings.Add(new PlacedVolume(entity.Transform, min, max));
                }
            }
        }

        var paths = new List<System.Numerics.Vector3>();
        if (world.PathNetwork is { } network)
        {
            var low = boxes.Select(b => b.Center - b.Size).Aggregate(System.Numerics.Vector3.Min);
            var high = boxes.Select(b => b.Center + b.Size).Aggregate(System.Numerics.Vector3.Max);
            foreach (var node in network.Areas.SelectMany(a => a.Nodes))
            {
                var p = node.Position;
                if (p.X >= low.X && p.X <= high.X && p.Y >= low.Y && p.Y <= high.Y && p.Z >= low.Z - 3f && p.Z <= high.Z)
                    paths.Add(p);
            }
        }

        var issues = OccluderCheck.Run(boxes, others.Select(s => s.Box!).ToList(), buildings, interiors, paths);
        string Name(OccluderSelection s) => L($"{s.File.FileName} : boîte {s.Index}");
        void Add(OccluderIssueKind kind, bool problem, string one, string several, string advice, Func<OccluderIssue, string> detail)
        {
            var found = issues.Where(i => i.Kind == kind).ToList();
            if (found.Count == 0)
                return;
            items.Add(new ExportCheckItem(problem, found.Count == 1 ? one : $"{found.Count} {several}",
                Limit(found.Select(detail).ToList()), advice, Occluders: found.Take(MaxDetails).Select(i => checkedBoxes[i.Box]).ToList()));
        }

        Add(OccluderIssueKind.InsideInterior, false, L("1 boîte d'occlusion empiète sur un intérieur (MLO)"), L("boîtes d'occlusion empiètent sur un intérieur (MLO)"),
            L("Le jeu ne dessine pas ce qui est derrière une boîte : vus de dehors (porte ouverte, fenêtres), les objets de ")
            + L("l'intérieur peuvent disparaître. Si l'intérieur se voit de dehors, réduisez la boîte pour qu'elle reste hors de ses pièces."),
            i => L($"{Name(checkedBoxes[i.Box])} (intérieur {names.Describe((uint)i.Other)})"));
        Add(OccluderIssueKind.OutsideBuildings, true, L("1 boîte d'occlusion dépasse des bâtiments"), L("boîtes d'occlusion dépassent des bâtiments"),
            L("Une partie de la boîte est dans le vide (rue, cour, ciel) : ce qui se trouve derrière disparaîtra en jeu alors qu'on ")
            + L("devrait le voir. Gardez la boîte à l'intérieur des murs, et vérifiez avec Calques > Occlusion > « Aperçu comme le jeu »."),
            i => Name(checkedBoxes[i.Box]));
        Add(OccluderIssueKind.AcrossPath, true, L("1 boîte d'occlusion sur une route ou un trottoir"), L("boîtes d'occlusion sur une route ou un trottoir"),
            L("Des véhicules ou des piétons du jeu passent dans la boîte : elle est sûrement à côté du bâtiment au lieu d'être dedans."),
            i => Name(checkedBoxes[i.Box]));
        Add(OccluderIssueKind.TooSmall, false, L("1 boîte d'occlusion trop petite"), L("boîtes d'occlusion trop petites"),
            L("Moins de 4 m² sur sa plus grande face : elle ne cache presque rien. Agrandissez-la ou supprimez-la."),
            i => Name(checkedBoxes[i.Box]));
        Add(OccluderIssueKind.Duplicate, false, L("1 boîte d'occlusion en double"), L("boîtes d'occlusion en double"),
            L("Elle est presque entièrement dans une autre boîte : elle ne cache rien de plus. Supprimez-la."),
            i => L($"{Name(checkedBoxes[i.Box])} (dans {Name(i.Other >= 0 ? checkedBoxes[(int)i.Other] : others[(int)(-1 - i.Other)])})"));
    }

    private static string Owners(IEnumerable<MapConflict> conflicts, string one, string several)
    {
        var owners = conflicts.Select(c => $"« {c.Owner} »").Distinct().ToList();
        return $"{(owners.Count == 1 ? one : several)} {string.Join(", ", owners)}";
    }

    private static List<string> Limit(List<string> lines) =>
        lines.Count <= MaxDetails ? lines : [.. lines.Take(MaxDetails), L($"... et {lines.Count - MaxDetails} autres")];
}
