using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// What the "Rooms and portals" layer shows.
internal enum RoomLayerPart
{
    Rooms,
    InnerPortals,
    OutsidePortals,
}

// A room of an interior instance near the camera, with what is needed to find it under the mouse.
internal sealed record RoomBox(MapEntity Interior, int Index, InteriorRoom Room, Matrix4x4 Transform, int PortalCount, int ObjectCount)
{
    public float Volume
    {
        get
        {
            var size = Room.BoundsMax - Room.BoundsMin;
            return MathF.Abs(size.X * size.Y * size.Z);
        }
    }
}

// A portal of an interior instance near the camera: its corners in the world and the rooms it joins.
internal sealed record PortalBox(MapEntity Interior, int Index, InteriorPortal Portal, Vector3[] Corners, string RoomFrom, string RoomTo, int ObjectCount)
{
    public bool LeadsOutside => Portal.RoomFrom == 0 || Portal.RoomTo == 0;
}

// A room of an interior selected for editing; the change goes to every instance of the interior.
internal sealed record RoomSelection(MapEntity Interior, int Index);

// Rooms and portals of the interiors (MLO) near the camera: the box of each room (room 0, "limbo", stands for the
// outside and is not drawn) and each opening, between two rooms or to the outside, as a translucent surface with its
// outline.
internal sealed class RoomLayer : IDisposable
{
    public const string LayerPrefix = "Room.";
    public const float Radius = 250f;

    private const float RebuildDistance = 40f;

    public static readonly IReadOnlyList<RoomLayerPart> Parts = Enum.GetValues<RoomLayerPart>();

    private readonly ColorMesh?[] _lines = new ColorMesh?[Parts.Count];
    private readonly ColorMesh?[] _surfaces = new ColorMesh?[Parts.Count];
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private int _builtStructure = -1;
    private int _builtRooms = -1;

    public static string Label(RoomLayerPart part) => part switch
    {
        RoomLayerPart.Rooms => "Salles",
        RoomLayerPart.InnerPortals => "Portails entre salles",
        RoomLayerPart.OutsidePortals => "Portails vers l'extérieur",
        _ => part.ToString(),
    };

    public static Vector4 Color(RoomLayerPart part) => part switch
    {
        RoomLayerPart.Rooms => new(0.8f, 0.5f, 1f, 0.9f),
        RoomLayerPart.InnerPortals => new(0.3f, 0.85f, 1f, 0.9f),
        RoomLayerPart.OutsidePortals => new(1f, 0.6f, 0.2f, 0.9f),
        _ => Vector4.One,
    };

    public static string LayerName(RoomLayerPart part) => LayerPrefix + part;

    public IReadOnlyList<RoomBox> Rooms { get; private set; } = [];

