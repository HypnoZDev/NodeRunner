using System.Collections.Concurrent;
using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Classic;

// The map of GTA III, Vice City or San Andreas in the form the 3D view draws (the GTA V one): each placement file
// becomes a map, each definition an archetype, and the model and texture files are indexed by name.
public sealed class ClassicScene
{
    public required MapLibrary Library { get; init; }

    public required ModelCatalog Models { get; init; }

    public required TextureCatalog Textures { get; init; }

    public required ClassicDefinitions Definitions { get; init; }

    public required HashNames Names { get; init; }

    // Placed models whose bounds could not be found (no collision, unreadable model).
    public int MissingBounds { get; private init; }

    private const uint LodInParentMap = 8;

    // GTA III / Vice City: furthest a low detail building may stand from the detailed one it replaces.
    private const float LodSearchDistance = 100f;

    public static ClassicScene Build(ClassicMap map, ClassicFileSystem files)
    {
        var definitions = map.Definitions;
        // The main archives last: their files win over the copies of other archives (cutscenes, player).
        static int Rank(string path) => path.StartsWith(@"models\gta3.img\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"models\gta_int.img\", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var dffs = files.EnumerateFiles(".dff").OrderBy(Rank).ToList();
        var models = ModelCatalog.FromFiles(dffs, [], []);

        var placed = map.Files.SelectMany(f => f.Instances).Select(i => i.ModelId).ToHashSet();
        var bounds = ReadBounds(map, files, dffs, placed, out int missing);

        var archetypes = definitions.Models.Values.Select(d => ToArchetype(d, bounds.GetValueOrDefault(d.Name))).ToList();
        var radii = new Dictionary<uint, float>();
        foreach (var archetype in archetypes)
            radii[archetype.NameHash] = archetype.SphereRadius;
        var parents = LodParents(map);
        var levels = LodLevels(parents);

        var first = new Dictionary<ClassicMapFile, int>();
        int total = 0;
        foreach (var file in map.Files)
        {
            first[file] = total;
            total += file.Instances.Length;
        }
        var fileOf = new ClassicMapFile[total];
        foreach (var file in map.Files)
            Array.Fill(fileOf, file, first[file], file.Instances.Length);

        var maps = new List<(MapData, string)>(map.Files.Count);
        int index = 0;
        foreach (var file in map.Files)
        {
            var entities = new List<MapEntity>(file.Instances.Length);
            bool inParent = false;
            foreach (var instance in file.Instances)
            {
                int global = index++;
                // Kept even if unknown, so the LOD numbers of the others still match.
                var definition = definitions.Find(instance.ModelId);
                int parent = parents[global];
                var parentFile = parent >= 0 ? fileOf[parent] : null;
                inParent |= parentFile is not null && parentFile != file;
                var (level, children) = levels[global];
                entities.Add(new MapEntity
                {
                    ArchetypeHash = definition is null ? 0 : Joaat.Hash(definition.Name),
                    Position = instance.Position,
                    // Stored inverted, as in the GTA V maps.
                    StoredRotation = Quaternion.Conjugate(instance.Rotation),
                    Scale = instance.Scale,
                    ParentIndex = parentFile is null ? -1 : parent - first[parentFile],
                    Flags = parentFile is not null && parentFile != file ? LodInParentMap : 0u,
                    LodDistance = definition?.DrawDistance ?? 0f,
                    LodLevel = level,
                    ChildCount = children,
                });
            }
            var (min, max) = Extents(entities, radii);
            maps.Add((new MapData
            {
                NameHash = Joaat.Hash(file.Path),
                ParentHash = inParent && file.LodFile is { } lodFile && lodFile != file ? Joaat.Hash(lodFile.Path) : 0,
                Entities = entities,
                EntitiesExtentsMin = min,
                EntitiesExtentsMax = max,
            }, file.Path));
        }

        return new ClassicScene
        {
            Library = MapLibrary.FromMaps(archetypes, maps),
            Models = models,
            Textures = TextureCatalogOf(files, definitions),
            Definitions = definitions,
            Names = NamesOf(definitions),
            MissingBounds = missing,
        };
    }

    // Bounds of each model from the collision files; the placed models without collision (most San Andreas low
    // detail buildings) are measured on their model.
    private static Dictionary<string, ClassicBounds> ReadBounds(ClassicMap map, ClassicFileSystem files, List<string> dffs,
        HashSet<int> placed, out int missing)
    {
        var bounds = new Dictionary<string, ClassicBounds>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files.EnumerateFiles(".col").Concat(map.CollisionFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (files.ReadFile(path) is not { } data)
                continue;
            foreach (var b in ClassicCollisions.ReadBounds(data))
                bounds[b.Model] = b;
        }

        var dffByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in dffs)
            dffByName[Path.GetFileNameWithoutExtension(path)] = path;
        var toMeasure = placed.Select(map.Definitions.Find).OfType<ClassicModelDef>()
            .Where(d => !bounds.ContainsKey(d.Name)).ToList();
        var measured = new ConcurrentBag<ClassicBounds>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        Parallel.ForEach(toMeasure, options, definition =>
        {
            if (!dffByName.TryGetValue(definition.Name, out var path) || files.ReadFile(path) is not { } data)
                return;
            try
            {
                if (ClassicModels.ReadClump(data, 0) is { } model)
                {
                    var center = (model.BoundsMin + model.BoundsMax) / 2f;
                    measured.Add(new ClassicBounds(definition.Name, model.BoundsMin, model.BoundsMax, center,
                        Vector3.Distance(model.BoundsMin, model.BoundsMax) / 2f));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                // Left without bounds.
            }
        });
        foreach (var b in measured)
            bounds[b.Model] = b;
        missing = toMeasure.Count - measured.Count;
        return bounds;
    }

    private static Archetype ToArchetype(ClassicModelDef model, ClassicBounds bounds) => new()
    {
        NameHash = Joaat.Hash(model.Name),
        Kind = model.IsTimed ? ArchetypeKind.Time : ArchetypeKind.Base,
        TimeFlags = model.IsTimed ? Hours(model.TimeOn, model.TimeOff) : 0,
        BoundsMin = bounds.Min,
        BoundsMax = bounds.Max,
        SphereCenter = bounds.Center,
        SphereRadius = bounds.Radius,
        LodDistance = model.DrawDistance,
        TextureDictionaryHash = Joaat.Hash(model.TextureDictionary),
        AssetType = ArchetypeAssetType.Drawable,
        AssetNameHash = Joaat.Hash(model.Name),
    };

    // One bit per hour shown, from "on" up to "off" (past midnight when on > off).
    public static uint Hours(int on, int off)
    {
        on = Math.Clamp(on, 0, 24) % 24;
        off = Math.Clamp(off, 0, 24) % 24;
        if (on == off)
            return 0xFFFFFF;
        uint flags = 0;
        for (int h = on; h != off; h = (h + 1) % 24)
            flags |= 1u << h;
        return flags;
    }

    // Index (over all files, in order) of the lower detail instance of every instance, -1 when none:
    // - San Andreas: the "lod" number, in the file itself or in the main file of a streamed one;
    // - GTA III / Vice City: the nearest instance of the "LOD" model of the same name in the same file.
    private static int[] LodParents(ClassicMap map)
    {
        var first = new Dictionary<ClassicMapFile, int>();
        int total = 0;
        foreach (var file in map.Files)
        {
            first[file] = total;
            total += file.Instances.Length;
        }
        var parents = new int[total];
        Array.Fill(parents, -1);
        foreach (var file in map.Files)
        {
            int start = first[file];
            if (map.Game == ClassicGame.SanAndreas)
            {
                if (file.LodFile is not { } lodFile)
                    continue;
                for (int i = 0; i < file.Instances.Length; i++)
                {
                    int lod = file.Instances[i].Lod;
                    if (lod >= 0 && lod < lodFile.Instances.Length && !(lodFile == file && lod == i))
                        parents[start + i] = first[lodFile] + lod;
                }
                continue;
            }
            var lods = new Dictionary<int, List<int>>();
            for (int i = 0; i < file.Instances.Length; i++)
            {
                if (map.Definitions.Find(file.Instances[i].ModelId) is { } d && ClassicDefinitions.IsLodName(d.Name))
                {
                    if (!lods.TryGetValue(d.Id, out var list))
                        lods[d.Id] = list = [];
                    list.Add(i);
                }
            }
            for (int i = 0; i < file.Instances.Length; i++)
            {
                if (map.Definitions.Find(file.Instances[i].ModelId) is not { } d || map.Definitions.LodModelOf(d) is not { } lodModel
                    || !lods.TryGetValue(lodModel.Id, out var candidates))
                    continue;
                var position = file.Instances[i].Position;
                int best = candidates.MinBy(c => Vector3.DistanceSquared(file.Instances[c].Position, position));
                if (Vector3.Distance(file.Instances[best].Position, position) <= LodSearchDistance)
                    parents[start + i] = start + best;
            }
        }
        return parents;
    }

    // Level of detail of every instance from the LOD links: height in the tree (0 = detailed object, 1 = LOD...), and
    // its number of children.
    private static (LodLevel Level, int Children)[] LodLevels(int[] parents)
    {
        int total = parents.Length;
        var children = new int[total];
        foreach (int p in parents)
            if (p >= 0)
                children[p]++;
        var heights = new int[total];
        for (int i = 0; i < total; i++)
        {
            if (children[i] > 0)
                continue;
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

    private static (Vector3 Min, Vector3 Max) Extents(List<MapEntity> entities, Dictionary<uint, float> radii)
    {
        if (entities.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var entity in entities)
        {
            float radius = radii.GetValueOrDefault(entity.ArchetypeHash) * MathF.Max(entity.Scale.X, MathF.Max(entity.Scale.Y, entity.Scale.Z));
            min = Vector3.Min(min, entity.Position - new Vector3(radius));
            max = Vector3.Max(max, entity.Position + new Vector3(radius));
        }
        return (min, max);
    }

    private static HashNames NamesOf(ClassicDefinitions definitions)
    {
        var names = new HashNames();
        foreach (var model in definitions.Models.Values)
        {
            names.Add(model.Name);
            names.Add(model.TextureDictionary);
        }
        return names;
    }

    // Archives, then the loose dictionaries of the game folder ("models\generic.txd", San Andreas
    // "models\generic\vehicle.txd"); the later wins for a name.
    private static TextureCatalog TextureCatalogOf(ClassicFileSystem files, ClassicDefinitions definitions)
    {
        var dictionaries = new Dictionary<uint, string>();
        foreach (string path in files.EnumerateFiles(".txd"))
            dictionaries[Joaat.Hash(Path.GetFileNameWithoutExtension(path))] = path;
        if (files.Folder is { } folder)
        {
            foreach (string sub in new[] { "models", Path.Combine("models", "generic") })
            {
                string dir = Path.Combine(folder, sub);
                if (!Directory.Exists(dir))
                    continue;
                foreach (string path in Directory.EnumerateFiles(dir, "*.txd"))
                    dictionaries[Joaat.Hash(Path.GetFileNameWithoutExtension(path))] = Path.GetRelativePath(folder, path);
            }
        }
        var parents = definitions.TextureParents.ToDictionary(p => Joaat.Hash(p.Key), p => Joaat.Hash(p.Value));
        // Vehicles share the textures of "vehicle.txd" (San Andreas: lights, plates, glass) or of the wheels (Vice City
        // "wheels.txd", GTA III "misc.txd").
        uint shared = new[] { "vehicle", "wheels", "misc" }.Select(Joaat.Hash).FirstOrDefault(dictionaries.ContainsKey);
        if (shared != 0)
        {
            foreach (var vehicle in definitions.Vehicles)
                parents.TryAdd(Joaat.Hash(vehicle.TextureDictionary), shared);
        }
        return new TextureCatalog(dictionaries, parents);
    }
}
