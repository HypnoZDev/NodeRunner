using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

public enum OccluderFitSource
{
    // No usable collision there: the model's box, a little smaller.
    Model,

    // Inside the walls of the collision: boxes per closed building (a model often holds a car park, or several
    // buildings), stacked when a tower stands on a lower part.
    Walls,

    // Walls found, but none closing a space big enough (open structure, fences only): no box.
    NoClosedSpace,
}

public sealed record OccluderFitResult(OccluderFitSource Source, IReadOnlyList<OccluderBox> Boxes);

// Box occluders fitted to an object. From above, its collision is drawn on a grid lined up with most of its walls; a
// cell is inside a building when it sees a wall in almost every direction (windows and doors are gaps in the
// collision). Each building gets the largest rectangle that fits inside it, up to its lowest roof there; a higher part
// (a tower) gets its own box on top.
public static class OccluderFit
{
    // Taken off each side of a model's box, and off the top and bottom of a box fitted to walls: an occluder must stay
    // inside the walls, or it hides what is behind them through windows.
    public const float Inset = 0.3f;

    // Walls must reach this high above the bottom of the model's box (kerbs, low walls and fences do not count).
    public const float MinWallTop = 2.5f;

    public const float MinSide = 2f;
    public const float MinFloorArea = 12f;
    public const int MaxBoxesPerObject = 8;

    // Out of the 8 directions (axes and diagonals), a cell inside a building sees a wall in at least this many.
    public const int MinWallDirections = 7;

    // A part at least this much higher than the box under it gets a box of its own, up to MaxLevels boxes stacked.
    public const float MinLevelRise = 3f;
    public const int MaxLevels = 3;

    private const float VerticalNormal = 0.3f;
    private const float FlatNormal = 0.8f;
    private const float PreferredCell = 0.5f;
    private const int MaxCells = 400;

    private const float MinRoofHeight = 2f;
    private const float FootShare = 0.1f;

    // The top of a box: the roof height that only this share of its cells is under (antennas and gaps ignored).
    private const float RoofShare = 0.05f;

    // keepOut: world boxes no occluder may cover (interiors placed there: the game must keep drawing what is in them).
    public static OccluderFitResult? Fit(MapEntity entity, Archetype? archetype, IReadOnlyList<PlacedCollision>? collisions = null,
        IReadOnlyList<(Vector3 Min, Vector3 Max)>? keepOut = null)
    {
        if (archetype is null || archetype.BoundsMin == archetype.BoundsMax)
            return null;
        var frame = new Frame(entity, archetype, null);
        var (walls, flats) = collisions is { Count: > 0 } ? Surfaces(collisions, frame) : ([], []);
        if (walls.Count == 0)
            return ModelBox(frame) is { } box ? new OccluderFitResult(OccluderFitSource.Model, [box]) : null;
        // Lined up with the walls: a building is not always turned like its object.
        var wallFrame = new Frame(entity, archetype, WallAngle(walls, frame.Angle));
        for (int i = 0; i < walls.Count; i++)
            walls[i] = wallFrame.ToFrame(walls[i]);
        for (int i = 0; i < flats.Count; i++)
            flats[i] = wallFrame.ToFrame(flats[i]);
        var boxes = new Grid(wallFrame, walls, flats).Boxes(keepOut ?? []);
        return new OccluderFitResult(boxes.Count > 0 ? OccluderFitSource.Walls : OccluderFitSource.NoClosedSpace, boxes);
    }

    // The model's box in a frame turned around Z, by default like the object.
    private sealed class Frame
    {
        public readonly float Angle, Cos, Sin;
        public readonly Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);

        public Frame(MapEntity entity, Archetype archetype, float? angle)
        {
            var transform = entity.Transform;
            var xAxis = Vector3.TransformNormal(Vector3.UnitX, transform);
            Angle = angle ?? MathF.Atan2(xAxis.Y, xAxis.X);
            Cos = MathF.Cos(Angle);
            Sin = MathF.Sin(Angle);
            for (int k = 0; k < 8; k++)
            {
                var local = new Vector3(
                    (k & 1) != 0 ? archetype.BoundsMax.X : archetype.BoundsMin.X,
                    (k & 2) != 0 ? archetype.BoundsMax.Y : archetype.BoundsMin.Y,
                    (k & 4) != 0 ? archetype.BoundsMax.Z : archetype.BoundsMin.Z);
                var p = ToFrame(Vector3.Transform(local, transform));
                Min = Vector3.Min(Min, p);
                Max = Vector3.Max(Max, p);
            }
        }

