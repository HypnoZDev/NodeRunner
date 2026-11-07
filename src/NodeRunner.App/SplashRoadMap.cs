using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using NodeRunner.Data.Paths;

namespace NodeRunner.App;

// The road network of the start-up screen, drawn from the player's own path nodes (vehicle roads only), built on a
// worker thread: the roads, a blurred copy for the neon glow, and a few long drives along the roads for the moving
// lights. Bitmaps are in pixels, everything else in design units.
internal sealed class SplashRoadMap : IDisposable
{
    private static readonly Color Road = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color Highway = Color.FromArgb(0x5F, 0xD3, 0xFF);

    public required Bitmap Roads { get; init; }
    public required Bitmap Glow { get; init; }
    public required RectangleF Bounds { get; init; }
    public required PointF Origin { get; init; }
    public required float Radius { get; init; }
    public required SplashRunner[] Runners { get; init; }
    public required int NodeCount { get; init; }
    public required double Kilometres { get; init; }

    public static SplashRoadMap? Build(PathNetwork network, RectangleF area, float scale)
    {
        var index = new Dictionary<PathNodeId, int>();
        var positions = new List<Vector2>();
        var highway = new List<bool>();
        foreach (var a in network.Areas)
        {
            foreach (var node in a.Nodes)
            {
                if (node.IsPedestrian)
                    continue;
                index[node.Id] = positions.Count;
                positions.Add(new Vector2(node.Position.X, node.Position.Y));
                highway.Add(node.IsHighway);
            }
        }
        if (positions.Count < 100)
            return null;

        var seen = new HashSet<(int, int)>();
        var segments = new List<(int A, int B, bool Major)>();
        var neighbours = new List<int>[positions.Count];

        foreach (var a in network.Areas)
        {
            foreach (var node in a.Nodes)
            {
                if (!index.TryGetValue(node.Id, out int from))
                    continue;
                foreach (var link in a.LinksOf(node))
                {
                    if (link.IsShortcut || !index.TryGetValue(link.Target, out int to) || to == from)
                        continue;
                    if (!seen.Add((Math.Min(from, to), Math.Max(from, to))))
                        continue;
                    bool major = highway[from] || highway[to] || link.LanesForward + link.LanesBackward >= 4;
                    segments.Add((from, to, major));
                    (neighbours[from] ??= []).Add(to);
                    (neighbours[to] ??= []).Add(from);
                }
            }
        }

        // Only the main network (the largest group of linked roads): far-away separate maps would shrink it.
        var main = MainNetwork(neighbours);
        segments.RemoveAll(x => !main[x.A]);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        int nodeCount = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            if (!main[i])
                continue;
            nodeCount++;
            var p = positions[i];
            (minX, maxX, minY, maxY) = (Math.Min(minX, p.X), Math.Max(maxX, p.X), Math.Min(minY, p.Y), Math.Max(maxY, p.Y));
        }
        if (nodeCount < 100 || maxX - minX < 1f || maxY - minY < 1f)
            return null;
        double metres = segments.Sum(x => (double)Vector2.Distance(positions[x.A], positions[x.B]));
        float s = Math.Min(area.Width / (maxX - minX), area.Height / (maxY - minY));
        float width = (maxX - minX) * s, height = (maxY - minY) * s;
        var bounds = new RectangleF(area.X + (area.Width - width) / 2f, area.Y + (area.Height - height) / 2f, width, height);
        PointF ToDesign(Vector2 p) => new(bounds.X + (p.X - minX) * s, bounds.Bottom - (p.Y - minY) * s);