    public IReadOnlyList<PortalBox> Portals { get; private set; } = [];

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, bool enabled)
    {
        if (loader.Maps != _library)
        {
            _library = loader.Maps;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add("Salles et portails", finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN); // Computed again at the next frame.
            }
            else if (finished.Result.Library == _library)
            {
                Apply(finished.Result, renderer);
            }
        }

        if (_library is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || _library.StructureVersion != _builtStructure
            || _library.RoomVersion != _builtRooms)
        {
            var library = _library;
            _builtStructure = library.StructureVersion;
            _builtRooms = library.RoomVersion;
            _builtCenter = center;
            _building = Task.Run(() => Build(library, center));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<RoomLayerPart, bool> isVisible)
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            if (!isVisible((RoomLayerPart)i))
                continue;
            if (_surfaces[i] is { } surface)
                renderer.Draw(surface, viewProjection, cameraPosition, Radius * 1.2f);
            if (_lines[i] is { } lines)
                renderer.Draw(lines, viewProjection, cameraPosition, Radius * 1.2f);
        }
    }

    // What is under a ray (in the world): the nearest visible portal, otherwise the room it enters first (the smallest
    // one when several start at the same place, e.g. the camera inside nested rooms).
    public (RoomBox? Room, PortalBox? Portal) Pick(Vector3 origin, Vector3 direction, Func<RoomLayerPart, bool> isVisible)
    {
        PortalBox? portal = null;
        float portalDistance = float.MaxValue;
        foreach (var candidate in Portals)
        {
            if (!isVisible(candidate.LeadsOutside ? RoomLayerPart.OutsidePortals : RoomLayerPart.InnerPortals))
                continue;
            if (Hit(candidate.Corners, origin, direction) is { } distance && distance < portalDistance)
            {
                portal = candidate;
                portalDistance = distance;
            }
        }
        if (portal is not null || !isVisible(RoomLayerPart.Rooms))
            return (null, portal);

        RoomBox? best = null;
        float bestEntry = float.MaxValue;
        foreach (var room in Rooms)
        {
            if (Entry(room, origin, direction) is not { } entry)
                continue;
            if (entry < bestEntry - 0.01f || MathF.Abs(entry - bestEntry) <= 0.01f && room.Volume < best!.Volume)
            {
                best = room;
                bestEntry = entry;
            }
        }
        return (best, null);
    }

    // The same for a point of the 3D view (pixels from its top left corner).
    public (RoomBox? Room, PortalBox? Portal) Pick(Matrix4x4 viewProjection, Vector3 camera, Vector2 point, Vector2 viewport,
        Func<RoomLayerPart, bool> isVisible)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return (null, null);
        // Through the point on the near plane (depth 1 with the reversed-Z projection).
        var ndc = new Vector2(point.X / viewport.X * 2f - 1f, 1f - point.Y / viewport.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        return Pick(camera, Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera), isVisible);
    }

    private static float? Hit(Vector3[] corners, Vector3 origin, Vector3 direction)
    {
        for (int c = 1; c + 1 < corners.Length; c++)
        {
            // Möller-Trumbore on each triangle of the fan, both faces.
            var a = corners[0];
            var e1 = corners[c] - a;
            var e2 = corners[c + 1] - a;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-8f)
                continue;
            var t0 = origin - a;
            float u = Vector3.Dot(t0, p) / det;
            if (u < 0f || u > 1f)
                continue;
            var q = Vector3.Cross(t0, e1);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0f || u + v > 1f)
                continue;
            float t = Vector3.Dot(e2, q) / det;
            if (t > 0f)
                return t;
        }
        return null;
    }

    // Distance along the ray at which it enters the room's box (0 when the origin is inside), null if it misses.
    private static float? Entry(RoomBox room, Vector3 origin, Vector3 direction)
    {
        if (!Matrix4x4.Invert(room.Transform, out var toRoom))
            return null;
        var o = Vector3.Transform(origin, toRoom);
        var d = Vector3.TransformNormal(direction, toRoom);
        float enter = 0f, exit = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = o[axis], da = d[axis], min = room.Room.BoundsMin[axis], max = room.Room.BoundsMax[axis];
            if (MathF.Abs(da) < 1e-6f)
            {
                if (oa < min || oa > max)
                    return null;
                continue;
            }
            float t1 = (min - oa) / da, t2 = (max - oa) / da;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            enter = MathF.Max(enter, t1);
            exit = MathF.Min(exit, t2);
            if (enter > exit)
                return null;
        }
        return enter;
    }

    private sealed record BuildResult(MapLibrary Library, LineBatch[] Lines, TriangleBatch[] Surfaces, List<RoomBox> Rooms, List<PortalBox> Portals);

    private static BuildResult Build(MapLibrary library, Vector2 center)
    {
        var lines = Parts.Select(_ => new LineBatch()).ToArray();
        var surfaces = Parts.Select(_ => new TriangleBatch()).ToArray();
        var rooms = new List<RoomBox>();
        var portals = new List<PortalBox>();

        foreach (var interior in library.EntitiesAround(center, Radius))
        {
            if (!interior.IsInterior || interior.IsDeleted || library.GetArchetype(interior.ArchetypeHash) is not { } archetype)
                continue;
            var transform = interior.Transform;

            var objectsPerRoom = new int[archetype.InteriorRooms.Count];
            foreach (var inside in archetype.InteriorEntities)
            {
                if (inside.InteriorRoom >= 0 && inside.InteriorRoom < objectsPerRoom.Length)
                    objectsPerRoom[inside.InteriorRoom]++;
            }
            for (int r = 1; r < archetype.InteriorRooms.Count; r++)
            {
                var room = archetype.InteriorRooms[r];
                int portalCount = archetype.InteriorPortals.Count(p => p.RoomFrom == r || p.RoomTo == r);
                rooms.Add(new RoomBox(interior, r, room, transform, portalCount, objectsPerRoom[r]));
                lines[(int)RoomLayerPart.Rooms].AddBox(room.BoundsMin, room.BoundsMax, transform, Color(RoomLayerPart.Rooms));
            }

            for (int p = 0; p < archetype.InteriorPortals.Count; p++)
            {
                var portal = archetype.InteriorPortals[p];
                if (portal.Corners.Count < 3)
                    continue;
                var part = portal.RoomFrom == 0 || portal.RoomTo == 0 ? RoomLayerPart.OutsidePortals : RoomLayerPart.InnerPortals;
                var color = Color(part);
                var fill = color with { W = 0.44f };
                var corners = portal.Corners.Select(c => Vector3.Transform(c, transform)).ToArray();
                int index = p;
                portals.Add(new PortalBox(interior, p, portal, corners, RoomName(archetype, portal.RoomFrom), RoomName(archetype, portal.RoomTo),
                    archetype.InteriorEntities.Count(e => e.InteriorPortal == index)));
                for (int c = 0; c < corners.Length; c++)
                    lines[(int)part].AddLine(corners[c], corners[(c + 1) % corners.Length], color);
                // Fan of triangles (ColorRenderer draws both faces).
                for (int c = 1; c + 1 < corners.Length; c++)
                {
                    surfaces[(int)part].AddTriangle(corners[0], corners[c], corners[c + 1], fill);
                }
            }
        }

        return new BuildResult(library, lines, surfaces, rooms, portals);
    }

    private static string RoomName(Archetype archetype, int room) =>
        room == 0 ? "extérieur"
        : room < archetype.InteriorRooms.Count && archetype.InteriorRooms[room].Name.Length > 0 ? $"{room} ({archetype.InteriorRooms[room].Name})"
        : $"{room}";

    private void Apply(BuildResult result, ColorRenderer renderer)
    {
        DisposeMeshes();
        for (int i = 0; i < Parts.Count; i++)
        {
            _lines[i] = renderer.CreateMesh(result.Lines[i]);
            _surfaces[i] = renderer.CreateMesh(result.Surfaces[i]);
        }
        Rooms = result.Rooms;
        Portals = result.Portals;
    }

    private void DisposeMeshes()
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            _lines[i]?.Dispose();
            _lines[i] = null;
            _surfaces[i]?.Dispose();
            _surfaces[i] = null;
        }
        Rooms = [];
        Portals = [];
    }

    public void Dispose() => DisposeMeshes();
}
