using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Gta4;

// The GTA IV map in the form the 3D view draws (the GTA V one): each placement file becomes a map, each definition an
// archetype, and the model files are indexed by name.
public sealed class Gta4Scene
{
    public required MapLibrary Library { get; init; }

    public required ModelCatalog Models { get; init; }

    // Texture dictionaries (".wtd") by name, with the parents of the definitions ("txdp").
    public required TextureCatalog Textures { get; init; }

    // Model definitions (".ide") and the names of the models and texture dictionaries.
    public required Gta4Definitions Definitions { get; init; }

    public required HashNames Names { get; init; }

    public static Gta4Scene Build(Gta4Map map, Gta4FileSystem files, Gta4Episode episode)
    {
        // An episode's files win over the base game's of the same name; other folders (mods, the other episode) are left out.
        string folder = Gta4Map.EpisodeFolder(episode);
        bool InGame(string path) => path.StartsWith(@"pc\", StringComparison.OrdinalIgnoreCase)
            || folder.Length > 0 && path.StartsWith(folder + @"\pc\", StringComparison.OrdinalIgnoreCase);
        List<string> Files(string extension) => files.EnumerateFiles(extension).Where(InGame)
            .OrderBy(p => p.StartsWith(@"pc\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        var drawables = Files(".wdr");
        var fragments = Files(".wft");
        var drawableNames = drawables.Select(p => Joaat.Hash(Path.GetFileNameWithoutExtension(p))).ToHashSet();
        var fragmentNames = fragments.Select(p => Joaat.Hash(Path.GetFileNameWithoutExtension(p))).ToHashSet();

        var archetypes = map.Definitions.Models.Values
            .Select(m => ToArchetype(m, m.DrawableDictionary is null && !drawableNames.Contains(m.Hash) && fragmentNames.Contains(m.Hash)))
            .Concat(map.Definitions.Interiors.Values.Select(i => ToArchetype(i, map.Definitions)))
            .ToList();
        var levels = LodLevels(map);
        var maps = new List<(MapData, string)>(map.Files.Count);
        int index = 0;
        foreach (var file in map.Files)
        {
            bool isMain = file.LodFile == file;
            var entities = new List<MapEntity>(file.Instances.Length);
            foreach (var instance in file.Instances)
            {
                var (level, children) = levels[index++];
                entities.Add(new MapEntity
                {
                    ArchetypeHash = instance.ModelHash,
                    Position = instance.Position,
                    // Stored inverted, as in the GTA V maps; an interior's is its real orientation (also as in GTA V).
                    StoredRotation = Quaternion.Conjugate(instance.Orientation),
                    IsInterior = map.Definitions.Interiors.ContainsKey(instance.ModelHash),
                    ParentIndex = instance.LodParent,
                    Flags = isMain ? 0u : LodInParentMap,
                    LodDistance = instance.LodDistance > 0f ? instance.LodDistance : 0f,
                    LodLevel = level,
                    ChildCount = children,
                });
            }
            var (min, max) = Extents(entities, map.Definitions);
            maps.Add((new MapData
            {
                NameHash = Joaat.Hash(file.Path),
                ParentHash = !isMain && file.LodFile is { } lodFile ? Joaat.Hash(lodFile.Path) : 0,
                Entities = entities,
                EntitiesExtentsMin = min,
                EntitiesExtentsMax = max,
            }, file.Path));
        }

        return new Gta4Scene
        {
            Library = MapLibrary.FromMaps(archetypes, maps),
            Models = ModelCatalog.FromFiles(drawables, Files(".wdd"), fragments),
            Textures = TextureCatalogOf(Files(".wtd"), map.Definitions),
            Definitions = map.Definitions,
            Names = NamesOf(map.Definitions),
        };
    }

    private const uint LodInParentMap = 8;

    // Space taken by the objects of a file: their positions widened by the radius of their models.
    private static (Vector3 Min, Vector3 Max) Extents(List<MapEntity> entities, Gta4Definitions definitions)
    {
        if (entities.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var entity in entities)
        {
            float radius = definitions.Find(entity.ArchetypeHash)?.SphereRadius
                ?? (definitions.Interiors.GetValueOrDefault(entity.ArchetypeHash) is { } interior ? InteriorBox(interior, definitions).Radius : 0f);
            min = Vector3.Min(min, entity.Position - new Vector3(radius));
            max = Vector3.Max(max, entity.Position + new Vector3(radius));
        }
        return (min, max);
    }

    private static HashNames NamesOf(Gta4Definitions definitions)
    {
        var names = new HashNames();
        foreach (var model in definitions.Models.Values)
        {
            names.Add(model.Name);
            names.Add(model.TextureDictionary);
        }
        foreach (var interior in definitions.Interiors.Values)
            names.Add(interior.Name);
        return names;
    }

    // Files are in order of priority, the last one wins for a name.
    private static TextureCatalog TextureCatalogOf(List<string> files, Gta4Definitions definitions)
    {
        var dictionaries = new Dictionary<uint, string>();
        foreach (var path in files)
            dictionaries[Joaat.Hash(Path.GetFileNameWithoutExtension(path))] = path;
        var parents = definitions.TextureParents.ToDictionary(p => Joaat.Hash(p.Key), p => Joaat.Hash(p.Value));
        return new TextureCatalog(dictionaries, parents);
    }

    private static Archetype ToArchetype(Gta4ModelDef model, bool fragment) => new()
    {
        NameHash = model.Hash,
        Kind = model.IsTimed ? ArchetypeKind.Time : ArchetypeKind.Base,
        TimeFlags = model.TimeFlags,
        BoundsMin = model.BoxMin,
        BoundsMax = model.BoxMax,
        SphereCenter = model.SphereCenter,
        SphereRadius = model.SphereRadius,
        LodDistance = model.DrawDistance,
        TextureDictionaryHash = Joaat.Hash(model.TextureDictionary),
        AssetType = model.DrawableDictionary is not null ? ArchetypeAssetType.DrawableDictionary
            : fragment ? ArchetypeAssetType.Fragment
            : ArchetypeAssetType.Drawable,
        AssetNameHash = model.Hash,
        DrawableDictionaryHash = model.DrawableDictionary is null ? 0 : Joaat.Hash(model.DrawableDictionary),
    };

    // An interior: its objects (rotations stored as their real orientation, unlike the placement files), rooms and
    // portals. Its simplified versions (LOD 1 and 2) stand in for the level below beyond the longest draw distance of
    // that level, as the LOD models of the map do.
    private static Archetype ToArchetype(Gta4InteriorDef interior, Gta4Definitions definitions)
    {
        var roomOf = new int[interior.Objects.Length];
        var portalOf = new int[interior.Objects.Length];
        Array.Fill(roomOf, -1);
        Array.Fill(portalOf, -1);
        for (int r = 0; r < interior.Rooms.Length; r++)
        {
            foreach (int i in interior.Rooms[r].Objects)
            {
                if (i < roomOf.Length)
                    roomOf[i] = r;
            }
        }
        for (int p = 0; p < interior.Portals.Length; p++)
        {
            foreach (int i in interior.Portals[p].Objects)
            {
                if (i < portalOf.Length)
                    portalOf[i] = p;
            }
        }

        float[] switchDistances = [0f, SwitchDistance(interior, 0, definitions), SwitchDistance(interior, 1, definitions)];

        var objects = interior.Objects.Select((o, i) => new MapEntity
        {
            ArchetypeHash = o.ModelHash,
            Position = o.Position,
            StoredRotation = Quaternion.Conjugate(Normalized(o.Rotation)),
            LodLevel = o.Lod switch
            {
                <= 0 => LodLevel.OrphanHd,
                1 => LodLevel.Lod,
                _ => LodLevel.Slod1,
            },
            ChildLodDistance = switchDistances[Math.Clamp(o.Lod, 0, 2)],
            InteriorRoom = roomOf[i],
            InteriorPortal = portalOf[i],
        }).ToList();

        var (min, max, center, radius) = InteriorBox(interior, definitions);
        return new Archetype
        {
            NameHash = interior.Hash,
            Kind = ArchetypeKind.Interior,
            BoundsMin = min,
            BoundsMax = max,
            SphereCenter = center,
            SphereRadius = radius,
            AssetType = ArchetypeAssetType.Assetless,
            InteriorEntities = objects,
            InteriorRooms = [.. interior.Rooms.Select((r, i) => new InteriorRoom(r.BoxMin, r.BoxMax)
            {
                Name = r.Name,
                TimecycleHash = r.TimecycleHash,
                SourceIndex = i,
            })],
            InteriorPortals = [.. interior.Portals.Select((p, i) => new InteriorPortal(p.RoomFrom, p.RoomTo, 0)
            {
                Corners = p.Corners,
                SourceIndex = i,
            })],
        };
    }

    // Distance at which the level above takes over from this level of an interior's objects: the longest draw
    // distance of its big pieces (walls, floors), not of a small object seen from further away (a bin), which would
    // leave a gap between the walls going and the simplified building coming.
    private static float SwitchDistance(Gta4InteriorDef interior, int lod, Gta4Definitions definitions)
    {
        var models = interior.Objects.Where(o => o.Lod == lod).Select(o => definitions.Find(o.ModelHash)).OfType<Gta4ModelDef>().ToList();
        if (models.Count == 0)
            return 0f;
        float biggest = models.Max(m => m.SphereRadius);
        return models.Where(m => m.SphereRadius >= biggest / 2f).Max(m => m.DrawDistance);
    }

    // Rooms covering the whole map (2000 km, e.g. in "playboyx") are not taken for the box.
    private const float LargestRoom = 2000f;

    // Box of an interior: its rooms but "limbo" (the outside), or its objects when it has no other room.
    public static (Vector3 Min, Vector3 Max, Vector3 Center, float Radius) InteriorBox(Gta4InteriorDef interior, Gta4Definitions definitions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var room in interior.Rooms.Skip(1))
        {
            var size = room.BoxMax - room.BoxMin;
            if (MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > LargestRoom)
                continue;
            min = Vector3.Min(min, room.BoxMin);
            max = Vector3.Max(max, room.BoxMax);
        }
        if (min.X > max.X)
        {
            foreach (var o in interior.Objects)
            {
                float r = definitions.Find(o.ModelHash)?.SphereRadius ?? 0f;
                min = Vector3.Min(min, o.Position - new Vector3(r));
                max = Vector3.Max(max, o.Position + new Vector3(r));
            }
        }
        if (min.X > max.X)
            return (Vector3.Zero, Vector3.Zero, Vector3.Zero, 0f);
        return (min, max, (min + max) / 2f, Vector3.Distance(min, max) / 2f);
    }

    private static Quaternion Normalized(Quaternion q) => q.LengthSquared() > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;

    // Level of detail of every instance (in the order of the files) from the LOD links: the height in the tree (0 =
    // detailed object, 1 = LOD, 2 and more = SLOD), and its number of children.
    private static (LodLevel Level, int Children)[] LodLevels(Gta4Map map)
    {
        var first = new Dictionary<Gta4MapFile, int>();
        int total = 0;
        foreach (var file in map.Files)
        {
            first[file] = total;
            total += file.Instances.Length;
        }
        var parents = new int[total];
        var children = new int[total];
        int index = 0;
        foreach (var file in map.Files)
        {
            foreach (var instance in file.Instances)
            {
                int parent = file.LodFile is { } lodFile && instance.LodParent >= 0 && instance.LodParent < lodFile.Instances.Length
                    ? first[lodFile] + instance.LodParent
                    : -1;
                parents[index++] = parent == index - 1 ? -1 : parent;
                if (parents[index - 1] >= 0)
                    children[parents[index - 1]]++;
            }
        }

        var heights = new int[total];
        for (int i = 0; i < total; i++)
        {
            if (children[i] > 0)
                continue;
            // From each detailed object up to its root; a broken (looping) chain stops after a few steps.
            int height = 0;
            for (int p = parents[i], steps = 0; p >= 0 && steps < 8; p = parents[p], steps++)
                heights[p] = Math.Max(heights[p], ++height);
        }

        var levels = new (LodLevel, int)[total];
        for (int i = 0; i < total; i++)
        {
            var level = heights[i] switch
            {
                0 => parents[i] >= 0 ? LodLevel.Hd : LodLevel.OrphanHd,
                1 => LodLevel.Lod,
                2 => LodLevel.Slod1,
                3 => LodLevel.Slod2,
                _ => LodLevel.Slod3,
            };
            levels[i] = (level, children[i]);
        }
        return levels;
    }
}
