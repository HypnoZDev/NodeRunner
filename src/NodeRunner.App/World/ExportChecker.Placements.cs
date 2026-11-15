using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Data.Scenarios;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Scenario points and road nodes left on a deleted object, in the air or under the ground: those moved or added by
// hand, and those over a deleted building (its collision is removed by the export).
internal static partial class ExportChecker
{
    // A deleted object this wide or less is a prop (bench, table...): a point on it loses what it was for.
    private const float SmallObject = 20f;

    private const float GridCell = 16f;

    // Ground checks at most (a few milliseconds each).
    private const int MaxGroundChecks = 3000;

    // Scenarios that do not stand on the ground: boats, swimming and flying animals.
    private static readonly string[] OffGround = ["BOAT", "FISH", "SHARK", "WHALE", "DOLPHIN", "STINGRAY", "FLYING", "HELI", "PLANE", "SWIM", "JETSKI", "SUB"];

    private static void CheckPlacements(Input input, WorldLoader world, HashNames names, List<(MapEntity, Archetype?)> removed,
        List<CollisionFileCut> cuts, List<ExportCheckItem> items)
    {
        var points = input.Scenarios ?? [];
        var nodes = input.Nodes ?? [];
        var pointGrid = Grid(points.Select(p => p.Position));
        var nodeGrid = Grid(nodes.Select(n => n.Position));

        var onObject = new Dictionary<int, string>();
        var pointsToCheck = new HashSet<int>(Enumerable.Range(0, points.Count).Where(i => points[i].Changed));
        var nodesToCheck = new HashSet<int>(Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Changed));
        foreach (var (entity, archetype) in removed)
        {
            if (GroundSnap.WorldBox(entity, archetype) is not { } box)
                continue;
            var size = box.Max - box.Min;
            bool small = MathF.Max(size.X, size.Y) <= SmallObject;
            var min = box.Min - new Vector3(0.3f, 0.3f, 0.5f);
            var max = box.Max + new Vector3(0.3f, 0.3f, 1f);
            foreach (int i in Near(pointGrid, min, max))
            {
                if (!Inside(points[i].Position, min, max))
                    continue;
                if (small)
                    onObject.TryAdd(i, names.Describe(entity.ArchetypeHash));
                else
                    pointsToCheck.Add(i);
            }
            if (small)
                continue;
            foreach (int i in Near(nodeGrid, min, max))
            {
                if (Inside(nodes[i].Position, min, max))
                    nodesToCheck.Add(i);
            }
        }
        // The ground as it will be in game: without the collision the export removes.
        var edits = new CollisionEdits();
        foreach (var cut in cuts)
            edits.SetRemoved(cut.File.Path, cut.Polygons, true);
        var query = new CollisionQuery();
        int budget = MaxGroundChecks;
        List<PlacedCollision>? Around(Vector3 p)
        {
            if (world.Collisions is null || budget-- <= 0)
                return null;
            var around = new Vector3(1f, 1f, PlacementCheck.SearchHeight);
            return query.CollisionIn(world, p - around, p + around, edits);
        }
        PlacementCheck.Kind? Ground(Vector3 p, float maxGap) => Around(p) is { } c ? PlacementCheck.Classify(c, p, maxGap) : null;

        // Points in a deleted prop's box: on it only when well above the ground (not at the foot of a tree).
        var onDeleted = new Dictionary<int, string>();
        foreach (var (i, name) in onObject)
        {
            if (Around(points[i].Position) is { } c && PlacementCheck.GroundGap(c, points[i].Position) is { } gap && gap > PlacementCheck.OnObjectHeight)
            {
                onDeleted[i] = name;
                pointsToCheck.Remove(i);
            }
        }

        var pointIssues = new List<(PointCopy Point, PlacementCheck.Kind Kind)>();
        foreach (int i in pointsToCheck.Order())
        {
            var point = points[i];
            string type = point.TypeName?.ToUpperInvariant() ?? "";
            if (OffGround.Any(type.Contains))
                continue;
            float gap = point.Category == ScenarioCategory.PedSeated ? PlacementCheck.MaxSeatGap : PlacementCheck.MaxGap;
            if (Ground(point.Position, gap) is { } kind)
                pointIssues.Add((point, kind));
        }
        var nodeIssues = new List<(NodeCopy Node, PlacementCheck.Kind Kind)>();
        foreach (int i in nodesToCheck.Order())
        {
            if (Ground(nodes[i].Position, PlacementCheck.MaxGap) is { } kind)
                nodeIssues.Add((nodes[i], kind));
        }

