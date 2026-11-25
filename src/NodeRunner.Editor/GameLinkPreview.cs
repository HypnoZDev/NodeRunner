using System.Numerics;
using System.Text.Json.Nodes;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// What the game must show differently from what its server streams, while the mapper edits: for each object touched
// since the last baseline (NodeRunner's start or the last export to the server), the game's object to hide and the
// object to put in its place. Objects of interiors are left out.
public sealed class GameLinkPreview
{
    private readonly record struct Look(uint Model, Vector3 Position, Quaternion Orientation);

    private sealed class Tracked(int key, Look? before)
    {
        public int Key { get; } = key;
        public Look? Before { get; } = before;
    }

    private readonly Dictionary<MapEntity, Tracked> _tracked = new(ReferenceEqualityComparer.Instance);
    private int _nextKey = 1;
    private long _signature;

    // Changes each time what the game must show changes.
    public int Revision { get; private set; }

    public int Count => _tracked.Count;

    // What the game shows now becomes the baseline (after an export to the server, or another map loaded).
    public void Reset()
    {
        if (_tracked.Count == 0)
            return;
        _tracked.Clear();
        _signature = 0;
        Revision++;
    }

    public static bool CanPreview(MapEntity entity) =>
        entity.Interior is null && !entity.IsInterior && entity.Definition is null && entity.Copies.Count == 0;

    // An object about to be edited (selected): its baseline is what it is now.
    public void Watch(MapEntity entity)
    {
        if (!_tracked.ContainsKey(entity) && CanPreview(entity))
            _tracked[entity] = new Tracked(_nextKey++, LookOf(entity));
    }

    // An edit done, undone or redone: objects not watched yet get the baseline they had before it.
    public void Observe(IEdit edit, bool undone)
    {
        switch (edit)
        {
            case PlacementEdit placement:
                Track(placement.Entity, e => Present(e, undone ? placement.After : placement.Before));
                break;
            case GroupPlacementEdit group:
                var entities = group.Entities.ToList();
                var before = undone ? group.After : group.Before;
                for (int i = 0; i < entities.Count && i < before.Count; i++)
                {
                    var place = before[i];
                    Track(entities[i], e => Present(e, place));
                }
                break;
            case AddEdit add:
                foreach (var entity in add.Entities)
                    Track(entity, e => undone ? LookOf(e, present: true) : null);
                break;
            case DeleteEdit delete:
                foreach (var entity in delete.Entities)
                    Track(entity, e => undone ? null : LookOf(e, present: true));
                break;
            default:
                foreach (var entity in edit.Entities)
                    Track(entity, e => LookOf(e));
                break;
        }
    }

    private void Track(MapEntity entity, Func<MapEntity, Look?> before)
    {
        if (!_tracked.ContainsKey(entity) && CanPreview(entity))
            _tracked[entity] = new Tracked(_nextKey++, before(entity));
    }

    private static Look Present(MapEntity entity, Placement place) => new(entity.ArchetypeHash, place.Position, place.Orientation);

    private static Look? LookOf(MapEntity entity, bool present = false) =>
        entity.IsDeleted && !present ? null : new Look(entity.ArchetypeHash, entity.Position, entity.Orientation);

    // True when what the game must show changed since the last call (Revision is then increased).
    public bool Refresh()
    {
        long signature = 17;
        foreach (var (entity, tracked) in _tracked)
            signature = signature * 31 + HashCode.Combine(tracked.Key, entity.Revision, entity.IsDeleted, entity.ArchetypeHash);
        if (signature == _signature)
            return false;
        _signature = signature;
        Revision++;
        return true;
    }

    private static bool Same(Look? a, Look? b)
    {
        if (a is not { } x || b is not { } y)
            return a is null && b is null;
        return x.Model == y.Model && Vector3.DistanceSquared(x.Position, y.Position) < 1e-6f
            && MathF.Abs(Quaternion.Dot(x.Orientation, y.Orientation)) > 0.999999f;
    }

    // The objects to change in the game, nearest to a point first: {k, hm, hx, hy, hz} = game object to hide,
    // {m, x, y, z, qx, qy, qz, qw} = object to show instead.
    public JsonArray Items(Vector3 around, int max = 600)
    {
        var items = new List<(float Distance, JsonObject Item)>();
        foreach (var (entity, tracked) in _tracked)
        {
            var now = LookOf(entity);
            if (Same(tracked.Before, now))
                continue;
            var item = new JsonObject { ["k"] = tracked.Key };
            var at = (now ?? tracked.Before!.Value).Position;
            if (tracked.Before is { } before)
            {
                item["hm"] = unchecked((int)before.Model);
                item["hx"] = Round(before.Position.X);
                item["hy"] = Round(before.Position.Y);
                item["hz"] = Round(before.Position.Z);
            }
            if (now is { } shown)
            {
                item["m"] = unchecked((int)shown.Model);
                item["x"] = Round(shown.Position.X);
                item["y"] = Round(shown.Position.Y);
                item["z"] = Round(shown.Position.Z);
                var q = Quaternion.Normalize(shown.Orientation);
                item["qx"] = Round(q.X);
                item["qy"] = Round(q.Y);
                item["qz"] = Round(q.Z);
                item["qw"] = Round(q.W);
            }
            items.Add((Vector3.DistanceSquared(at, around), item));
        }
        return [.. items.OrderBy(i => i.Distance).Take(max).Select(i => (JsonNode)i.Item)];
    }

    private static double Round(float value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
