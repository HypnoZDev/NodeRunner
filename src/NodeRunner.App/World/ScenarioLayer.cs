using System.Numerics;
using NodeRunner.Data.Scenarios;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Draws scenario points as small markers: a vertical post (about a person's height) and a short line showing the
// direction the ped or vehicle faces.
internal sealed class ScenarioLayer : IDisposable
{
    private const float PostHeight = 1.8f;
    private const float ArrowHeight = 1.2f;
    private const float ArrowLength = 0.8f;

    private const float FadeDistance = 2000f;

    public static readonly IReadOnlyList<ScenarioCategory> Categories = Enum.GetValues<ScenarioCategory>();

    private readonly ColorMesh?[] _meshes = new ColorMesh?[Categories.Count];
    private readonly int[] _counts = new int[Categories.Count];
    private ScenarioSet? _built;
    private int _builtRevision;

    public static string Label(ScenarioCategory category) => category switch
    {
        ScenarioCategory.PedActivity => L("Piétons (activités, marche)"),
        ScenarioCategory.PedSeated => L("Piétons assis"),
        ScenarioCategory.Vehicle => L("Véhicules"),
        ScenarioCategory.Animal => L("Animaux"),
        ScenarioCategory.Other => L("Autres"),
        _ => category.ToString(),
    };

    public static Vector4 Color(ScenarioCategory category) => category switch
    {
        ScenarioCategory.PedActivity => new(0.3f, 0.95f, 0.35f, 1f),
        ScenarioCategory.PedSeated => new(1f, 0.9f, 0.2f, 1f),
        ScenarioCategory.Vehicle => new(0.65f, 0.45f, 1f, 1f),
        ScenarioCategory.Animal => new(0.95f, 0.95f, 0.95f, 1f),
        ScenarioCategory.Other => new(0.55f, 0.55f, 0.55f, 1f),
        _ => Vector4.One,
    };

    public static string LayerName(ScenarioCategory category) => "Scenario." + category;

    public int Count(ScenarioCategory category) => _counts[(int)category];

    public void Update(WorldLoader loader, ColorRenderer renderer)
    {
        var scenarios = loader.Scenarios;
        if (scenarios == _built && scenarios?.Revision == _builtRevision)
            return;

        _built = scenarios;
        _builtRevision = scenarios?.Revision ?? 0;
        DisposeMeshes();
        if (scenarios is null)
            return;

        var batches = Categories.Select(_ => new LineBatch()).ToArray();
        foreach (var point in scenarios.AllPoints)
        {
            int index = (int)point.Category;
            var color = Color(point.Category);
            var arrowStart = point.Position + new Vector3(0f, 0f, ArrowHeight);

            batches[index].AddLine(point.Position, point.Position + new Vector3(0f, 0f, PostHeight), color);
            batches[index].AddLine(arrowStart, arrowStart + point.Direction * ArrowLength, color);
            _counts[index]++;
        }

        for (int i = 0; i < batches.Length; i++)
            _meshes[i] = renderer.CreateMesh(batches[i]);
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<ScenarioCategory, bool> isVisible)
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            if (_meshes[i] is { } mesh && isVisible((ScenarioCategory)i))
                renderer.Draw(mesh, viewProjection, cameraPosition, FadeDistance);
        }
    }

    private const float PickPixels = 10f;
    private const float PickDistance = 400f;

    // The point closest to the mouse on screen (its post, within a few pixels), among the shown categories, or null.
    public static ScenarioPoint? Pick(ScenarioSet set, Matrix4x4 viewProjection, Vector3 camera, Vector2 mouse, Vector2 viewSize,
        Func<ScenarioCategory, bool> isVisible)
    {
        ScenarioPoint? best = null;
        float bestScore = PickPixels * PickPixels;
        foreach (var point in set.AllPoints)
        {
            if (Vector3.DistanceSquared(point.Position, camera) > PickDistance * PickDistance || !isVisible(point.Category))
                continue;
            // Anywhere along the post.
            foreach (float z in (ReadOnlySpan<float>)[0.2f, 0.9f, PostHeight])
            {
                var clip = Vector4.Transform(new Vector4(point.Position + new Vector3(0, 0, z), 1f), viewProjection);
                if (clip.W <= 0.1f)
                    continue;
                var screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewSize.X, (0.5f - clip.Y / clip.W * 0.5f) * viewSize.Y);
                float d = Vector2.DistanceSquared(screen, mouse);
                if (d < bestScore)
                {
                    bestScore = d;
                    best = point;
                }
            }
        }
        return best;
    }

    // The selected point over everything: a thicker post, its direction, and its radius when it has one.
    public static void DrawSelected(ColorRenderer lines, ScenarioPoint point, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        var color = new Vector4(1f, 0.95f, 0.35f, 1f);
        var p = point.Position;
        const float side = 0.12f;
        foreach (var offset in (ReadOnlySpan<Vector3>)[new(side, 0, 0), new(-side, 0, 0), new(0, side, 0), new(0, -side, 0)])
            lines.AddLine(p + offset, p + offset + new Vector3(0, 0, PostHeight), color);
        var arrowStart = p + new Vector3(0f, 0f, ArrowHeight);
        var tip = arrowStart + point.Direction * ArrowLength * 1.6f;
        lines.AddLine(arrowStart, tip, color);
        var back = -point.Direction * 0.3f;
        var across = new Vector3(-point.Direction.Y, point.Direction.X, 0f) * 0.2f;
        lines.AddLine(tip, tip + back + across, color);
        lines.AddLine(tip, tip + back - across, color);
        if (point.Radius > 0)
        {
            const int segments = 40;
            for (int i = 0; i < segments; i++)
            {
                float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
                var lift = new Vector3(0, 0, 0.05f);
                lines.AddLine(p + new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f) * point.Radius + lift,
                    p + new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f) * point.Radius + lift, color);
            }
        }
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    private void DisposeMeshes()
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i]?.Dispose();
            _meshes[i] = null;
            _counts[i] = 0;
        }
    }

    public void Dispose() => DisposeMeshes();
}
