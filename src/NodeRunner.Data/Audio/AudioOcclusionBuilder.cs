using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.Data.Audio;

// Builds the audio occlusion of a placed interior from its rooms and portals, as the game's files are made (measured on
// the game's interiors: identical content for those whose file matches their archetype).
public static class AudioOcclusionBuilder
{
    // Room 0 of every interior.
    public static readonly uint OutsideRoomKey = Joaat.Hash("outside");

    // Every pair of rooms gets its paths of 1 to 3 portals; a pair further apart gets its shortest paths, up to 5 portals.
    public const int AllPairsDepth = 3;
    public const int MaxDepth = 5;

    public const float DoorOcclusion = 0.7f;

    private const uint MirrorPortal = 0x4;
    private const uint DynamicArchetype = 0x20000;
    private const uint DoorArchetype = 0x4000000;

    public static uint RoomKey(uint proxyHash, InteriorRoom room, int index) =>
        index == 0 ? OutsideRoomKey : proxyHash ^ Joaat.Hash(room.Name);

    public static uint PathKey(uint fromRoomKey, uint toRoomKey, int depth) => fromRoomKey - toRoomKey + (uint)depth;

    public static AudioOcclusion Build(Archetype interior, uint proxyHash, Func<uint, Archetype?> types)
    {
        var rooms = interior.InteriorRooms;
        var portals = interior.InteriorPortals;
        var attached = interior.InteriorEntities.Where(e => !e.IsDeleted && e.InteriorPortal >= 0)
            .GroupBy(e => e.InteriorPortal).ToDictionary(g => g.Key, g => g.ToList());

        var links = new List<AudioPortalLink>();
        for (int room = 0; room < rooms.Count; room++)
        {
            int rank = 0; // Rank among the portals touching the room, mirrors included.
            for (int p = 0; p < portals.Count; p++)
            {
                var portal = portals[p];
                if (portal.RoomFrom != room && portal.RoomTo != room)
                    continue;
                int destination = portal.RoomFrom == room ? portal.RoomTo : portal.RoomFrom;
                if ((portal.Flags & MirrorPortal) == 0 && destination != room && destination >= 0 && destination < rooms.Count)
                    links.Add(new AudioPortalLink(proxyHash, rank, room, proxyHash, destination, Entities(attached.GetValueOrDefault(p), types)));
                rank++;
            }
        }
        return new AudioOcclusion(links, Paths(rooms, links, proxyHash));
    }

    // The objects of a portal that muffle the sound while closed: the moving ones (doors, breakable windows).
    private static List<AudioPortalEntity> Entities(List<MapEntity>? attached, Func<uint, Archetype?> types)
    {
        var entities = new List<AudioPortalEntity>();
        foreach (var entity in attached ?? [])
        {
            if (types(entity.ArchetypeHash) is not { } type || (type.Flags & DynamicArchetype) == 0)
                continue;
            bool door = (type.Flags & DoorArchetype) != 0;
            entities.Add(new AudioPortalEntity(1, door ? DoorOcclusion : 0f, entity.ArchetypeHash, door, type.AssetType == ArchetypeAssetType.Fragment));
        }
        return entities;
    }

    // Node (from, to, depth): the portals of "from" leading to "to" through at most depth portals, each with the node of
    // the rest of the way (0 when the portal leads to "to"). The outside is never crossed on the way.
    private static List<AudioPathNode> Paths(IReadOnlyList<InteriorRoom> rooms, List<AudioPortalLink> links, uint proxyHash)
    {
        int count = rooms.Count;
        var keys = new uint[count];
        for (int r = 0; r < count; r++)
            keys[r] = RoomKey(proxyHash, rooms[r], r);
        var byRoom = new List<int>[count];
        for (int r = 0; r < count; r++)
            byRoom[r] = [];
        for (int i = 0; i < links.Count; i++)
            byRoom[links[i].RoomIndex].Add(i);

        var reachable = new Dictionary<(int, int, int), bool>();
        bool Reaches(int from, int to, int depth)
        {
            if (depth < 1)
                return false;
            if (reachable.TryGetValue((from, to, depth), out bool known))
                return known;
            bool found = false;
            foreach (int i in byRoom[from])
            {
                int next = links[i].DestRoomIndex;
                if (next == to || next != 0 && Reaches(next, to, depth - 1))
                {
                    found = true;
                    break;
                }
            }
            reachable[(from, to, depth)] = found;
            return found;
        }

        var nodes = new Dictionary<(int From, int To, int Depth), List<AudioPathChild>>();
        void Add(int from, int to, int depth)
        {
            if (nodes.ContainsKey((from, to, depth)) || !Reaches(from, to, depth))
                return;
            var children = new List<AudioPathChild>();
            foreach (int i in byRoom[from])
            {
                int next = links[i].DestRoomIndex;
                if (next == to)
                    children.Add(new AudioPathChild(0, i));
                else if (next != 0 && Reaches(next, to, depth - 1))
                    children.Add(new AudioPathChild(PathKey(keys[next], keys[to], depth - 1), i));
            }
            nodes[(from, to, depth)] = children;
            foreach (int i in byRoom[from])
            {
                int next = links[i].DestRoomIndex;
                if (next != to && next != 0)
                    Add(next, to, depth - 1);
            }
        }

        for (int depth = 1; depth <= AllPairsDepth; depth++)
            for (int from = 0; from < count; from++)
                for (int to = 0; to < count; to++)
                    if (from != to)
                        Add(from, to, depth);
        for (int from = 0; from < count; from++)
            for (int to = 0; to < count; to++)
            {
                if (from == to || Reaches(from, to, AllPairsDepth))
                    continue;
                for (int depth = AllPairsDepth + 1; depth <= MaxDepth; depth++)
                    if (Reaches(from, to, depth))
                    {
                        Add(from, to, depth);
                        break;
                    }
            }

        // Nodes made only of direct portals first, then those using them, and so on; by key within each level.
        var byKey = new Dictionary<uint, List<AudioPathChild>>();
        foreach (var (node, children) in nodes)
            byKey.TryAdd(PathKey(keys[node.From], keys[node.To], node.Depth), children);
        var levels = new Dictionary<uint, int>();
        int Level(uint key)
        {
            if (levels.TryGetValue(key, out int level))
                return level;
            level = 1 + byKey[key].Max(c => c.ParentKey == 0 ? 0 : Level(c.ParentKey));
            levels[key] = level;
            return level;
        }
        return byKey.OrderBy(n => Level(n.Key)).ThenBy(n => n.Key).Select(n => new AudioPathNode(n.Key, n.Value)).ToList();
    }
}
