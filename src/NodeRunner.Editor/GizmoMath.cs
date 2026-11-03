using System.Numerics;

namespace NodeRunner.Editor;

/// <summary>Geometry used by the 3D handles (gizmo): mouse ray against an axis or a plane, angles, snapping.</summary>
public static class GizmoMath
{
    /// <summary>
    /// Position along the axis line (origin + t * direction) of the point closest to the ray.
    /// Null when the ray is (almost) parallel to the axis: dragging along it would jump.
    /// </summary>
    public static float? ClosestAxisParameter(Vector3 axisOrigin, Vector3 axisDirection, Vector3 rayOrigin, Vector3 rayDirection)
    {
        var d1 = Vector3.Normalize(axisDirection);
        var d2 = Vector3.Normalize(rayDirection);
        float b = Vector3.Dot(d1, d2);
        float denominator = 1f - b * b;
        if (denominator < 1e-4f)
            return null;
        var r = axisOrigin - rayOrigin;
        float c = Vector3.Dot(d1, r);
        float f = Vector3.Dot(d2, r);
        return (b * f - c) / denominator;
    }

    /// <summary>Where the ray meets the plane, null if it is parallel to it or the plane is behind the ray.</summary>
    public static Vector3? IntersectPlane(Vector3 rayOrigin, Vector3 rayDirection, Vector3 planePoint, Vector3 planeNormal)
    {
        float denominator = Vector3.Dot(rayDirection, planeNormal);
        if (MathF.Abs(denominator) < 1e-6f)
            return null;
        float t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denominator;
        return t < 0f ? null : rayOrigin + rayDirection * t;
    }

    /// <summary>Angle (radians, counter-clockwise when the axis points at the viewer) of a vector turning around an axis.</summary>
    public static float AngleAround(Vector3 vector, Vector3 axis)
    {
        var (u, v) = PlaneBasis(axis);
        return MathF.Atan2(Vector3.Dot(vector, v), Vector3.Dot(vector, u));
    }

    /// <summary>Two unit vectors perpendicular to the axis and to each other (u x v = axis).</summary>
    public static (Vector3 U, Vector3 V) PlaneBasis(Vector3 axis)
    {
        var n = Vector3.Normalize(axis);
        var helper = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(helper, n));
        return (u, Vector3.Cross(n, u));
    }

    /// <summary>Brings an angle back between -pi and pi.</summary>
    public static float WrapAngle(float radians)
    {
        radians %= MathF.Tau;
        if (radians > MathF.PI) radians -= MathF.Tau;
        else if (radians < -MathF.PI) radians += MathF.Tau;
        return radians;
    }

    /// <summary>Nearest multiple of <paramref name="step"/> (the value itself if step is not positive).</summary>
    public static float Snap(float value, float step) => step > 0f ? MathF.Round(value / step) * step : value;

    /// <summary>Distance in pixels from a point to a segment on the screen.</summary>
    public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        float t = lengthSquared < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }

    /// <summary>True if the point is inside the convex quadrilateral (corners in order, either direction).</summary>
    public static bool InsideQuad(Vector2 point, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Side(Vector2 p, Vector2 from, Vector2 to) => (to.X - from.X) * (p.Y - from.Y) - (to.Y - from.Y) * (p.X - from.X);
        float s1 = Side(point, a, b), s2 = Side(point, b, c), s3 = Side(point, c, d), s4 = Side(point, d, a);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0);
    }
}
