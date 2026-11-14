using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Editor.Online;

namespace NodeRunner.App.Online;

// The selected element that is not an object (road node, occluder, room, portal, parking spot, timecycle zone, distant
// light), written the same way on every computer: the bytes themselves tell whether two participants hold the same one.
internal static class SelectedItem
{
    private enum Kind : byte { Node = 1, Occluder, Room, Portal, CarGenerator, Timecycle, Light }

    // What this computer has selected (null: no such element).
    public static byte[]? Of(AppState state)
    {
        if (state.World.Maps is not { } library)
            return null;
        return state.SelectedPathNode is { } node ? Node(node)
            : state.SelectedOccluder is { } occluder ? Occluder(occluder)
            : state.SelectedRoom is { } room ? Room(library, room, Kind.Room)
            : state.SelectedPortal is { } portal ? Room(library, portal, Kind.Portal)
            : state.SelectedCarGenerator is { } spot ? InMap(library, Kind.CarGenerator, spot.Map, spot.Index)
            : state.SelectedTimecycle is { } zone ? InMap(library, Kind.Timecycle, zone.Map, zone.Index)
            : state.SelectedLodLight is { } light ? InMap(library, Kind.Light, light.Distant, light.Index)
            : null;
    }

    public static byte[] Node(PathNode node) => Write(null, w =>
    {
        w.Write((byte)Kind.Node);
        w.Write(node.Id.AreaId);
        w.Write(node.Id.NodeId);
    });

    public static byte[] Occluder(OccluderSelection occluder) => Write(null, w =>
    {
        w.Write((byte)Kind.Occluder);
        w.Write(occluder.File.IsProject);
        w.Write(occluder.File.IsProject ? "" : occluder.File.GamePath);
        w.Write((int)occluder.Kind);
        w.Write(occluder.Index);
    });

    public static byte[]? Room(MapLibrary library, RoomSelection room, bool portal = false) =>
        Room(library, room, portal ? Kind.Portal : Kind.Room);

    private static byte[]? Room(MapLibrary library, RoomSelection room, Kind kind) => Write(library, w =>
    {
        w.Write((byte)kind);
        w.WriteEntity(room.Interior);
        w.Write(room.Index);
    });

    public static byte[]? CarGenerator(MapLibrary library, CarGeneratorSelection spot) => InMap(library, Kind.CarGenerator, spot.Map, spot.Index);

    public static byte[]? Timecycle(MapLibrary library, TimecycleSelection zone) => InMap(library, Kind.Timecycle, zone.Map, zone.Index);

    public static byte[]? Light(MapLibrary library, LodLightSelection light) => InMap(library, Kind.Light, light.Distant, light.Index);

    private static byte[]? InMap(MapLibrary library, Kind kind, MapData map, int index) => Write(library, w =>
    {
        w.Write((byte)kind);
        w.WriteMap(map);
        w.Write(index);
    });

    private static byte[] Write(MapLibrary? library, Action<EditWriter> write)
    {
        using var w = new EditWriter(library!);
        try
        {
            write(w);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        return w.ToArray();
    }

    // Where a participant's element is on this computer: its center and size, and what it is.
    public static (Vector3 Center, float Radius, string What)? Locate(byte[] item, AppState state)
    {
        if (item.Length == 0 || state.World.Maps is not { } library || state.Online.Context is not { } context)
            return null;
        try
        {
            using var r = new EditReader(item) { Context = context, FindOnly = true };
            switch ((Kind)r.ReadByte())
            {
                case Kind.Node:
                    var id = new PathNodeId(r.ReadUShort(), r.ReadUShort());
                    return state.World.PathNetwork?.GetNode(id) is { } node ? (node.Position, 2f, L("nœud")) : null;
                case Kind.Occluder:
                    bool isProject = r.ReadBool();
                    string path = r.ReadString();
                    var part = (OcclusionLayerPart)r.ReadInt();
                    int index = r.ReadInt();
                    var file = OccluderMaps.Get(state.World).FirstOrDefault(f => f.IsProject == isProject
                        && (isProject || string.Equals(f.GamePath, path, StringComparison.OrdinalIgnoreCase)));
                    if (file is null)
                        return null;
                    if (part == OcclusionLayerPart.Boxes)
                        return index < file.Boxes.Count ? (file.Boxes[index].Center, file.Boxes[index].Size.Length() / 2, L("occluder")) : null;
                    if (index >= file.Occluders.Models.Count || file.Occluders.Models[index].Vertices is not { Count: > 0 } vertices)
                        return null;
                    var (min, max) = (vertices[0], vertices[0]);
                    foreach (var v in vertices)
                        (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
                    return ((min + max) / 2, Vector3.Distance(min, max) / 2, L("occluder"));
                case var kind and (Kind.Room or Kind.Portal):
                    var interior = r.ReadEntity();
                    int at = r.ReadInt();
                    if (library.GetArchetype(interior.ArchetypeHash) is not { } archetype)
                        return null;
                    if (kind == Kind.Room)
                    {
                        if (at >= archetype.InteriorRooms.Count)
                            return null;
                        var room = archetype.InteriorRooms[at];
                        return (Vector3.Transform((room.BoundsMin + room.BoundsMax) / 2, interior.Transform),
                            Vector3.Distance(room.BoundsMin, room.BoundsMax) / 2, L("salle"));
                    }
                    if (at >= archetype.InteriorPortals.Count || archetype.InteriorPortals[at].Corners is not { Count: > 0 } corners)
                        return null;
                    var sum = Vector3.Zero;
                    foreach (var c in corners)
                        sum += c;
                    return (Vector3.Transform(sum / corners.Count, interior.Transform), 1.5f, L("portail"));
                case Kind.CarGenerator:
                    var spots = r.ReadMap().CarGenerators;
                    int s = r.ReadInt();
                    return s < spots.Count ? (spots[s].Position, 3f, L("place de véhicule")) : null;
                case Kind.Timecycle:
                    var zones = r.ReadMap().TimecycleModifiers;
                    int z = r.ReadInt();
                    return z < zones.Count ? (zones[z].Center, zones[z].Size.Length() / 2, L("zone de timecycle")) : null;
                case Kind.Light:
                    var lights = r.ReadMap().DistantLights;
                    int l = r.ReadInt();
                    return l < lights.Count ? (lights[l].Position, 1.5f, L("lumière LOD")) : null;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
