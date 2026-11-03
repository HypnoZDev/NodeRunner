using System.Numerics;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Maps;

/// <summary>
/// Every placed object of the map (.ymap) and every object type (.ytyp), with a grid index to quickly
/// find the entities near a position.
/// </summary>
public sealed class MapLibrary
{
    /// <summary>Size of the cells of the spatial index, in metres.</summary>
    public const float CellSize = 100f;

    private readonly Dictionary<uint, Archetype> _archetypes = new();
    private readonly List<MapData> _maps = new();
    private readonly Dictionary<(int X, int Y), List<MapEntity>> _cells = new();

    public IReadOnlyList<MapData> Maps => _maps;
    public int ArchetypeCount => _archetypes.Count;

    /// <summary>Name hashes of every object type (a new set at each call).</summary>
    public HashSet<uint> GetArchetypeHashes() => _archetypes.Keys.ToHashSet();

    /// <summary>Name hashes of the interior types (MLO).</summary>
    public HashSet<uint> GetInteriorArchetypeHashes() =>
        _archetypes.Values.Where(a => a.Kind == ArchetypeKind.Interior).Select(a => a.NameHash).ToHashSet();
    public int EntityCount { get; private set; }

    /// <summary>Files in another format (XML, PSO) or damaged, skipped with the reason.</summary>
    public List<string> Errors { get; } = new();

    public Archetype? GetArchetype(uint nameHash) => _archetypes.GetValueOrDefault(nameHash);

    /// <summary>The interior types (MLO) that have an entity set of this name hash.</summary>
    public List<uint> FindEntitySetInteriors(uint setHash) =>
        _archetypes.Values.Where(a => a.InteriorEntitySets.Any(s => s.NameHash == setHash)).Select(a => a.NameHash).ToList();

    /// <summary>Path of the .ytyp file defining an object type, null if unknown.</summary>
    public string? GetArchetypeFile(uint nameHash) => _archetypeFiles.GetValueOrDefault(nameHash);
    private readonly Dictionary<uint, string> _archetypeFiles = new();

    /// <summary>
    /// Top-level entities (no parent) seen from further than <see cref="NearRootDistance"/>: big LOD/SLOD
    /// models covering whole districts. They are always checked; smaller roots are found with the grid.
    /// </summary>
    public IReadOnlyList<MapEntity> FarRoots => _farRoots;
    private readonly List<MapEntity> _farRoots = new();

    /// <summary>Top-level entities with a draw distance up to this value are only looked up near the camera.</summary>
    public const float NearRootDistance = 1000f;

    /// <summary>Distance up to which an entity is drawn: its own value, or its archetype's if not set.</summary>
    public float DrawDistance(MapEntity entity) =>
        entity.LodDistance > 0f ? entity.LodDistance : GetArchetype(entity.ArchetypeHash)?.LodDistance ?? 0f;

