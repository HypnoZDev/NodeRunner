using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Collisions;

/// <summary>Which version of the collision a file holds (the game keeps several for the same place).</summary>
public enum CollisionKind
{
    /// <summary>No prefix: the collision of peds, objects and weapons.</summary>
    Standard,

    /// <summary>"hi@" prefix: more detailed collision used by vehicles.</summary>
    Vehicle,

    /// <summary>"ma@" prefix: extra collision, purpose not documented.</summary>
    Other,
}

/// <summary>A map collision file and the world box it covers.</summary>
public sealed record CollisionFile(string Path, string Name, Vector3 BoundsMin, Vector3 BoundsMax, CollisionKind Kind);

/// <summary>World box of a collision file, as listed by the game's cache files (the file is named by its hash).</summary>
public readonly record struct CollisionBox(uint NameHash, Vector3 Min, Vector3 Max);

/// <summary>Reads the list of collision boxes of a map cache file (gta5_cache_y.dat, &lt;pack&gt;_..._cache_y.dat).</summary>
public interface IMapCacheReader
{
    /// <exception cref="InvalidDataException">The data is not a valid cache file.</exception>
    IReadOnlyList<CollisionBox> ReadCollisionBoxes(ReadOnlySpan<byte> data);
}

/// <summary>
/// Knows which map collision file (.ybn) covers which part of the map, and reads them on demand.
/// The game does not compute this at run time: it reads the box of every file from its cache files, which
/// NodeRunner reads too. Collision files of interiors are relative to the interior: they are kept apart
/// (<see cref="FindInteriorFile"/>), placed with each instance of the interior.
/// </summary>
public sealed class CollisionCatalog
{
    private const float CellSize = 200f;
    private const string CacheFileSuffix = "_cache_y.dat";

    private readonly ICollisionReader _reader;
    private readonly List<CollisionFile> _files = new();
    private readonly Dictionary<(int X, int Y), List<CollisionFile>> _cells = new();
    private readonly Dictionary<uint, string> _interiorFiles = new();

    private CollisionCatalog(ICollisionReader reader)
    {
        _reader = reader;
    }

    /// <summary>Map collision files (interiors not included).</summary>
    public IReadOnlyList<CollisionFile> Files => _files;

    /// <summary>Collision files the cache files do not place (cannot be shown).</summary>
    public int UnplacedCount { get; private set; }

    public int InteriorFileCount => _interiorFiles.Count;

    /// <summary>Problems met while reading the cache files.</summary>
    public List<string> Errors { get; } = new();

