using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Gta4;
using NodeRunner.Data.Water;

namespace NodeRunner.Data.Native.Classic;

// Water of the first trilogy:
// - GTA III / Vice City: "data\waterpro.dat", water heights (count, 48 heights, 48 rectangles) then a 64 x 64 and a
//   128 x 128 grid of height numbers (0x80 = no water), [x][y]; the fine grid has 32 m cells from (-2048, -2048) in
//   GTA III, from (-2448, -2048) in Vice City (its map is further west);
// - San Andreas: "data\water.dat", the GTA IV text layout (four corners of seven numbers, then flags) plus triangles
//   (three corners).
public static class ClassicWater
{
    public const string GridFile = @"data\waterpro.dat";
    public const string SanAndreasFile = @"data\water.dat";

    private const int Levels = 48, Coarse = 64, Fine = 128;
    private const float Cell = 32f;
    private const int CornerValues = 7;

    public static WaterMap Load(ClassicFileSystem files, ClassicGame game)
    {
        var map = new WaterMap();
        string path = game == ClassicGame.SanAndreas ? SanAndreasFile : GridFile;
        if (files.ReadFile(path) is not { } data)
        {
            map.Errors.Add($"{path} : fichier introuvable");
            return map;
        }
        try
        {
            if (game == ClassicGame.SanAndreas)
                ReadSanAndreas(map, Encoding.ASCII.GetString(data));
            else
                ReadGrid(map, data, game == ClassicGame.ViceCity ? new Vector2(-2448f, -2048f) : new Vector2(-2048f, -2048f));
        }
        catch (InvalidDataException ex)
        {
            map.Errors.Add($"{path} : {ex.Message}");
        }
        return map;
    }

    // Cells of the fine grid at the same height, joined along each row.
    public static void ReadGrid(WaterMap map, ReadOnlySpan<byte> data, Vector2 origin)
    {
        const int fineStart = 4 + Levels * 4 + Levels * 16 + Coarse * Coarse;
        if (data.Length < fineStart + Fine * Fine)
            throw new InvalidDataException("grille d'eau tronquée");
        int count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data), 0, Levels);
        var heights = new float[Levels];
        for (int i = 0; i < count; i++)
            heights[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(4 + i * 4)..]);
        var grid = data.Slice(fineStart, Fine * Fine);
        var used = new int[Levels];
        for (int y = 0; y < Fine; y++)
        {
            int x = 0;
            while (x < Fine)
            {
                byte value = grid[x * Fine + y];
                if ((value & 0x80) != 0 || (value & 0x7F) >= count)
                {
                    x++;
                    continue;
                }
                int level = value & 0x7F, start = x;
                while (x < Fine && grid[x * Fine + y] == value)
                    x++;
                used[level] += x - start;
                map.Quads.Add(new WaterQuad(origin.X + start * Cell, origin.X + x * Cell, origin.Y + y * Cell, origin.Y + (y + 1) * Cell,
                    heights[level], WaterQuadShape.Rectangle));
            }
        }
        // The sea goes on beyond the grid at the most common height.
        map.OceanBounds = new Vector4(origin.X, origin.Y, origin.X + Fine * Cell, origin.Y + Fine * Cell);
        map.OceanHeight = heights[Array.IndexOf(used, used.Max())];
    }

    public static void ReadSanAndreas(WaterMap map, string text)
    {
        var quads = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= CornerValues * 4)
                quads.AppendLine(line);
            else if (values.Length >= CornerValues * 3 && Triangle(values) is { } triangle)
                map.Quads.Add(triangle);
        }
        map.Quads.AddRange(Gta4Water.Parse(quads.ToString()));
        if (map.Quads.Count > 0)
        {
            map.OceanBounds = new Vector4(map.Quads.Min(q => q.MinX), map.Quads.Min(q => q.MinY),
                map.Quads.Max(q => q.MaxX), map.Quads.Max(q => q.MaxY));
        }
    }

    // A right triangle along the axes: the corner of its box that is not one of its points is the one cut off.
    private static WaterQuad? Triangle(string[] values)
    {
        var points = new Vector3[3];
        for (int c = 0; c < 3; c++)
        {
            if (!TryFloat(values[c * CornerValues], out float x) || !TryFloat(values[c * CornerValues + 1], out float y)
                || !TryFloat(values[c * CornerValues + 2], out float z))
                return null;
            points[c] = new Vector3(MathF.Round(x, 2), MathF.Round(y, 2), z);
        }
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        if (maxX - minX < 0.01f || maxY - minY < 0.01f)
            return null;
        bool Has(float x, float y) => points.Any(p => MathF.Abs(p.X - x) < 0.05f && MathF.Abs(p.Y - y) < 0.05f);
        WaterQuadShape? shape = !Has(maxX, maxY) ? WaterQuadShape.WithoutNorthEast
            : !Has(maxX, minY) ? WaterQuadShape.WithoutSouthEast
            : !Has(minX, minY) ? WaterQuadShape.WithoutSouthWest
            : !Has(minX, maxY) ? WaterQuadShape.WithoutNorthWest
            : null;
        return shape is { } s ? new WaterQuad(minX, maxX, minY, maxY, points[2].Z, s) : null;
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
