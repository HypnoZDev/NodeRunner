using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Automatic LODs written with an export: each big object of the project's own maps gets a lighter model drawn in its
// place from afar (LodModel). The models go to <resource>_nrlod_*.ydr... named after their object types, declared in
// <resource>_nrlod.ytyp and placed in <resource>_nrlod.ymap, the parent map of the project's maps.
internal static class LodFileSaver
{
    internal sealed class Plan
    {
        public required string MapName { get; init; }
        public Dictionary<MapData, (uint ParentMap, Dictionary<int, int> Parents)> Links { get; } = [];
        public List<(MapEntity Object, uint LodType)> Lods { get; } = [];
        public Dictionary<uint, LodModel.LodType> Types { get; } = [];
        public uint TypesSource { get; set; }
    }

    // Writes the LOD models into stream and returns what the maps must link to, null when no object needs a LOD.
    // A model that cannot be made (format not handled) leaves its objects without LOD.
    public static Plan? Prepare(WorldLoader world, IReadOnlyList<MapData> maps, string resourceName, string stream, MapFileSaver.Report report)
    {
        if (world.Content is not { } content || world.Maps is not { } library || world.Models is not { } models || world.Files is not { } files)
            return null;
        string mapName = resourceName.ToLowerInvariant() + "_nrlod";
        var plan = new Plan { MapName = mapName + ".ymap" };
        uint parentMap = Joaat.Hash(mapName);
        var made = new Dictionary<uint, uint?>(); // object type -> its LOD type (null: could not be made)
        foreach (var map in maps)
        {
            if (!map.IsCustom || map.ParentHash != 0)
                continue;
            var kept = map.KeptEntities.ToList();
            for (int i = 0; i < kept.Count; i++)
            {
                var entity = kept[i];
                if (entity.IsInterior || entity.LodLevel != LodLevel.OrphanHd)
                    continue;
                var type = library.GetArchetype(entity.ArchetypeHash);
                if (!LodModel.Wanted(type))
                    continue;
                if (!made.TryGetValue(entity.ArchetypeHash, out uint? lodType))
                    made[entity.ArchetypeHash] = lodType = MakeModel(world, content, files, models, type!, entity.ArchetypeHash, stream, plan, report);
                if (lodType is not { } lod)
                    continue;
                if (!plan.Links.TryGetValue(map, out var links))
                    plan.Links[map] = links = (parentMap, []);
                links.Parents[i] = plan.Lods.Count;
                plan.Lods.Add((entity, lod));
                if (plan.TypesSource == 0)
                    plan.TypesSource = entity.ArchetypeHash;
                // The LOD type is drawn as far as its farthest object needs.
                var t = plan.Types[lod];
                float distance = LodModel.LodDistance(entity.LodDistance);
                if (distance > t.LodDistance)
                    plan.Types[lod] = t with { LodDistance = distance };
            }
        }
        return plan.Lods.Count == 0 ? null : plan;
    }

