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
                    // Stored inverted, as in the GTA V maps.
                    StoredRotation = Quaternion.Conjugate(instance.Orientation),
                    ParentIndex = instance.LodParent,
                    Flags = isMain ? 0u : LodInParentMap,
                    LodDistance = instance.LodDistance > 0f ? instance.LodDistance : 0f,
                    LodLevel = level,
                    ChildCount = children,
                });
            }
            maps.Add((new MapData
            {
                NameHash = Joaat.Hash(file.Path),
                ParentHash = !isMain && file.LodFile is { } lodFile ? Joaat.Hash(lodFile.Path) : 0,
                Entities = entities,
            }, file.Path));
        }

        return new Gta4Scene
        {
            Library = MapLibrary.FromMaps(archetypes, maps),
            Models = ModelCatalog.FromFiles(drawables, Files(".wdd"), fragments),
            Textures = TextureCatalogOf(Files(".wtd"), map.Definitions),
        };
    }

    private const uint LodInParentMap = 8;

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
