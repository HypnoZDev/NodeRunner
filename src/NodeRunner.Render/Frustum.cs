using System.Numerics;

namespace NodeRunner.Render;

// The volume seen by the camera, used to skip objects that are off screen. Built from a view-projection matrix
// (reversed-Z, infinite far plane: only 5 planes matter).
public readonly struct Frustum
{
    private readonly Plane _left, _right, _bottom, _top, _near;

    public Frustum(Matrix4x4 viewProjection)
    {
        // Rows of the clip-space transform for row vectors: clip = v * M, so use the matrix columns.
        var m = viewProjection;
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);

        _left = Normalize(c4 + c1);
        _right = Normalize(c4 - c1);
        _bottom = Normalize(c4 + c2);
        _top = Normalize(c4 - c2);
        _near = Normalize(c4 - c3); // Reversed Z: depth <= w in front of the near plane.
    }

    public bool Intersects(Vector3 center, float radius) =>
        Distance(_left, center) >= -radius &&
        Distance(_right, center) >= -radius &&
        Distance(_bottom, center) >= -radius &&
        Distance(_top, center) >= -radius &&
        Distance(_near, center) >= -radius;

    private static float Distance(Plane plane, Vector3 point) => Vector3.Dot(plane.Normal, point) + plane.D;

    private static Plane Normalize(Vector4 v) => Plane.Normalize(new Plane(v.X, v.Y, v.Z, v.W));
}
