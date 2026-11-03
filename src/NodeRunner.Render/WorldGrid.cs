using System.Numerics;

namespace NodeRunner.Render;

/// <summary>
/// Reference grid on the sea-level plane (Z = 0) that follows the camera, plus the world axes at the origin.
/// The grid gets larger and coarser as the camera goes up.
/// </summary>
public static class WorldGrid
{
    private static readonly Vector4 MinorColor = new(0.35f, 0.38f, 0.42f, 0.35f);
    private static readonly Vector4 MajorColor = new(0.55f, 0.58f, 0.62f, 0.6f);
    private static readonly Vector4 AxisXColor = new(0.9f, 0.25f, 0.25f, 1f);
    private static readonly Vector4 AxisYColor = new(0.25f, 0.85f, 0.3f, 1f);
    private static readonly Vector4 AxisZColor = new(0.3f, 0.5f, 1f, 1f);

    /// <summary>Adds the grid lines to <paramref name="lines"/> and returns the distance at which they should fade out.</summary>
    public static float AddTo(ColorRenderer lines, Vector3 cameraPosition)
    {
        float altitude = MathF.Abs(cameraPosition.Z);
        float extent = Math.Clamp(altitude * 4f, 500f, 8000f);
        float spacing = extent <= 1000f ? 10f : 100f;
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
            lines.AddLine(new Vector3(x, minY, 0f), new Vector3(x, maxY, 0f), IsMajor(x, majorSpacing) ? MajorColor : MinorColor);
            lines.AddLine(new Vector3(minX, y, 0f), new Vector3(maxX, y, 0f), IsMajor(y, majorSpacing) ? MajorColor : MinorColor);
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
