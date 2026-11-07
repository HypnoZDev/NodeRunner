using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A small depth image drawn on the CPU from occluder triangles, to tell whether an object is fully behind them (the
// way the game skips what its occluders hide). Depth is stored as 1 / view distance (0 = nothing), so the nearest
// occluder keeps the highest value; coarser levels keep the farthest depth of the texels below.
public sealed class OcclusionBuffer
{
    public const int Width = 320;

    // Objects nearer than this are always drawn; occluders are cut there.
    private const float NearDepth = 0.5f;

    // Pixels on the edge shared by two triangles belong to both (rounding would leave a crack).
    private const float EdgeTolerance = 1e-3f;

    private int _width;
    private int _height;
    private float[][] _levels = [];
    private Matrix4x4 _viewProjection;
    private Vector3 _camera;
    private bool _ready;

    // Lanes let several threads draw at once, each into its own depth image, merged by End (lane 0 = level 0).
    private float[][] _laneDepth = [];
    private Vector4[][] _laneProjected = [];

    private static readonly int[] BoxIndices = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];

    // A box as a mesh for AddMesh: its 8 corners and 12 triangles turned outward.
    public static (Vector3[] Vertices, int[] Indices) BoxMesh(OccluderBox box)
    {
        var corners = box.Corners();
        var indices = (int[])BoxIndices.Clone();
        for (int t = 0; t < indices.Length; t += 3)
        {
            var (a, b, c) = (corners[indices[t]], corners[indices[t + 1]], corners[indices[t + 2]]);
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3 - box.Center) < 0f)
                (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        }
        return (corners, indices);
    }

    // Starts a view: the occluders are added next (from up to `lanes` threads, one lane each), then End.
    public void Begin(Matrix4x4 viewProjection, Vector3 camera, float aspectRatio, int lanes = 1)
    {
        _viewProjection = viewProjection;
        _camera = camera;
        _width = Width;
        _height = Math.Clamp((int)MathF.Round(Width / MathF.Max(aspectRatio, 0.1f)), 16, Width * 2);
        if (_levels.Length == 0 || _levels[0].Length != _width * _height)
            _levels = AllocateLevels(_width, _height);
        lanes = Math.Max(lanes, 1);
        if (_laneDepth.Length != lanes || _laneDepth[0] != _levels[0])
        {
            _laneDepth = new float[lanes][];
            _laneDepth[0] = _levels[0];
            for (int i = 1; i < lanes; i++)
                _laneDepth[i] = new float[_levels[0].Length];
            _laneProjected = [.. Enumerable.Range(0, lanes).Select(_ => new Vector4[256])];
        }
        foreach (var depth in _laneDepth)
            Array.Clear(depth);
        _ready = false;
    }

    // Whether an occluder inside this sphere can hide anything: in view and at least a few pixels wide.
    public bool IsWorthDrawing(Vector3 center, float radius, float minPixels = 2f)
    {
        var clip = Vector4.Transform(new Vector4(center, 1f), _viewProjection);
        if (clip.W + radius < NearDepth)
            return false;
        if (clip.W - radius <= NearDepth)
            return true; // Around the camera.
        // Off screen: the sphere's reach on screen is below its widest projection (radius scaled like the X or Y axis).
        float reach = 0f;
        for (int axis = 0; axis < 3; axis++)
        {
            var offset = axis == 0 ? new Vector3(radius, 0, 0) : axis == 1 ? new Vector3(0, radius, 0) : new Vector3(0, 0, radius);
            var moved = Vector4.Transform(new Vector4(center + offset, 1f), _viewProjection);
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(moved.X - clip.X), MathF.Abs(moved.Y - clip.Y)));
        }
        // The view distance shrinks by up to the radius toward the near side.
        reach *= clip.W / (clip.W - radius);
        if (clip.X - reach * 1.8f > clip.W || clip.X + reach * 1.8f < -clip.W
            || clip.Y - reach * 1.8f > clip.W || clip.Y + reach * 1.8f < -clip.W)
            return false;
        return reach / clip.W * 0.5f * _width >= minPixels;
    }

    // Draws a mesh, each vertex projected once. One-sided: triangles turned away from the camera are skipped (their
    // winding must face outward); two-sided for meshes with mixed windings.
    public void AddMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, bool twoSided, int lane = 0)
    {
        if (_laneProjected[lane].Length < vertices.Length)
            _laneProjected[lane] = new Vector4[Math.Max(vertices.Length, _laneProjected[lane].Length * 2)];
        var projected = _laneProjected[lane];
        for (int i = 0; i < vertices.Length; i++)
            projected[i] = Vector4.Transform(new Vector4(vertices[i], 1f), _viewProjection);

        Span<Vector4> polygon = stackalloc Vector4[3];
        Span<Vector4> clipped = stackalloc Vector4[4];
        var depth = _laneDepth[lane];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            if (!twoSided)
            {
                var a = vertices[ia];
                if (Vector3.Dot(Vector3.Cross(vertices[ib] - a, vertices[ic] - a), _camera - a) <= 0f)
                    continue;
            }
            polygon[0] = projected[ia];
            polygon[1] = projected[ib];
            polygon[2] = projected[ic];
            if (OutsideView(polygon))
                continue;
            if (polygon[0].W >= NearDepth && polygon[1].W >= NearDepth && polygon[2].W >= NearDepth)
            {
                Rasterize(depth, polygon[0], polygon[1], polygon[2]);
                continue;
            }
            int count = ClipNear(polygon, clipped);
            for (int i = 1; i + 1 < count; i++)
                Rasterize(depth, clipped[0], clipped[i], clipped[i + 1]);
        }
    }

    public void End()
    {
        var target = _levels[0];
        for (int lane = 1; lane < _laneDepth.Length; lane++)
        {
            var source = _laneDepth[lane];
            int i = 0;
            for (; i + Vector<float>.Count <= target.Length; i += Vector<float>.Count)
                Vector.Max(new Vector<float>(target, i), new Vector<float>(source, i)).CopyTo(target, i);
            for (; i < target.Length; i++)
                target[i] = MathF.Max(target[i], source[i]);
        }
        BuildLevels();
        _ready = true;
    }

    // Whether the sphere is behind the occluders everywhere it shows on screen.
    public bool IsHidden(Vector3 center, float radius)
    {
        if (!_ready)
            return false;
        var clipCenter = Vector4.Transform(new Vector4(center, 1f), _viewProjection);
        float nearest = clipCenter.W - radius;
        if (nearest <= NearDepth)
            return false;

        // Screen rectangle of the sphere's bounding box.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = center + new Vector3((i & 1) == 0 ? -radius : radius, (i & 2) == 0 ? -radius : radius, (i & 4) == 0 ? -radius : radius);
            var clip = Vector4.Transform(new Vector4(corner, 1f), _viewProjection);
            if (clip.W <= NearDepth)
                return false;
            var (x, y) = ToPixels(clip);
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        if (maxX < 0f || maxY < 0f || minX >= _width || minY >= _height)
            return false; // Off screen: the frustum test deals with it.

        int x0 = Math.Max((int)MathF.Floor(minX), 0);
        int y0 = Math.Max((int)MathF.Floor(minY), 0);
        int x1 = Math.Min((int)MathF.Floor(maxX), _width - 1);
        int y1 = Math.Min((int)MathF.Floor(maxY), _height - 1);

        // Coarsest level where the rectangle spans at most 4 texels across, then every texel it touches.
        int level = 0;
        while (level + 1 < _levels.Length && ((x1 >> level) - (x0 >> level) > 3 || (y1 >> level) - (y0 >> level) > 3))
            level++;
        int width = LevelSize(_width, level);
        var texels = _levels[level];
        float objectDepth = 1f / nearest;
        for (int y = y0 >> level; y <= y1 >> level; y++)
        {
            for (int x = x0 >> level; x <= x1 >> level; x++)
            {
                if (texels[y * width + x] <= objectDepth)
                    return false;
            }
        }
        return true;
    }

    private (float X, float Y) ToPixels(Vector4 clip) =>
        ((clip.X / clip.W * 0.5f + 0.5f) * _width, (0.5f - clip.Y / clip.W * 0.5f) * _height);

    // Whether all three points are past the same side of the view.
    private static bool OutsideView(ReadOnlySpan<Vector4> p)
    {
        int all = 31;
        foreach (var v in p)
        {
            int outside = (v.W < NearDepth ? 1 : 0) | (v.X > v.W ? 2 : 0) | (v.X < -v.W ? 4 : 0) | (v.Y > v.W ? 8 : 0) | (v.Y < -v.W ? 16 : 0);
            all &= outside;
        }
        return all != 0;
    }

    // Cuts the triangle at the near distance (Sutherland-Hodgman on one plane); returns the number of points.
    private static int ClipNear(ReadOnlySpan<Vector4> input, Span<Vector4> output)
    {
        int count = 0;
        for (int i = 0; i < input.Length; i++)
        {
            var a = input[i];
            var b = input[(i + 1) % input.Length];
            bool aIn = a.W >= NearDepth;
            bool bIn = b.W >= NearDepth;
            if (aIn)
                output[count++] = a;
            if (aIn != bIn)
                output[count++] = Vector4.Lerp(a, b, (NearDepth - a.W) / (b.W - a.W));
        }
        return count;
    }

    // Fills the pixels whose centre is inside the triangle, row by row: on each row the three edges bound one span,
    // and 1 / w goes up by a fixed step from pixel to pixel.
    private void Rasterize(float[] depth, Vector4 a, Vector4 b, Vector4 c)
    {
        var (ax, ay) = ToPixels(a);
        var (bx, by) = ToPixels(b);
        var (cx, cy) = ToPixels(c);
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-6f)
            return;
        float inverse = 1f / area;
        float da = 1f / a.W, db = 1f / b.W, dc = 1f / c.W;

        // Barycentric weights as linear functions of the pixel position: w = kx * px + ky * py + k.
        float k0x = (by - cy) * inverse, k0y = (cx - bx) * inverse, k0 = (bx * cy - by * cx) * inverse;
        float k1x = (cy - ay) * inverse, k1y = (ax - cx) * inverse, k1 = (cx * ay - cy * ax) * inverse;
        float k2x = -k0x - k1x, k2y = -k0y - k1y, k2 = 1f - k0 - k1;
        float stepX = k0x * da + k1x * db + k2x * dc;

        int y0 = Math.Max((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), 0);
        int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), _height - 1);
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            float m0 = k0y * py + k0, m1 = k1y * py + k1, m2 = k2y * py + k2;
            float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
            if (!Bound(k0x, m0, ref lo, ref hi) || !Bound(k1x, m1, ref lo, ref hi) || !Bound(k2x, m2, ref lo, ref hi))
                continue;
            int x0 = (int)MathF.Max(MathF.Ceiling(lo - 0.5f - EdgeTolerance), 0f);
            int x1 = (int)MathF.Min(MathF.Floor(hi - 0.5f + EdgeTolerance), _width - 1);
            if (x0 > x1)
                continue;
            float d = (m0 * da + m1 * db + m2 * dc) + stepX * (x0 + 0.5f);
            int row = y * _width;
            for (int x = x0; x <= x1; x++, d += stepX)
            {
                if (d > depth[row + x])
                    depth[row + x] = d;
            }
        }
    }

    // Narrows [lo, hi] to where k * px + m >= 0; false when no pixel of the row qualifies.
    private static bool Bound(float k, float m, ref float lo, ref float hi)
    {
        if (k > 1e-9f)
            lo = MathF.Max(lo, -m / k);
        else if (k < -1e-9f)
            hi = MathF.Min(hi, -m / k);
        else if (m < 0f)
            return false;
        return lo <= hi + 2 * EdgeTolerance;
    }

    private static int LevelSize(int size, int level) => Math.Max((size + (1 << level) - 1) >> level, 1);

    private static float[][] AllocateLevels(int width, int height)
    {
        var levels = new List<float[]>();
        for (int level = 0; ; level++)
        {
            int w = LevelSize(width, level), h = LevelSize(height, level);
            levels.Add(new float[w * h]);
            if (w == 1 && h == 1)
                break;
        }
        return [.. levels];
    }

    // Each coarser texel keeps the farthest depth of the four below it.
    private void BuildLevels()
    {
        for (int level = 1; level < _levels.Length; level++)
        {
            var below = _levels[level - 1];
            var texels = _levels[level];
            int belowWidth = LevelSize(_width, level - 1), belowHeight = LevelSize(_height, level - 1);
            int width = LevelSize(_width, level), height = LevelSize(_height, level);
            for (int y = 0; y < height; y++)
            {
                int by0 = y * 2, by1 = Math.Min(by0 + 1, belowHeight - 1);
                for (int x = 0; x < width; x++)
                {
                    int bx0 = x * 2, bx1 = Math.Min(bx0 + 1, belowWidth - 1);
                    texels[y * width + x] = MathF.Min(
                        MathF.Min(below[by0 * belowWidth + bx0], below[by0 * belowWidth + bx1]),
                        MathF.Min(below[by1 * belowWidth + bx0], below[by1 * belowWidth + bx1]));
                }
            }
        }
    }
}