        // The light starts from the busiest part of the map (the city centre).
        const int Cells = 24;
        var counts = new int[Cells * Cells];
        foreach (var p in positions.Where((_, i) => main[i]))
        {
            int cx = (int)((p.X - minX) / (maxX - minX) * Cells), cy = (int)((p.Y - minY) / (maxY - minY) * Cells);
            if (cx is >= 0 and < Cells && cy is >= 0 and < Cells)
                counts[cy * Cells + cx]++;
        }
        int best = Array.IndexOf(counts, counts.Max());
        var origin = ToDesign(new Vector2(minX + (best % Cells + 0.5f) / Cells * (maxX - minX), minY + (best / Cells + 0.5f) / Cells * (maxY - minY)));
        float radius = new[] { bounds.Location, new PointF(bounds.Right, bounds.Top), new PointF(bounds.Left, bounds.Bottom), new PointF(bounds.Right, bounds.Bottom) }
            .Max(c => MathF.Sqrt((c.X - origin.X) * (c.X - origin.X) + (c.Y - origin.Y) * (c.Y - origin.Y)));

        var roads = new Bitmap(Math.Max(1, (int)MathF.Ceiling(width * scale)), Math.Max(1, (int)MathF.Ceiling(height * scale)), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(roads))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = s * scale;
            PointF ToPixel(Vector2 p) => new((p.X - minX) * k, (maxY - p.Y) * k);
            using var minor = new Pen(Color.FromArgb(70, Road), 0.6f * scale);
            using var major = new Pen(Color.FromArgb(235, Highway), 1.1f * scale);
            foreach (var (a, b, _) in segments.Where(x => !x.Major))
                g.DrawLine(minor, ToPixel(positions[a]), ToPixel(positions[b]));
            foreach (var (a, b, _) in segments.Where(x => x.Major))
                g.DrawLine(major, ToPixel(positions[a]), ToPixel(positions[b]));
        }
        var glow = Blurred(roads, Math.Max(1, (int)(2f * scale)), 0.75f);

        var runners = Drives(positions, highway, neighbours, main, ToDesign);
        return new SplashRoadMap
        {
            Roads = roads,
            Glow = glow,
            Bounds = bounds,
            Origin = origin,
            Radius = radius,
            Runners = runners,
            NodeCount = nodeCount,
            Kilometres = metres / 1000.0,
        };
    }

    // Long drives along the network: from a random road (motorways first), always taking the straightest way on, never
    // twice through the same node.
    private static bool[] MainNetwork(List<int>[] neighbours)
    {
        var group = new int[neighbours.Length];
        var sizes = new List<int> { 0 };
        var queue = new Queue<int>();
        for (int start = 0; start < neighbours.Length; start++)
        {
            if (group[start] != 0 || neighbours[start] is null)
                continue;
            int id = sizes.Count, size = 0;
            group[start] = id;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int node))
            {
                size++;
                foreach (int next in neighbours[node])
                {
                    if (group[next] == 0)
                    {
                        group[next] = id;
                        queue.Enqueue(next);
                    }
                }
            }
            sizes.Add(size);
        }
        int largest = sizes.IndexOf(sizes.Max());
        return group.Select(g => g == largest && largest > 0).ToArray();
    }

    private static SplashRunner[] Drives(List<Vector2> positions, List<bool> highway, List<int>[] neighbours, bool[] main, Func<Vector2, PointF> toDesign)
    {
        var random = new Random();
        var starts = Enumerable.Range(0, positions.Count).Where(i => highway[i] && main[i]).ToArray();
        if (starts.Length == 0)
            starts = Enumerable.Range(0, positions.Count).Where(i => main[i]).ToArray();
        var runners = new List<SplashRunner>();
        for (int attempt = 0; attempt < 80 && runners.Count < 9 && starts.Length > 0; attempt++)
        {
            int current = starts[random.Next(starts.Length)], previous = -1;
            var visited = new HashSet<int> { current };
            var points = new List<PointF> { toDesign(positions[current]) };
            for (int hop = 0; hop < 4000; hop++)
            {
                var options = neighbours[current]?.Where(n => n != previous && !visited.Contains(n)).ToList();
                if (options is null || options.Count == 0)
                    break;
                int next = options[0];
                if (previous >= 0 && options.Count > 1 && random.NextDouble() > 0.12)
                {
                    var heading = Vector2.Normalize(positions[current] - positions[previous]);
                    next = options.MaxBy(n => Vector2.Dot(heading, Vector2.Normalize(positions[n] - positions[current] + new Vector2(1e-4f))));
                }
                else if (options.Count > 1)
                    next = options[random.Next(options.Count)];
                previous = current;
                current = next;
                visited.Add(current);
                var p = toDesign(positions[current]);
                var last = points[^1];
                if ((p.X - last.X) * (p.X - last.X) + (p.Y - last.Y) * (p.Y - last.Y) > 2f)
                    points.Add(p);
            }
            var runner = new SplashRunner(points.ToArray(), 55f + (float)random.NextDouble() * 45f, (float)random.NextDouble() * 50f);
            if (runner.Length > 90f)
                runners.Add(runner);
        }
        return runners.ToArray();
    }

    // Box blur (two passes per direction), brightened: the neon halo around the roads.
    private static Bitmap Blurred(Bitmap source, int radius, float gain)
    {
        var bitmap = source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            int w = bitmap.Width, h = bitmap.Height, stride = data.Stride;
            var pixels = new byte[stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var line = new int[Math.Max(w, h) * 4];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < h; y++)
                    BoxLine(pixels, y * stride, 4, w, radius, line);
                for (int x = 0; x < w; x++)
                    BoxLine(pixels, x * 4, stride, h, radius, line);
            }
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = Math.Min(255, (int)(pixels[i + 3] * gain));
                pixels[i + 3] = (byte)alpha;
                for (int c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(alpha, (int)(pixels[i + c] * gain));
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static void BoxLine(byte[] pixels, int start, int step, int count, int radius, int[] line)
    {
        for (int i = 0; i < count; i++)
            for (int c = 0; c < 4; c++)
                line[i * 4 + c] = pixels[start + i * step + c];
        int size = radius * 2 + 1;
        Span<int> sum = stackalloc int[4];
        for (int i = -radius; i <= radius; i++)
            for (int c = 0; c < 4; c++)
                sum[c] += i >= 0 && i < count ? line[i * 4 + c] : 0;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < 4; c++)
                pixels[start + i * step + c] = (byte)(sum[c] / size);
            int add = i + radius + 1, remove = i - radius;
            for (int c = 0; c < 4; c++)
                sum[c] += (add < count ? line[add * 4 + c] : 0) - (remove >= 0 ? line[remove * 4 + c] : 0);
        }
    }

    public void Dispose()
    {
        Roads.Dispose();
        Glow.Dispose();
    }
}