        public Vector3 ToFrame(Vector3 w) => new(w.X * Cos + w.Y * Sin, -w.X * Sin + w.Y * Cos, w.Z);

        public Vector2 ToWorld(Vector2 f) => new(f.X * Cos - f.Y * Sin, f.X * Sin + f.Y * Cos);

        public OccluderBox? Box(Vector3 min, Vector3 max)
        {
            var size = max - min;
            if (size.Z < 1f || size.X < 0.5f || size.Y < 0.5f)
                return null;
            var c = (min + max) / 2;
            var center = new Vector3(c.X * Cos - c.Y * Sin, c.X * Sin + c.Y * Cos, c.Z);
            return new OccluderBox(0, center, size, Angle).Quantized();
        }
    }

    private static OccluderBox? ModelBox(Frame frame) =>
        frame.Box(frame.Min + new Vector3(Inset), frame.Max - new Vector3(Inset));

    // Triangles inside the model's box, in world positions (three points each): near-vertical ones reaching
    // MinWallTop (walls), and flat ones (floors, roofs).
    private static (List<Vector3> Walls, List<Vector3> Flats) Surfaces(IReadOnlyList<PlacedCollision> collisions, Frame frame)
    {
        var limitMin = frame.Min - new Vector3(CollisionCut.Margin);
        var limitMax = frame.Max + new Vector3(CollisionCut.Margin);
        var walls = new List<Vector3>();
        var flats = new List<Vector3>();
        var v = new Vector3[3];
        var w = new Vector3[3];
        foreach (var (mesh, meshToWorld) in collisions)
        {
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                bool inside = true;
                for (int k = 0; k < 3 && inside; k++)
                {
                    w[k] = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + k]], meshToWorld);
                    v[k] = frame.ToFrame(w[k]);
                    inside = v[k].X >= limitMin.X && v[k].Y >= limitMin.Y && v[k].Z >= limitMin.Z
                             && v[k].X <= limitMax.X && v[k].Y <= limitMax.Y && v[k].Z <= limitMax.Z;
                }
                if (!inside)
                    continue;
                var cross = Vector3.Cross(v[1] - v[0], v[2] - v[0]);
                float length = cross.Length();
                if (length < 1e-4f)
                    continue;
                float up = MathF.Abs(cross.Z / length);
                List<Vector3> list;
                if (up >= FlatNormal)
                    list = flats;
                else if (up < VerticalNormal && MathF.Max(v[0].Z, MathF.Max(v[1].Z, v[2].Z)) >= frame.Min.Z + MinWallTop)
                    list = walls;
                else
                    continue;
                list.Add(w[0]);
                list.Add(w[1]);
                list.Add(w[2]);
            }
        }
        return (walls, flats);
    }

    // The direction most of the wall area faces (modulo a quarter turn), the closest to the object's own angle.
    private static float WallAngle(List<Vector3> walls, float objectAngle)
    {
        const int Bins = 90;
        var histogram = new float[Bins];
        for (int i = 0; i < walls.Count; i += 3)
        {
            var cross = Vector3.Cross(walls[i + 1] - walls[i], walls[i + 2] - walls[i]);
            float degrees = MathF.Atan2(cross.Y, cross.X) * 180 / MathF.PI;
            int bin = (int)MathF.Floor(((degrees % 90) + 90) % 90) % Bins;
            histogram[bin] += new Vector2(cross.X, cross.Y).Length();
        }
        int best = 0;
        float bestWeight = -1;
        for (int b = 0; b < Bins; b++)
        {
            float weight = 0;
            for (int k = -2; k <= 2; k++)
                weight += histogram[(b + k + Bins) % Bins];
            if (weight > bestWeight)
            {
                bestWeight = weight;
                best = b;
            }
        }
        // Refined to the mean of the bins around the peak.
        float sum = 0, total = 0;
        for (int k = -2; k <= 2; k++)
        {
            float weight = histogram[(best + k + Bins) % Bins];
            sum += (best + k + 0.5f) * weight;
            total += weight;
        }
        float angle = (total > 0 ? sum / total : best + 0.5f) * MathF.PI / 180;
        float quarter = MathF.PI / 2;
        return angle + MathF.Round((objectAngle - angle) / quarter) * quarter;
    }

    // The collision seen from above, in the frame.
    private sealed class Grid
    {
        private readonly Frame _frame;
        private readonly float _cell;
        private readonly Vector2 _origin;
        private readonly int _width, _height;

        // Per cell: walls there and the lowest point of their triangles; highest flat surface (NaN without any).
        private readonly bool[] _wall;
        private readonly float[] _wallBottom, _roof;

        public Grid(Frame frame, List<Vector3> walls, List<Vector3> flats)
        {
            _frame = frame;
            float extent = MathF.Max(frame.Max.X - frame.Min.X, frame.Max.Y - frame.Min.Y) + 2 * CollisionCut.Margin;
            _cell = MathF.Max(PreferredCell, extent / MaxCells);
            _origin = new Vector2(frame.Min.X - CollisionCut.Margin - _cell, frame.Min.Y - CollisionCut.Margin - _cell);
            _width = (int)MathF.Ceiling((frame.Max.X - frame.Min.X + 2 * CollisionCut.Margin) / _cell) + 2;
            _height = (int)MathF.Ceiling((frame.Max.Y - frame.Min.Y + 2 * CollisionCut.Margin) / _cell) + 2;
            _wall = new bool[_width * _height];
            _wallBottom = new float[_width * _height];
            _roof = new float[_width * _height];
            Array.Fill(_roof, float.NaN);
            for (int i = 0; i < walls.Count; i += 3)
            {
                float z = MathF.Min(walls[i].Z, MathF.Min(walls[i + 1].Z, walls[i + 2].Z));
                Rasterize(walls[i], walls[i + 1], walls[i + 2], index =>
                {
                    _wallBottom[index] = _wall[index] ? MathF.Min(_wallBottom[index], z) : z;
                    _wall[index] = true;
                });
            }
            for (int i = 0; i < flats.Count; i += 3)
            {
                float z = (flats[i].Z + flats[i + 1].Z + flats[i + 2].Z) / 3;
                Rasterize(flats[i], flats[i + 1], flats[i + 2], index =>
                    _roof[index] = float.IsNaN(_roof[index]) ? z : MathF.Max(_roof[index], z));
            }
        }

        public List<OccluderBox> Boxes(IReadOnlyList<(Vector3 Min, Vector3 Max)> keepOut)
        {
            // Inside: walls seen in enough directions (inner walls are inside too), single missing cells filled.
            var seen = WallDirections();
            var inside = new bool[_wall.Length];
            for (int i = 0; i < inside.Length; i++)
                inside[i] = seen[i] >= MinWallDirections;
            inside = Invert(Dilate(Invert(Dilate(inside, 1)), 1));
            foreach (var (min, max) in keepOut)
            {
                if (max.Z < _frame.Min.Z || min.Z > _frame.Max.Z)
                    continue;
                for (int i = 0; i < inside.Length; i++)
                {
                    var p = _frame.ToWorld(_origin + new Vector2(i % _width + 0.5f, i / _width + 0.5f) * _cell);
                    // A cell of margin: the grid is turned, the box is not.
                    if (p.X >= min.X - _cell && p.Y >= min.Y - _cell && p.X <= max.X + _cell && p.Y <= max.Y + _cell)
                        inside[i] = false;
                }
            }

            var label = new bool[_wall.Length];
            var spaces = new List<List<int>>();
            for (int i = 0; i < label.Length; i++)
            {
                if (inside[i] && !label[i])
                    spaces.Add(Fill(i, inside, label));
            }
            spaces.Sort((a, b) => b.Count.CompareTo(a.Count));

            var boxes = new List<OccluderBox>();
            foreach (var space in spaces)
            {
                if (boxes.Count >= MaxBoxesPerObject || space.Count * _cell * _cell < MinFloorArea)
                    break;
                var mask = new bool[_wall.Length];
                foreach (int i in space)
                    mask[i] = true;
                float? bottom = null;
                for (int level = 0; level < MaxLevels && boxes.Count < MaxBoxesPerObject; level++)
                {
                    var (x0, y0, x1, y1) = LargestRectangle(mask);
                    if (x1 < x0 || (x1 - x0 + 1) * _cell < MinSide || (y1 - y0 + 1) * _cell < MinSide
                        || (x1 - x0 + 1) * (y1 - y0 + 1) * _cell * _cell < MinFloorArea)
                        break;
                    bottom ??= WallFoot(x0, y0, x1, y1);
                    if (bottom is not { } low || Roof(x0, y0, x1, y1, low) is not { } roof || roof - low < 1f + 2 * Inset)
                        break;
                    var min = _origin + new Vector2(x0, y0) * _cell;
                    var max = _origin + new Vector2(x1 + 1, y1 + 1) * _cell;
                    if (_frame.Box(new Vector3(min, level == 0 ? low + Inset : low), new Vector3(max, roof - Inset)) is { } box)
                        boxes.Add(box);
                    // What rises well above this box: a tower on a lower part.
                    bottom = roof - Inset;
                    int higher = 0;
                    for (int i = 0; i < mask.Length; i++)
                    {
                        mask[i] &= !float.IsNaN(_roof[i]) && _roof[i] >= roof + MinLevelRise;
                        if (mask[i])
                            higher++;
                    }
                    if (higher * _cell * _cell < MinFloorArea)
                        break;
                }
            }
            return boxes;
        }

        // Where the walls in and around a rectangle stand: the ground, as buildings that cannot be entered have no floor
        // in their collision. Not their lowest point: some walls go down into a lower street.
        private float? WallFoot(int x0, int y0, int x1, int y1)
        {
            const int Band = 3;
            var feet = new List<float>();
            for (int y = Math.Max(0, y0 - Band); y <= Math.Min(_height - 1, y1 + Band); y++)
            {
                for (int x = Math.Max(0, x0 - Band); x <= Math.Min(_width - 1, x1 + Band); x++)
                {
                    if (_wall[y * _width + x])
                        feet.Add(_wallBottom[y * _width + x]);
                }
            }
            if (feet.Count == 0)
                return null;
            feet.Sort();
            return feet[(int)(feet.Count * FootShare)];
        }

        // The top of a rectangle standing at bottom: the roof only RoofShare of its roofed cells are lower than; null
        // when most of it has no roof.
        private float? Roof(int x0, int y0, int x1, int y1, float bottom)
        {
            var roofs = new List<float>();
            int cells = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    cells++;
                    float roof = _roof[y * _width + x];
                    if (roof >= bottom + MinRoofHeight)
                        roofs.Add(roof);
                }
            }
            if (roofs.Count < cells / 2)
                return null;
            roofs.Sort();
            return roofs[(int)(roofs.Count * RoofShare)];
        }

        // Calls mark for the cells a triangle crosses (seen from above), found by points spaced half a cell apart.
        private void Rasterize(Vector3 a, Vector3 b, Vector3 c, Action<int> mark)
        {
            var pa = new Vector2(a.X, a.Y);
            var pb = new Vector2(b.X, b.Y);
            var pc = new Vector2(c.X, c.Y);
            float longest = MathF.Max(Vector2.Distance(pa, pb), MathF.Max(Vector2.Distance(pb, pc), Vector2.Distance(pc, pa)));
            int steps = Math.Max(1, (int)MathF.Ceiling(longest / (_cell * 0.5f)));
            int last = -1;
            for (int i = 0; i <= steps; i++)
            {
                for (int j = 0; j <= steps - i; j++)
                {
                    var p = pa + (pb - pa) * (i / (float)steps) + (pc - pa) * (j / (float)steps);
                    int x = (int)((p.X - _origin.X) / _cell), y = (int)((p.Y - _origin.Y) / _cell);
                    if (x < 0 || y < 0 || x >= _width || y >= _height)
                        continue;
                    int index = y * _width + x;
                    if (index != last)
                        mark(index);
                    last = index;
                }
            }
        }

        // For each cell, in how many of the 8 directions a wall lies further on (in the grid).
        private byte[] WallDirections()
        {
            var count = new byte[_wall.Length];
            var ahead = new bool[_wall.Length];
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    // Cells are visited so that the next one in the direction is already done.
                    for (int sy = 0; sy < _height; sy++)
                    {
                        int y = dy > 0 ? _height - 1 - sy : sy;
                        for (int sx = 0; sx < _width; sx++)
                        {
                            int x = dx > 0 ? _width - 1 - sx : sx;
                            int nx = x + dx, ny = y + dy;
                            bool hit = false;
                            if (nx >= 0 && ny >= 0 && nx < _width && ny < _height)
                            {
                                int next = ny * _width + nx;
                                hit = _wall[next] || ahead[next];
                            }
                            ahead[y * _width + x] = hit;
                            if (hit)
                                count[y * _width + x]++;
                        }
                    }
                }
            }
            return count;
        }

        // Square dilation by r cells, as two passes (rows then columns).
        private bool[] Dilate(bool[] grid, int r)
        {
            var rows = new bool[grid.Length];
            for (int y = 0; y < _height; y++)
            {
                int last = int.MinValue / 2;
                for (int x = 0; x < _width; x++)
                {
                    if (grid[y * _width + x])
                        last = x;
                    rows[y * _width + x] = x - last <= r;
                }
                last = int.MaxValue / 2;
                for (int x = _width - 1; x >= 0; x--)
                {
                    if (grid[y * _width + x])
                        last = x;
                    rows[y * _width + x] |= last - x <= r;
                }
            }
            var result = new bool[grid.Length];
            for (int x = 0; x < _width; x++)
            {
                int last = int.MinValue / 2;
                for (int y = 0; y < _height; y++)
                {
                    if (rows[y * _width + x])
                        last = y;
                    result[y * _width + x] = y - last <= r;
                }
                last = int.MaxValue / 2;
                for (int y = _height - 1; y >= 0; y--)
                {
                    if (rows[y * _width + x])
                        last = y;
                    result[y * _width + x] |= last - y <= r;
                }
            }
            return result;
        }

        private static bool[] Invert(bool[] grid)
        {
            var result = new bool[grid.Length];
            for (int i = 0; i < grid.Length; i++)
                result[i] = !grid[i];
            return result;
        }

        // The cells of open connected to start, marked in done.
        private List<int> Fill(int start, bool[] open, bool[] done)
        {
            var cells = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (!open[i] || done[i])
                    continue;
                done[i] = true;
                cells.Add(i);
                int x = i % _width, y = i / _width;
                if (x > 0) stack.Push(i - 1);
                if (x < _width - 1) stack.Push(i + 1);
                if (y > 0) stack.Push(i - _width);
                if (y < _height - 1) stack.Push(i + _width);
            }
            return cells;
        }

        // The largest rectangle of true cells (histogram method), as inclusive cell bounds; x1 < x0 when there is none.
        private (int X0, int Y0, int X1, int Y1) LargestRectangle(bool[] mask)
        {
            var heights = new int[_width];
            var stack = new Stack<int>();
            long bestArea = 0;
            var best = (0, 0, -1, -1);
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                    heights[x] = mask[y * _width + x] ? heights[x] + 1 : 0;
                stack.Clear();
                for (int x = 0; x <= _width; x++)
                {
                    int h = x < _width ? heights[x] : 0;
                    while (stack.Count > 0 && heights[stack.Peek()] >= h)
                    {
                        int top = stack.Pop();
                        int left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                        long area = (long)heights[top] * (x - left);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = (left, y - heights[top] + 1, x - 1, y);
                        }
                    }
                    stack.Push(x);
                }
            }
            return best;
        }
    }
}