    private static uint? MakeModel(WorldLoader world, GameContent content, IGameFileSystem files, Data.Models.ModelCatalog models, Archetype type,
        uint typeHash, string stream, Plan plan, MapFileSaver.Report report)
    {
        if (models.Locate(type) is not { } location)
            return null;
        string extension = Path.GetExtension(location.Path).ToLowerInvariant();
        if (extension is not (".ydr" or ".yft"))
            return null;
        string name = LodModel.Name(world.Names?.TryGet(typeHash), typeHash);
        try
        {
            if (content.ReadFile(location.Path) is not { } data || content.GetResourceHeader(location.Path) is not { } header)
                return null;
            string file = Path.GetFileName(location.Path);
            var xml = extension == ".yft" ? YftXml.Export(file, data, header, ShaderNames(files)) : YdrXml.Export(file, data, header, ShaderNames(files));
            // The embedded textures are found by their file name (the export puts them in a folder named after the model).
            var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, bytes) in xml.Files)
                textures[Path.GetFileName(path)] = bytes;
            byte[] model = YdrXml.Convert(new XmlExport(name + ".ydr.xml", LodModel.DrawableXml(xml.Text, name),
                path => textures.GetValueOrDefault(Path.GetFileName(path))));
            string target = name + ".ydr";
            SafeFile.WriteAllBytes(Path.Combine(stream, target), model);
            report.Saved.Add(target);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or FormatException or IndexOutOfRangeException)
        {
            Diagnostics.ErrorLog.Add(L("LOD automatiques"), L($"{name} : {ex.Message}"), ex);
            return null;
        }
        uint lod = Joaat.Hash(name);
        string textureDictionary = type.TextureDictionaryHash == 0 ? ""
            : world.Names?.TryGet(type.TextureDictionaryHash) ?? $"hash_{type.TextureDictionaryHash:X8}";
        plan.Types[lod] = new LodModel.LodType(name, textureDictionary, 0f, type.BoundsMin, type.BoundsMax);
        return lod;
    }

    private static (IGameFileSystem Files, ShaderNameCatalog Names)? _shaderNames;

    private static ShaderNameCatalog ShaderNames(IGameFileSystem files)
    {
        if (_shaderNames is not { } cached || cached.Files != files)
            _shaderNames = cached = (files, ShaderNameCatalog.Read(files));
        return cached.Names;
    }

    // The LOD types (.ytyp) and the LOD map (.ymap), once the project's maps are written linked to it.
    public static void Save(WorldLoader world, Plan plan, string stream, MapFileSaver.Report report)
    {
        if (world.Content is not { } content || world.Maps is not { } library)
            return;
        string baseName = Path.GetFileNameWithoutExtension(plan.MapName);
        try
        {
            // Descriptions of the type structures: from the .ytyp of one of the objects.
            if (library.GetArchetypeFile(plan.TypesSource) is not { } typesFile || content.ReadFile(typesFile) is not { } typesData)
                throw new InvalidDataException(L("fichier de types introuvable"));
            var schema = new MetaSchema([typesData], []);
            string typesName = baseName + ".ytyp";
            byte[] types = YtypXml.Convert(new XmlExport(typesName + ".xml", LodModel.TypesXml(baseName, plan.Types.Values), _ => null), schema);
            SafeFile.WriteAllBytes(Path.Combine(stream, typesName), types);
            report.Saved.Add(typesName);

            var entities = plan.Lods.Select(l => new MapEntity
            {
                ArchetypeHash = l.LodType,
                Position = l.Object.Position,
                StoredRotation = l.Object.StoredRotation,
                Scale = l.Object.Scale,
                Flags = Adding.StaticFlags,
                Guid = (uint)Random.Shared.Next(1, int.MaxValue),
                LodDistance = LodModel.LodDistance(l.Object.LodDistance),
                ChildLodDistance = l.Object.LodDistance,
                LodLevel = LodLevel.Lod,
            }).ToList();
            var map = new MapData { NameHash = Joaat.Hash(baseName), Entities = entities };
            var lodTypes = plan.Types.ToDictionary(t => Joaat.Hash(t.Value.Name), t => new Archetype
            {
                NameHash = Joaat.Hash(t.Value.Name),
                BoundsMin = t.Value.BoxMin,
                BoundsMax = t.Value.BoxMax,
                SphereCenter = (t.Value.BoxMin + t.Value.BoxMax) * 0.5f,
                SphereRadius = System.Numerics.Vector3.Distance(t.Value.BoxMin, t.Value.BoxMax) * 0.5f,
                LodDistance = t.Value.LodDistance,
                AssetType = ArchetypeAssetType.Drawable,
            });
            var extents = MapExtentsCalculator.Compute(map, h => lodTypes.GetValueOrDefault(h) ?? library.GetArchetype(h));
            var template = MapFileSaver.Template(content, library);
            var writer = new YmapWriter();
            var written = writer.Build(template.Content, map, baseName, extents);
            written = YmapWriter.SetChildren(written, Enumerable.Range(0, entities.Count).ToDictionary(i => i, i => (1, entities[i].ChildLodDistance)));
            var header = template.Header;
            var file = ResourceFile.Write(header with { SystemFlags = (header.SystemFlags & 0xF0000000) | written.SystemPageFlags }, written.Content);
            SafeFile.WriteAllBytes(Path.Combine(stream, plan.MapName), file);
            report.Saved.Add(plan.MapName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            report.Errors.Add(L($"LOD automatiques : {ex.Message}"));
        }
    }
}
