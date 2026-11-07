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
        if (scenarios == _built)
            return;

        _built = scenarios;
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
