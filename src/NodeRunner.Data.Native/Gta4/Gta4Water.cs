using System.Globalization;
using System.Numerics;
using NodeRunner.Data.Water;

namespace NodeRunner.Data.Native.Gta4;

// Water surfaces of Liberty City ("common\data\water.dat", shared by the episodes). One line per flat rectangle:
// four corners of seven numbers (x, y, z, then flow and wave values), then flags and one more value.
public static class Gta4Water
{
    public const string WaterFile = @"common\data\water.dat";

    private const int CornerValues = 7, Corners = 4;

    public static WaterMap Load(Gta4FileSystem files)
    {
        var map = new WaterMap();
        if (files.ReadFile(WaterFile) is not { } data)
        {
            map.Errors.Add($"{WaterFile} : fichier introuvable");
            return map;
        }
        map.Quads.AddRange(Parse(System.Text.Encoding.ASCII.GetString(data)));
        if (map.Quads.Count > 0)
        {
            map.OceanBounds = new Vector4(map.Quads.Min(q => q.MinX), map.Quads.Min(q => q.MinY),
                map.Quads.Max(q => q.MaxX), map.Quads.Max(q => q.MaxY));
        }
        return map;
    }

    // Overlapping rectangles at the same height are cut so that each place is covered once (the water is
    // see-through: twice would look darker).
    public static List<WaterQuad> Parse(string text)
    {
        var quads = new List<WaterQuad>();
        foreach (string line in text.Split('\n'))
        {
            var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < CornerValues * Corners)
                continue;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, z = 0f;
            bool valid = true;
            for (int c = 0; c < Corners && valid; c++)
            {
                valid = TryFloat(values[c * CornerValues], out float x) & TryFloat(values[c * CornerValues + 1], out float y)
                    & TryFloat(values[c * CornerValues + 2], out float h);
                minX = MathF.Min(minX, x);
                maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                z = h;
            }
            // Corners are a few hundredths of a millimetre off: rounded to the centimetre.
            minX = MathF.Round(minX, 2);
            maxX = MathF.Round(maxX, 2);
            minY = MathF.Round(minY, 2);
            maxY = MathF.Round(maxY, 2);
            if (!valid || maxX - minX < 0.01f || maxY - minY < 0.01f)
                continue;

            var pieces = new List<WaterQuad> { new(minX, maxX, minY, maxY, z, WaterQuadShape.Rectangle) };
            foreach (var other in quads)
            {
                if (MathF.Abs(other.Z - z) > 0.01f)
                    continue;
                pieces = pieces.SelectMany(p => Subtract(p, other)).ToList();
                if (pieces.Count == 0)
                    break;
            }
            quads.AddRange(pieces);
        }
        return quads;
    }

    // What is left of a rectangle once another is taken out: up to four rectangles.
    private static IEnumerable<WaterQuad> Subtract(WaterQuad a, WaterQuad b)
    {
        float left = MathF.Max(a.MinX, b.MinX), right = MathF.Min(a.MaxX, b.MaxX);
        float bottom = MathF.Max(a.MinY, b.MinY), top = MathF.Min(a.MaxY, b.MaxY);
        if (right - left < 0.01f || top - bottom < 0.01f)
        {
            yield return a;
            yield break;
        }
        if (a.MinY < bottom)
            yield return a with { MaxY = bottom };
        if (top < a.MaxY)
            yield return a with { MinY = top };
        if (a.MinX < left)
            yield return a with { MaxX = left, MinY = bottom, MaxY = top };
        if (right < a.MaxX)
            yield return a with { MinX = right, MinY = bottom, MaxY = top };
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