        string Where(Vector3 p) => $"({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0})";
        string Type(PointCopy p) => p.TypeName ?? L("point");

        if (onDeleted.Count > 0)
        {
            int n = onDeleted.Count;
            items.Add(new ExportCheckItem(true,
                n == 1 ? L("1 point de scénario sur un objet supprimé") : L($"{n} points de scénario sur un objet supprimé"),
                Limit(onDeleted.OrderBy(d => d.Key).Select(d => L($"{Type(points[d.Key])} sur {d.Value} {Where(points[d.Key].Position)}")).ToList()),
                L("Le PNJ jouera son activité dans le vide (s'asseoir sur un banc disparu...). Cliquez sur une ligne, puis Suppr ")
                + L("pour retirer le point, ou déplacez-le."),
                ScenarioPoints: onDeleted.Keys.Order().Take(MaxDetails).Select(i => points[i].Point).ToList()));
        }
        foreach (var group in pointIssues.GroupBy(p => p.Kind).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            int n = list.Count;
            var (title, advice) = group.Key == PlacementCheck.Kind.Floating
                ? (n == 1 ? L("1 point de scénario en l'air") : L($"{n} points de scénario en l'air"),
                    L("Aucun sol à moins d'un mètre sous le point (point déplacé trop haut, ou bâtiment supprimé dessous) : le PNJ ou ")
                    + L("le véhicule apparaîtra en l'air ou tombera. Redescendez-le aux poignées."))
                : (n == 1 ? L("1 point de scénario sous le sol") : L($"{n} points de scénario sous le sol"),
                    L("Une surface couvre le point, sans sol juste dessous : le PNJ ou le véhicule apparaîtra dans le sol. ")
                    + L("Remontez-le aux poignées."));
            items.Add(new ExportCheckItem(true, title, Limit(list.Select(p => $"{Type(p.Point)} {Where(p.Point.Position)}").ToList()), advice,
                ScenarioPoints: list.Take(MaxDetails).Select(p => p.Point.Point).ToList()));
        }
        foreach (var group in nodeIssues.GroupBy(p => p.Kind).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            int n = list.Count;
            var (title, advice) = group.Key == PlacementCheck.Kind.Floating
                ? (n == 1 ? L("1 nœud de route en l'air") : L($"{n} nœuds de route en l'air"),
                    L("Aucun sol à moins d'un mètre sous le nœud (nœud déplacé trop haut, ou pont / bâtiment supprimé dessous) : ")
                    + L("voitures ou piétons y passeront dans le vide. Redescendez-le aux poignées, ou supprimez-le."))
                : (n == 1 ? L("1 nœud de route sous le sol") : L($"{n} nœuds de route sous le sol"),
                    L("Une surface couvre le nœud, sans sol juste dessous : voitures ou piétons y passeront sous le sol. ")
                    + L("Remontez-le aux poignées."));
            static string Id(PathNodeId id) => $"{id.AreaId}:{id.NodeId}";
            items.Add(new ExportCheckItem(true, title,
                Limit(list.Select(p => L($"Nœud {Id(p.Node.Node.Id)} {Where(p.Node.Position)}")).ToList()), advice,
                PathNodes: list.Take(MaxDetails).Select(p => p.Node.Node).ToList()));
        }
    }

    private static bool Inside(Vector3 p, Vector3 min, Vector3 max) => Vector3.Clamp(p, min, max) == p;

    private static Dictionary<(int, int), List<int>> Grid(IEnumerable<Vector3> positions)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        int i = 0;
        foreach (var p in positions)
        {
            var cell = ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Y / GridCell));
            if (!grid.TryGetValue(cell, out var list))
                grid[cell] = list = [];
            list.Add(i++);
        }
        return grid;
    }

    private static IEnumerable<int> Near(Dictionary<(int, int), List<int>> grid, Vector3 min, Vector3 max)
    {
        for (int x = (int)MathF.Floor(min.X / GridCell); x <= (int)MathF.Floor(max.X / GridCell); x++)
        {
            for (int y = (int)MathF.Floor(min.Y / GridCell); y <= (int)MathF.Floor(max.Y / GridCell); y++)
            {
                if (grid.TryGetValue((x, y), out var list))
                {
                    foreach (int i in list)
                        yield return i;
                }
            }
        }
    }
}
