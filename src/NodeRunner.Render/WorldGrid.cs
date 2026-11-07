using System.Numerics;

namespace NodeRunner.Render;

// Reference grid on the sea-level plane (Z = 0) that follows the camera, plus the world axes at the origin. The grid
// gets larger and coarser as the camera goes up.
public static class WorldGrid
{
    public static readonly Vector4 DefaultColor = new(0.55f, 0.58f, 0.62f, 0.6f);
    private const int MaxHalfCount = 250; // Lines on each side of the camera, whatever the spacing.
    private static readonly Vector4 AxisXColor = new(0.9f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 AxisYColor = new(0.25f, 0.85f, 0.3f, 1f);
    private static readonly Vector4 AxisZColor = new(0.3f, 0.5f, 1f, 1f);

    // spacing: metres between two lines, 0 = chosen from the altitude. color: the major lines (the others are fainter).
    public static float AddTo(ColorRenderer lines, Vector3 cameraPosition, float spacing = 0f, Vector4? color = null)
    {
        var majorColor = color ?? DefaultColor;
        var minorColor = new Vector4(majorColor.X * 0.65f, majorColor.Y * 0.65f, majorColor.Z * 0.68f, majorColor.W * 0.58f);
        float altitude = MathF.Abs(cameraPosition.Z);
        float extent = Math.Clamp(altitude * 4f, 500f, 8000f);
        if (spacing <= 0f)
            spacing = extent <= 1000f ? 10f : 100f;
        extent = MathF.Min(extent, spacing * MaxHalfCount);
        float majorSpacing = spacing * 10f;

        // Snap the grid to multiples of the major spacing so lines don't "swim" when the camera moves.
        float centerX = MathF.Round(cameraPosition.X / majorSpacing) * majorSpacing;
        float centerY = MathF.Round(cameraPosition.Y / majorSpacing) * majorSpacing;
        int halfCount = (int)MathF.Ceiling(extent / spacing);

        float minX = centerX - halfCount * spacing, maxX = centerX + halfCount * spacing;
        float minY = centerY - halfCount * spacing, maxY = centerY + halfCount * spacing;

        for (int i = -halfCount; i <= halfCount; i++)
        {
            float x = centerX + i * spacing;
            float y = centerY + i * spacing;
            lines.AddLine(new Vector3(x, minY, 0f), new Vector3(x, maxY, 0f), IsMajor(x, majorSpacing) ? majorColor : minorColor);
            lines.AddLine(new Vector3(minX, y, 0f), new Vector3(maxX, y, 0f), IsMajor(y, majorSpacing) ? majorColor : minorColor);
        }

        // World axes, slightly above the grid so they are not hidden by it.
        const float axisLength = 100f;
        var origin = new Vector3(0f, 0f, 0.05f);
        lines.AddLine(origin, origin + new Vector3(axisLength, 0f, 0f), AxisXColor);
        lines.AddLine(origin, origin + new Vector3(0f, axisLength, 0f), AxisYColor);
        lines.AddLine(origin, origin + new Vector3(0f, 0f, axisLength), AxisZColor);

        return extent;
    }

    private static bool IsMajor(float value, float majorSpacing) =>
        MathF.Abs(MathF.IEEERemainder(value, majorSpacing)) < 0.5f;
}