    /// <summary>Entities whose position is within <paramref name="radius"/> of <paramref name="center"/> (horizontally).</summary>
    public IEnumerable<MapEntity> EntitiesAround(Vector2 center, float radius)
    {
        var min = CellOf(center - new Vector2(radius));
        var max = CellOf(center + new Vector2(radius));
        float radiusSquared = radius * radius;
        for (int y = min.Y; y <= max.Y; y++)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                if (!_cells.TryGetValue((x, y), out var entities))
                    continue;
                foreach (var entity in entities)
                {
                    if (!entity.IsDeleted && Vector2.DistanceSquared(new Vector2(entity.Position.X, entity.Position.Y), center) <= radiusSquared)
                        yield return entity;
                }
            }
        }
    }

    /// <summary>
    /// Incremented when entities are deleted or brought back (<see cref="SetDeleted"/>): what was computed from the
    /// list of entities (objects to draw, search index...) must be computed again.
    /// </summary>
    public int StructureVersion { get; private set; }

    /// <summary>Entities deleted in the editor (not counting the ones of saved files, already left out).</summary>
    public int DeletedCount { get; private set; }

    /// <summary>Deletes entities (or brings them back): they stay in memory, so that the deletion can be undone.</summary>
    public void SetDeleted(IEnumerable<MapEntity> entities, bool deleted)
    {
        foreach (var entity in entities)
        {
            if (entity.IsDeleted != deleted)
                DeletedCount += deleted ? 1 : -1;
            entity.IsDeleted = deleted;
        }
        StructureVersion++;
    }

    /// <summary>Shows or hides an entity set of an interior instance (as a script would activate it in the game).</summary>
    public void SetEntitySetActive(PlacedEntitySet set, bool active)
    {
        if (set.IsActive == active)
            return;
        set.IsActive = active;
        StructureVersion++;
    }

    /// <summary>
    /// A new map made by the user (see <see cref="MapData.IsCustom"/>), empty, named after its file
    /// (<paramref name="fileName"/>, e.g. "hopital_props.ymap"): the internal name is the hash of the name without extension.
    /// </summary>
    public MapData CreateCustomMap(string fileName)
    {
        var map = new MapData
        {
            NameHash = Hashing.Joaat.Hash(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant()),
            Entities = [],
            FilePath = fileName,
            IsCustom = true,
        };
        Add(map);
        StructureVersion++;
        return map;
    }

    /// <summary>The custom map saved or made under this file name, null if none.</summary>
    public MapData? FindCustomMap(string fileName) =>
        _maps.Find(m => m.IsCustom && string.Equals(m.FilePath, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds an object to a map (an object added in the editor: <see cref="MapEntity.SourceIndex"/> -1).</summary>
    public void AddEntity(MapData map, MapEntity entity)
    {
        map.AddEntity(entity);
        entity.Map = map;
        AddToCell(entity);
        EntityCount++;
        StructureVersion++;
    }

    /// <summary>
    /// Maps whose entities may have their lower-detail parent in <paramref name="map"/> (their parent map): the parent
    /// indices they store change when entities of <paramref name="map"/> are deleted.
    /// </summary>
    public IEnumerable<MapData> ChildMaps(MapData map) => _maps.Where(m => m.ParentHash == map.NameHash && m != map);

    /// <summary>The map holding the lower-detail parents of <paramref name="map"/>'s entities, or null.</summary>
    public MapData? ParentMap(MapData map) =>
        map.ParentHash != 0 ? _maps.FirstOrDefault(m => m.NameHash == map.ParentHash && m != map) : null;

    /// <summary>
    /// Whether an entity of <paramref name="map"/> has fewer children left than its file declares (children deleted now,
    /// or missing from a saved child map): the file must be rewritten with the new count, otherwise the game keeps
    /// showing the simplified model even up close (it waits for every declared child).
    /// </summary>
    public static bool HasLostChildren(MapData map) => map.Entities.Any(HasLostChildren);

    /// <inheritdoc cref="HasLostChildren(MapData)"/>
    public static bool HasLostChildren(MapEntity entity) =>
        !entity.IsDeleted && entity.Children.Count > 0 && entity.Children.Count(c => !c.IsDeleted) < entity.ChildCount;

    /// <summary>
    /// File names (without extension) of the maps only loaded by scripts and missions (see <see cref="MapData.IsScripted"/>)
    /// that can be switched on or off, sorted. Their simplified versions (parent maps, also loaded by scripts) are not
    /// listed: they come with them.
    /// </summary>
    public IReadOnlyList<string> ScriptedMapNames { get; private set; } = [];

    /// <summary>
    /// The <see cref="ScriptedMapNames"/> hidden when the user has not chosen: alternative versions of a place
    /// shown by another map (see <see cref="ScriptedMapVariants"/>).
    /// </summary>
    public IReadOnlySet<string> DefaultHiddenScriptedMaps { get; private set; } = new HashSet<string>();

    /// <summary>
    /// Reads every .ytyp and .ymap the game uses: base game, update and DLC packs (see <see cref="GameContent"/>).
    /// Maps loaded by scripts only are added too, as CodeWalker does, except <paramref name="hiddenScriptedMaps"/>
    /// (file names without extension; null = <see cref="DefaultHiddenScriptedMaps"/>). Each one comes with its
    /// parent maps and replaces a map with the same internal name.
    /// </summary>
    public static MapLibrary Load(GameContent content, IMapTypesReader typesReader, IMapDataReader mapReader,
        IProgress<string>? progress = null, IReadOnlySet<string>? hiddenScriptedMaps = null,
        IReadOnlyDictionary<string, byte[]>? savedMaps = null)
    {
        var library = new MapLibrary();
        var unusedSaved = savedMaps?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        // Object types are definitions looked up by name: take them from every archive (the active version
        // first). Placed objects (ymaps) only come from active archives.
        var typeFiles = content.EnumerateFiles(".ytyp", withInactiveFallback: true);
        int done = 0;
        foreach (var path in typeFiles)
        {
            if (++done % 100 == 0)
                progress?.Report($"Lecture des types d'objets : {done} / {typeFiles.Count} fichiers");
            var types = ReadOrNull(content, path, typesReader.Read, library.Errors);
            foreach (var archetype in types?.Archetypes ?? [])
            {
                library._archetypes[archetype.NameHash] = archetype;
                library._archetypeFiles[archetype.NameHash] = path;
            }
        }

        // Some ymaps share the internal name of another one under a different file name (e.g. hei_dt1_02 for dt1_02):
        // they are variants that missions switch on, not loaded by default. Keep the original, whose file name
        // matches its internal name; the children ymaps' parent indices refer to it.
        var mapFiles = content.EnumerateFiles(".ymap");
        var byName = new Dictionary<uint, (MapData Map, string Path)>();
        var scripted = new List<(MapData Map, string Path)>();
        done = 0;
        foreach (var path in mapFiles)
        {
            if (++done % 250 == 0)
                progress?.Report($"Lecture des objets de la carte : {done} / {mapFiles.Count} fichiers");
            var map = ReadOrNull(content, path, mapReader.Read, library.Errors);
            if (map is null)
                continue;
            if (savedMaps is not null && savedMaps.TryGetValue(Path.GetFileName(path), out var saved))
            {
                unusedSaved.Remove(Path.GetFileName(path));
                map = library.UseSaved(map, saved, mapReader, Path.GetFileName(path));
            }
            if (map.IsScripted)
                scripted.Add((map, path));
            else if (!byName.TryGetValue(map.NameHash, out var existing) || IsOriginal(map, path) && !IsOriginal(existing.Map, existing.Path))
                byName[map.NameHash] = (map, path);
        }
        bool withMansions = content.Packs.Contains(ScriptedMapVariants.MansionsPack, StringComparer.OrdinalIgnoreCase);
        library.AddScriptedMaps(scripted, byName, hiddenScriptedMaps, withMansions);

        foreach (var (map, path) in byName.Values)
        {
            map.FilePath = path;
            library.Add(map);
        }

        // Saved maps without a game file of that name (in any archive): the user's own maps, added to the game's.
        var gameMapNames = unusedSaved.Count == 0 ? [] : content.EnumerateFiles(".ymap", withInactiveFallback: true)
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in unusedSaved)
        {
            if (gameMapNames.Contains(name))
            {
                library.SavedMapErrors.Add($"{name} : aucun YMAP de ce nom dans la carte chargée (autre version de la carte ou DLC ?)");
                continue;
            }
            try
            {
                var custom = mapReader.Read(savedMaps![name]);
                custom.FilePath = name;
                custom.IsCustom = true;
                custom.IsSaved = true;
                custom.SavedContent = savedMaps[name];
                library.Add(custom);
            }
            catch (InvalidDataException ex)
            {
                library.SavedMapErrors.Add($"{name} : {ex.Message}");
            }
        }

        library.LinkLodTree();
        library.MarkWindowViews();
        library.PlaceInteriorContents();
        return library;
    }

    /// <summary>A library of the given maps (no object types), with the level-of-detail tree linked: for tests.</summary>
    internal static MapLibrary Create(params (MapData Map, string Path)[] maps)
    {
        var library = new MapLibrary();
        foreach (var (map, path) in maps)
        {
            map.FilePath = path;
            library.Add(map);
        }
        library.LinkLodTree();
        return library;
    }

    /// <summary>Problems with the maps of the user's save folder (files left unused, the game's file is shown instead).</summary>
    public List<string> SavedMapErrors { get; } = new();

    /// <summary>
    /// The version of a map saved by the user, if it was made from this game file: same internal name and same parent
    /// map (objects may have been moved, deleted or added). Otherwise the game's version, and a note.
    /// </summary>
    private MapData UseSaved(MapData original, byte[] saved, IMapDataReader reader, string name)
    {
        try
        {
            var map = reader.Read(saved);
            if (map.NameHash == original.NameHash && map.ParentHash == original.ParentHash)
            {
                map.IsSaved = true;
                map.SavedContent = saved;
                return map;
            }
            SavedMapErrors.Add($"{name} : ne correspond plus au fichier du jeu (autre nom interne ou autre YMAP parent), version du jeu affichée");
        }
        catch (InvalidDataException ex)
        {
            SavedMapErrors.Add($"{name} : {ex.Message}");
        }
        return original;
    }

    /// <summary>
    /// Lists the maps loaded by scripts, and adds the ones not hidden to <paramref name="byName"/>, followed by
    /// their parent maps (simplified versions) when those are loaded by scripts too.
    /// </summary>
    private void AddScriptedMaps(List<(MapData Map, string Path)> scripted, Dictionary<uint, (MapData Map, string Path)> byName,
        IReadOnlySet<string>? hidden, bool withMansions)
    {
        static string FileName(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        var parentsOfScripted = scripted.Select(s => s.Map.ParentHash).ToHashSet();
        var listed = scripted
            .Where(s => !parentsOfScripted.Contains(s.Map.NameHash))
            .DistinctBy(s => FileName(s.Path))
            .ToList();
        ScriptedMapNames = listed.Select(s => FileName(s.Path)).Order(StringComparer.Ordinal).ToList();
        DefaultHiddenScriptedMaps = ScriptedMapVariants.DefaultHidden(
            listed.Select(s => (FileName(s.Path), s.Map)).ToList(), byName.Values.Select(m => m.Map), withMansions);
        hidden ??= DefaultHiddenScriptedMaps;

        var scriptedByName = new Dictionary<uint, (MapData Map, string Path)>();
        foreach (var s in scripted)
            scriptedByName.TryAdd(s.Map.NameHash, s);

        var pending = new Queue<(MapData Map, string Path)>(listed.Where(s => !hidden.Contains(FileName(s.Path))));
        while (pending.TryDequeue(out var next))
        {
            byName[next.Map.NameHash] = next;
            uint parent = next.Map.ParentHash;
            if (parent != 0 && !byName.ContainsKey(parent) && scriptedByName.TryGetValue(parent, out var parentMap))
                pending.Enqueue(parentMap);
        }
    }

    /// <summary>Distance beyond the openings of an interior from which an object of it is a view through the windows.</summary>
    private const float WindowViewMargin = 100f;

    /// <summary>
    /// Finds the simplified views of the surroundings held by some interiors (<see cref="MapEntity.IsWindowView"/>):
    /// objects of a room that hides the real outside (<see cref="InteriorRoom.HidesExterior"/>) lying far beyond every
    /// opening (portal) of the interior. In the game they are only seen through the windows from inside; drawn from
    /// outside, they would cover the real map (the psychiatrist's office covers the Del Perro beach, 400 m away).
    /// </summary>
    private void MarkWindowViews()
    {
        foreach (var archetype in _archetypes.Values)
        {
            if (!archetype.InteriorRooms.Any(r => r.HidesExterior))
                continue;
            var corners = archetype.InteriorPortals.SelectMany(p => p.Corners).ToList();
            if (corners.Count == 0)
                continue;
            var min = corners.Aggregate(Vector3.Min) - new Vector3(WindowViewMargin);
            var max = corners.Aggregate(Vector3.Max) + new Vector3(WindowViewMargin);

            foreach (var entity in archetype.InteriorEntities)
            {
                int room = entity.InteriorRoom;
                if (room < 0 || room >= archetype.InteriorRooms.Count || !archetype.InteriorRooms[room].HidesExterior
                    || GetArchetype(entity.ArchetypeHash) is not { } model)
                    continue;
                var center = Vector3.Transform(model.SphereCenter, entity.Transform);
                entity.IsWindowView = Vector3.Clamp(center, min, max) != center;
            }
        }
    }

    /// <summary>
    /// Gives each interior instance its objects, moved into the world. An interior archetype can be placed
    /// several times (identical apartments...): each instance gets its own copies.
    /// </summary>
    private void PlaceInteriorContents()
    {
        foreach (var map in _maps)
        {
            foreach (var instance in map.Entities)
            {
                if (!instance.IsInterior || GetArchetype(instance.ArchetypeHash) is not { } archetype)
                    continue;
                if (archetype.InteriorEntities.Count > 0)
                    instance.InteriorEntities = archetype.InteriorEntities.Select(e => e.PlaceInside(instance)).ToList();
                if (archetype.InteriorEntitySets.Count > 0)
                    instance.EntitySets = archetype.InteriorEntitySets.Select(set => PlaceEntitySet(set, instance)).ToList();
            }
        }
    }

    private static PlacedEntitySet PlaceEntitySet(InteriorEntitySet set, MapEntity instance)
    {
        var placed = new PlacedEntitySet { NameHash = set.NameHash, Interior = instance };
        placed.Entities = set.Entities.Select(e =>
        {
            var inside = e.PlaceInside(instance);
            inside.EntitySet = placed;
            return inside;
        }).ToList();
        return placed;
    }

    /// <summary>
    /// Connects each entity to its lower-detail parent. Each entity gives the index of its parent entity:
    /// in the parent ymap (named by the ymap) when flagged so, otherwise in its own ymap (e.g. the district
    /// models of dt1_rd1.ymap and their own simplified versions, in the same file).
    /// </summary>
    private void LinkLodTree()
    {
        var byName = new Dictionary<uint, MapData>();
        foreach (var map in _maps)
            byName[map.NameHash] = map;

        foreach (var map in _maps)
        {
            var parentMap = map.ParentHash != 0 ? byName.GetValueOrDefault(map.ParentHash) : null;
            for (int i = 0; i < map.Entities.Count; i++)
            {
                var entity = map.Entities[i];
                int index = entity.ParentIndex;
                var candidates = entity.LodInParentMap ? parentMap?.Entities : map.Entities;
                if (index < 0 || candidates is null || index >= candidates.Count || candidates == map.Entities && index == i)
                    continue;
                var parent = candidates[index];
                // A parent must be coarser than its child, otherwise the link is meaningless (the game ignores it).
                if (parent.LodLevel.Coarseness() > entity.LodLevel.Coarseness())
                    parent.AddChild(entity);
            }
        }

        foreach (var map in _maps)
        {
            foreach (var entity in map.Entities)
            {
                if (entity.Parent is null && DrawDistance(entity) > NearRootDistance)
                    _farRoots.Add(entity);
            }
        }
    }

    private void Add(MapData map)
    {
        _maps.Add(map);
        foreach (var entity in map.Entities)
        {
            entity.Map = map;
            AddToCell(entity);
        }
        EntityCount += map.Entities.Count;
    }

    private void AddToCell(MapEntity entity)
    {
        var cell = CellOf(new Vector2(entity.Position.X, entity.Position.Y));
        if (!_cells.TryGetValue(cell, out var list))
            _cells[cell] = list = new List<MapEntity>();
        list.Add(entity);
    }

    private static (int X, int Y) CellOf(Vector2 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Y / CellSize));

    private delegate T Reader<T>(ReadOnlySpan<byte> data);

    private static T? ReadOrNull<T>(GameContent content, string path, Reader<T> read, List<string> errors) where T : class
    {
        try
        {
            var data = content.ReadFile(path);
            return data is null || data.Length == 0 ? null : read(data);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            errors.Add($"{path} : {ex.Message}");
            return null;
        }
    }

    private static bool IsOriginal(MapData map, string path) =>
        map.NameHash == Hashing.Joaat.Hash(Path.GetFileNameWithoutExtension(path));

}