// One light driving along a road, in design units; it comes back after a short pause at the end of its drive.
internal sealed class SplashRunner
{
    private readonly PointF[] _points;
    private readonly float[] _distances;

    public SplashRunner(PointF[] points, float speed, float offset)
    {
        _points = points;
        _distances = new float[points.Length];
        for (int i = 1; i < points.Length; i++)
        {
            float dx = points[i].X - points[i - 1].X, dy = points[i].Y - points[i - 1].Y;
            _distances[i] = _distances[i - 1] + MathF.Sqrt(dx * dx + dy * dy);
        }
        Speed = speed;
        Offset = offset;
    }

    public float Length => _distances[^1];
    public float Speed { get; }
    public float Offset { get; }

    public PointF At(float distance)
    {
        distance = Math.Clamp(distance, 0f, Length);
        int i = Array.BinarySearch(_distances, distance);
        if (i >= 0)
            return _points[i];
        i = Math.Clamp(~i, 1, _points.Length - 1);
        float span = _distances[i] - _distances[i - 1];
        float f = span > 0f ? (distance - _distances[i - 1]) / span : 0f;
        return new PointF(_points[i - 1].X + (_points[i].X - _points[i - 1].X) * f, _points[i - 1].Y + (_points[i].Y - _points[i - 1].Y) * f);
    }
}
