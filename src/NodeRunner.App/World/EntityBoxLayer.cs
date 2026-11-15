using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Groups of entities shown as boxes, by level of detail.
internal enum EntityBoxCategory
{
    Hd,
    OrphanHd,
    Lod,
    Slod,
    Interior,
}

// A box to draw: the model's bounds in model space and the object's transform; Missing = model unknown (a cross at
// Position is drawn instead).
internal readonly record struct EntityBox(EntityBoxCategory Category, Vector3 Min, Vector3 Max, Matrix4x4 Transform, bool Missing);

// Draws every placed object near the camera as the bounding box of its archetype, turned and scaled like the object.
internal sealed class EntityBoxLayer : IDisposable
{
    public const float MinRadius = 100f;
    public const float MaxRadius = 1000f;

    private const float RebuildDistance = 40f;
    private const float MissingArchetypeSize = 0.5f;

    public static readonly IReadOnlyList<EntityBoxCategory> Categories = Enum.GetValues<EntityBoxCategory>();

    private readonly ColorMesh?[] _meshes = new ColorMesh?[Categories.Count];
    private readonly int[] _counts = new int[Categories.Count];
    private object? _source;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private float _builtRadius;
    private int _builtStructure = -1; // Objects deleted or brought back since (MapLibrary.StructureVersion).

    public static string Label(EntityBoxCategory category) => category switch
    {
        EntityBoxCategory.Hd => L("Détail (HD)"),
        EntityBoxCategory.OrphanHd => L("Détail sans LOD (props...)"),
        EntityBoxCategory.Lod => L("LOD (vue moyenne)"),
        EntityBoxCategory.Slod => L("SLOD (vue lointaine)"),
        EntityBoxCategory.Interior => L("Intérieurs (MLO)"),
        _ => category.ToString(),
    };

    public static Vector4 Color(EntityBoxCategory category) => category switch
    {
        EntityBoxCategory.Hd => new(0.92f, 0.92f, 0.92f, 0.85f),
        EntityBoxCategory.OrphanHd => new(0.55f, 0.85f, 1f, 0.85f),
        EntityBoxCategory.Lod => new(1f, 0.75f, 0.3f, 0.85f),
        EntityBoxCategory.Slod => new(1f, 0.4f, 0.4f, 0.85f),
        EntityBoxCategory.Interior => new(0.8f, 0.5f, 1f, 0.9f),
        _ => Vector4.One,
    };

    public static string LayerName(EntityBoxCategory category) => "Entity." + category;

    public int Count(EntityBoxCategory category) => _counts[(int)category];

    public int MissingArchetypeCount { get; private set; }

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, float radius, bool enabled) =>
        Update(loader.Maps, loader.Maps is { } maps ? (center, r) => LibraryBoxes(maps, center, r) : null, loader.Maps?.StructureVersion ?? 0,
            renderer, cameraPosition, radius, enabled);

    // Boxes of another map (GTA IV): source identifies the map, boxes lists the objects around a point.
    public void Update(object? source, Func<Vector2, float, IEnumerable<EntityBox>>? boxes, int structure, ColorRenderer renderer,
        Vector3 cameraPosition, float radius, bool enabled)
    {
        if (source != _source)
        {
            _source = source;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add(L("Cadres des objets"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN); // Computed again at the next frame.
            }
            else if (finished.Result.Source == _source)
            {
                Apply(finished.Result, renderer);
            }
        }

        if (_source is null || boxes is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        bool moved = float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance;
        if (moved || MathF.Abs(radius - _builtRadius) > 1f || structure != _builtStructure)
        {
            var built = _source;
            _builtStructure = structure;
            _builtCenter = center;
            _builtRadius = radius;
            _building = Task.Run(() => Build(built, boxes(center, radius)));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<EntityBoxCategory, bool> isVisible)
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            if (_meshes[i] is { } mesh && isVisible((EntityBoxCategory)i))
                renderer.Draw(mesh, viewProjection, cameraPosition, _builtRadius * 1.2f);
        }
    }

    private sealed record BuildResult(object Source, LineBatch[] Batches, int[] Counts, int Missing);

    private static BuildResult Build(object source, IEnumerable<EntityBox> boxes)
    {
        var batches = Categories.Select(_ => new LineBatch()).ToArray();
        var counts = new int[Categories.Count];
        int missing = 0;

        foreach (var box in boxes)
        {
            var batch = batches[(int)box.Category];
            counts[(int)box.Category]++;
            if (box.Missing)
            {
                missing++;
                AddCross(batch, box.Transform.Translation, new Vector4(1f, 0.2f, 1f, 1f));
                continue;
            }
            batch.AddBox(box.Min, box.Max, box.Transform, Color(box.Category));
        }

        return new BuildResult(source, batches, counts, missing);
    }

    private static IEnumerable<EntityBox> LibraryBoxes(MapLibrary library, Vector2 center, float radius)
    {
        foreach (var entity in library.EntitiesAround(center, radius))
        {
            var archetype = library.GetArchetype(entity.ArchetypeHash);
            yield return archetype is null
                ? new EntityBox(Classify(entity), default, default, Matrix4x4.CreateTranslation(entity.Position), true)
                : new EntityBox(Classify(entity), archetype.BoundsMin, archetype.BoundsMax, entity.Transform, false);
        }
    }

    private static EntityBoxCategory Classify(MapEntity entity) =>
        entity.IsInterior ? EntityBoxCategory.Interior
        : entity.LodLevel switch
        {
            LodLevel.Hd => EntityBoxCategory.Hd,
            LodLevel.OrphanHd => EntityBoxCategory.OrphanHd,
            LodLevel.Lod => EntityBoxCategory.Lod,
            _ => EntityBoxCategory.Slod,
        };

    private static void AddCross(LineBatch batch, Vector3 p, Vector4 color)
    {
        const float s = MissingArchetypeSize;
        batch.AddLine(p - new Vector3(s, 0, 0), p + new Vector3(s, 0, 0), color);
        batch.AddLine(p - new Vector3(0, s, 0), p + new Vector3(0, s, 0), color);
        batch.AddLine(p - new Vector3(0, 0, s), p + new Vector3(0, 0, s), color);
    }

    private void Apply(BuildResult result, ColorRenderer renderer)
    {
        DisposeMeshes();
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i] = renderer.CreateMesh(result.Batches[i]);
            _counts[i] = result.Counts[i];
        }
        MissingArchetypeCount = result.Missing;
    }

    private void DisposeMeshes()
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i]?.Dispose();
            _meshes[i] = null;
            _counts[i] = 0;
        }
        MissingArchetypeCount = 0;
    }

    public void Dispose() => DisposeMeshes();
}
