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
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || _library.StructureVersion != _builtStructure)
        {
            var library = _library;
            _builtStructure = library.StructureVersion;
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
