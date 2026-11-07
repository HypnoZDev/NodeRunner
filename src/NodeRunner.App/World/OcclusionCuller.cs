using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Hides what the game would hide behind its occluders: the boxes and meshes near the camera are drawn into an
// OcclusionBuffer each frame, then the objects fully behind them are skipped.
internal sealed class OcclusionCuller
{
    public const float Radius = 1000f;

    private const float RebuildDistance = 60f;

    // A box or mesh, with a sphere around it. Boxes are one-sided (outward triangles); meshes are two-sided, their
    // windings are mixed.
    private sealed record Occluder(Vector3 Center, float Radius, Vector3[] Vertices, int[] Indices, bool TwoSided);

    private MapLibrary? _library;
    private Task<Occluder[]>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private int _builtRevision = -1;
    private Occluder[] _occluders = [];

    private readonly OcclusionBuffer _buffer = new();
    private readonly List<Occluder> _inView = [];
    private Occluder[]? _preparedOccluders;
    private Matrix4x4 _preparedView;

    // Threads drawing the occluders: the big meshes of a city block cost a few milliseconds on one core.
    private static readonly int Lanes = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public int HiddenCount { get; private set; }

    public void Update(WorldLoader loader, Vector3 cameraPosition, bool enabled)
    {
        if (loader.Maps != _library)
        {
            _library = loader.Maps;
            _builtCenter = new Vector2(float.NaN);
            _occluders = [];
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (finished.IsCompletedSuccessfully)
            {
                _occluders = finished.Result;
            }
            else
            {
                Diagnostics.ErrorLog.Add(L("Aperçu de l'occlusion"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN);
            }
        }

        if (_library is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || OccluderMaps.Revision != _builtRevision)
        {
            _builtCenter = center;
            _builtRevision = OccluderMaps.Revision;
            _building = Task.Run(() => Build(OccluderMaps.Get(loader), center));
        }
    }

    // Draws the occluders seen from the camera; until the next call, IsHidden answers for this view.
    public void Prepare(Matrix4x4 viewProjection, Vector3 camera, float aspectRatio)
    {
        HiddenCount = 0;
        // Same view and occluders as the last frame: the image is still right.
        if (_preparedOccluders == _occluders && _preparedView == viewProjection)
            return;
        _preparedOccluders = _occluders;
        _preparedView = viewProjection;

        _buffer.Begin(viewProjection, camera, aspectRatio, Lanes);
        _inView.Clear();
        foreach (var occluder in _occluders)
        {
            if (_buffer.IsWorthDrawing(occluder.Center, occluder.Radius))
                _inView.Add(occluder);
        }
        // Every lane takes every n-th occluder, so the big near ones are shared out.
        if (Lanes == 1 || _inView.Count < 64)
        {
            foreach (var occluder in _inView)
                _buffer.AddMesh(occluder.Vertices, occluder.Indices, occluder.TwoSided);
        }
        else
        {
            Parallel.For(0, Lanes, lane =>
            {
                for (int i = lane; i < _inView.Count; i += Lanes)
                    _buffer.AddMesh(_inView[i].Vertices, _inView[i].Indices, _inView[i].TwoSided, lane);
            });
        }
        _buffer.End();
    }

    // Whether the sphere is behind the occluders everywhere it shows on screen.
    public bool IsHidden(Vector3 center, float radius)
    {
        if (_occluders.Length == 0 || !_buffer.IsHidden(center, radius))
            return false;
        HiddenCount++;
        return true;
    }

    private static Occluder[] Build(IReadOnlyList<OccluderMapFile> files, Vector2 center)
    {
        var occluders = new List<Occluder>();
        foreach (var file in files)
        {
            // By index: a box may be edited meanwhile.
            for (int b = 0; b < file.Boxes.Count; b++)
            {
                var box = file.Boxes[b];
                if (file.DeletedBoxes.Contains(b))
                    continue;
                float reach = Radius + 0.5f * MathF.Max(box.Size.X, box.Size.Y);
                if (Vector2.DistanceSquared(new Vector2(box.Center.X, box.Center.Y), center) > reach * reach)
                    continue;
                var (vertices, indices) = OcclusionBuffer.BoxMesh(box);
                occluders.Add(new Occluder(box.Center, box.Size.Length() / 2, vertices, indices, TwoSided: false));
            }

            foreach (var model in file.Occluders.Models)
            {
                if (file.DeletedModels.Contains(model.Index) || model.Vertices.Count == 0)
                    continue;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var v in model.Vertices)
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
                var closest = Vector2.Clamp(center, new Vector2(min.X, min.Y), new Vector2(max.X, max.Y));
                if (Vector2.DistanceSquared(closest, center) > Radius * Radius)
                    continue;
                occluders.Add(new Occluder((min + max) / 2, (max - min).Length() / 2, [.. model.Vertices],
                    [.. model.Indices.Select(i => (int)i)], TwoSided: true));
            }
        }
        return [.. occluders];
    }
}
