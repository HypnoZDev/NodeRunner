using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Opening a building: a click on its wall in the 3D view places a door-sized opening (on the building's model, its
// bottom on the ground in front); then sizes, the room behind, the interior's name and the door are chosen before
// AppState.OpenBuilding makes it all.
internal sealed class BuildingOpeningTool(MapEntity building, ModelPartTool model)
{
    public MapEntity Building { get; } = building;
    public ModelPartTool Model { get; } = model;

    // As clicked (world space), before the shift and sizes below.
    public Opening? Placed { get; private set; }
    public Vector2 Shift;
    public float Width = Opening.DefaultWidth;
    public float Height = Opening.DefaultHeight;
    public Vector3 RoomSize = BuildingOpening.DefaultRoomSize;
    public string Name = "";
    public (uint Hash, string Name)? Door;
    public bool AddDoor = true;
    public bool AddRoom = true;
    public bool CutCollision = true;
    public bool RemoveOccluders = true;
    public bool RemoveGameDoors = true;

    // Changes each time the opening changes (the view redraws its outline).
    public int Revision { get; private set; }

    // Other objects of the map the opening goes through (a door frame laid on the wall, a facade piece): their models,
    // already cut in memory for Revision, are saved with the building's when kept.
    public List<Neighbour> Others { get; } = [];
    public int OthersRevision = -1;

    public sealed class Neighbour(MapEntity entity, string name, int copies, int triangles, ModelPartTool model)
    {
        public MapEntity Entity { get; } = entity;
        public string Name { get; } = name;
        // Objects of the map using the same model: cutting it cuts them all.
        public int Copies { get; } = copies;
        public int Triangles { get; } = triangles;
        public ModelPartTool Model { get; } = model;
        public bool Cut = copies <= 1;
    }

    public Opening? Current => Placed is { } o
        ? o with { Bottom = o.Bottom + o.Right * Shift.X + new Vector3(0, 0, Shift.Y), Width = Width, Height = Height }
        : null;

    public void Changed() => Revision++;

    // A click in the view (world ray): the opening goes on the wall of the building's model under it, its bottom on the
    // ground in front of the wall when there is one within its height. False when the ray misses the model's walls.
    public bool Click(Vector3 origin, Vector3 direction, Func<Vector3, float?> groundAt)
    {
        if (!Matrix4x4.Invert(Building.Transform, out var toModel)
            || Model.HitPoint(Vector3.Transform(origin, toModel), Vector3.Normalize(Vector3.TransformNormal(direction, toModel))) is not { } hit)
            return false;
        var point = Vector3.Transform(hit.Point, Building.Transform);
        var normal = Vector3.TransformNormal(hit.Normal, Building.Transform);
        var flat = new Vector3(normal.X, normal.Y, 0);
        if (flat.LengthSquared() < 1e-6f)
            return false;
        var front = point + Vector3.Normalize(flat) * 0.5f;
        float? floor = groundAt(front with { Z = point.Z + 0.5f }) is { } ground && point.Z - ground <= Height ? ground : null;
        if (Opening.At(point, normal, Width, Height, floor) is not { } opening)
            return false;
        Placed = opening;
        Shift = Vector2.Zero;
        Revision++;
        return true;
    }
}