    /// <param name="interiorNames">Name hashes of the interior types (MLO): their collision files are relative to them.</param>
    public static CollisionCatalog Create(GameContent content, ICollisionReader reader, IMapCacheReader cacheReader,
        IReadOnlySet<uint> interiorNames)
    {
        var catalog = new CollisionCatalog(reader);
        catalog.Materials = ReadMaterials(content);

        // Every version of the cache files, oldest first: a newer box (update, DLC pack) replaces an older one.
        var boxes = new Dictionary<uint, CollisionBox>();
        foreach (var path in content.EnumerateAllVersions(".dat"))
        {
            if (!path.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var data = content.ReadFile(path);
                if (data is null)
                    continue;
                foreach (var box in cacheReader.ReadCollisionBoxes(data))
                    boxes[box.NameHash] = box;
            }
            catch (InvalidDataException ex)
            {
                catalog.Errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }

        foreach (var path in content.EnumerateFiles(".ybn"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            uint hash = Joaat.Hash(name);
            if (interiorNames.Contains(StripPrefix(name, out _)))
            {
                // Several versions (standard, hi@...) per interior: the standard one is used.
                if (KindOf(name) == CollisionKind.Standard)
                    catalog._interiorFiles[Joaat.Hash(name)] = path;
                continue;
            }
            if (!boxes.TryGetValue(hash, out var placed))
            {
                catalog.UnplacedCount++;
                continue;
            }
            catalog.Add(new CollisionFile(path, name, placed.Min, placed.Max, KindOf(name)));
        }
        return catalog;
    }

    /// <summary>Names of the materials (read from the game's materials.dat), empty if not found.</summary>
    public CollisionMaterials Materials { get; private set; } = CollisionMaterials.Empty;

    private static CollisionMaterials ReadMaterials(GameContent content)
    {
        var path = content.EnumerateFiles(".dat")
            .FirstOrDefault(p => Path.GetFileName(p).Equals(CollisionMaterials.FileName, StringComparison.OrdinalIgnoreCase));
        var data = path is null ? null : content.ReadFile(path);
        return data is null ? CollisionMaterials.Empty : CollisionMaterials.Parse(System.Text.Encoding.UTF8.GetString(data));
    }

    /// <summary>Collision file of an interior type (relative to the interior), or null.</summary>
    public string? FindInteriorFile(uint interiorNameHash) => _interiorFiles.GetValueOrDefault(interiorNameHash);

    /// <summary>Map collision files of this kind whose box overlaps the given box.</summary>
    public IEnumerable<CollisionFile> FilesIn(Vector3 min, Vector3 max, CollisionKind kind = CollisionKind.Standard)
    {
        var seen = new HashSet<CollisionFile>();
        foreach (var cell in CellsOf(new Vector2(min.X, min.Y), new Vector2(max.X, max.Y)))
        {
            if (!_cells.TryGetValue(cell, out var files))
                continue;
            foreach (var file in files)
            {
                if (file.Kind == kind && Overlaps(file, min, max) && seen.Add(file))
                    yield return file;
            }
        }
    }

    /// <summary>Map collision files of this kind within <paramref name="radius"/> of a point (on the ground plane), nearest first.</summary>
    public IEnumerable<CollisionFile> FilesAround(Vector2 center, float radius, CollisionKind kind = CollisionKind.Standard)
    {
        var min = new Vector3(center - new Vector2(radius), float.MinValue);
        var max = new Vector3(center + new Vector2(radius), float.MaxValue);
        return FilesIn(min, max, kind)
            .Select(f => (File: f, Distance: DistanceToBox(center, f)))
            .Where(f => f.Distance <= radius)
            .OrderBy(f => f.Distance)
            .Select(f => f.File);
    }

    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public CollisionMesh? Read(CollisionFile file) => _reader.Read(file.Path);

    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public CollisionMesh? Read(string path) => _reader.Read(path);

    /// <summary>The file without these polygons (see <see cref="ICollisionWriter"/>).</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    /// <exception cref="NotSupportedException">The reader cannot write files.</exception>
    public byte[]? RemovePolygons(CollisionFile file, IReadOnlySet<int> polygons) =>
        (_reader as ICollisionWriter ?? throw new NotSupportedException()).RemovePolygons(file.Path, polygons);

    public static CollisionKind KindOf(string fileName)
    {
        StripPrefix(fileName, out var kind);
        return kind;
    }

    /// <summary>Hash of the name without its "hi@" / "ma@" prefix.</summary>
    private static uint StripPrefix(string name, out CollisionKind kind)
    {
        kind = name.StartsWith("hi@", StringComparison.OrdinalIgnoreCase) ? CollisionKind.Vehicle
            : name.StartsWith("ma@", StringComparison.OrdinalIgnoreCase) ? CollisionKind.Other
            : CollisionKind.Standard;
        return Joaat.Hash(kind == CollisionKind.Standard ? name : name[3..]);
    }

    private void Add(CollisionFile file)
    {
        _files.Add(file);
        foreach (var cell in CellsOf(new Vector2(file.BoundsMin.X, file.BoundsMin.Y), new Vector2(file.BoundsMax.X, file.BoundsMax.Y)))
        {
            if (!_cells.TryGetValue(cell, out var files))
                _cells[cell] = files = new List<CollisionFile>();
            files.Add(file);
        }
    }

    private static IEnumerable<(int X, int Y)> CellsOf(Vector2 min, Vector2 max)
    {
        // Clamped: a query with an unbounded box (or a damaged file box) must not loop forever.
        const int Limit = 100; // 20 km around the origin, larger than the map
        int x0 = Math.Clamp((int)MathF.Floor(min.X / CellSize), -Limit, Limit);
        int x1 = Math.Clamp((int)MathF.Floor(max.X / CellSize), -Limit, Limit);
        int y0 = Math.Clamp((int)MathF.Floor(min.Y / CellSize), -Limit, Limit);
        int y1 = Math.Clamp((int)MathF.Floor(max.Y / CellSize), -Limit, Limit);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
                yield return (x, y);
        }
    }

    private static bool Overlaps(CollisionFile file, Vector3 min, Vector3 max) =>
        file.BoundsMin.X <= max.X && file.BoundsMax.X >= min.X
        && file.BoundsMin.Y <= max.Y && file.BoundsMax.Y >= min.Y
        && file.BoundsMin.Z <= max.Z && file.BoundsMax.Z >= min.Z;

    private static float DistanceToBox(Vector2 point, CollisionFile file)
    {
        var closest = Vector2.Clamp(point, new Vector2(file.BoundsMin.X, file.BoundsMin.Y), new Vector2(file.BoundsMax.X, file.BoundsMax.Y));
        return Vector2.Distance(point, closest);
    }
}
