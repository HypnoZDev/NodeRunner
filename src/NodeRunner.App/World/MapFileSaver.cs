using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes modified map files (.ymap) to a folder, as loose resource files with their game name: the form a FiveM
// resource streams them in (they replace the game's file of the same name).
internal static class MapFileSaver
{
    public sealed record Report(List<string> Saved, List<string> Errors)
    {
        // Files of a previous export no longer written, left in place (export made before NodeRunner listed its files).
        public List<string> Kept { get; } = [];
    }

    public static Report Save(WorldLoader world, IEnumerable<MapData> maps, string folder, bool markSaved = true)
    {
        var report = new Report([], []);
        if (world.Content is not { } content || world.Maps is not { } library)
        {
            report.Errors.Add(L("Aucune carte chargée."));
            return report;
        }

        Directory.CreateDirectory(folder);
        var writer = new YmapWriter();
        foreach (var map in maps)
        {
            string name = map.FilePath is { } path ? Path.GetFileName(path) : $"0x{map.NameHash:X8}.ymap";
            try
            {
                if (map.FilePath is null)
                    throw new InvalidDataException(L("fichier d'origine inconnu"));
                // A lodlights file: its lights are where those of its parent distlodlights file are.
                var parentLights = map.LodLights.Count > 0
                    ? library.Maps.FirstOrDefault(m => m.DistantLights.Count > 0 && m.NameHash == map.ParentHash)?.KeptDistantLights
                    : null;
                var extents = MapExtentsCalculator.Compute(map, library.GetArchetype, parentLights);
                MapFileContent written;
                ResourceHeader header;
                // A project's map getting its first parking spots or timecycle zones: their description is taken from a game map.
                var borrowed = new List<Data.Native.Resources.MetaDocument.StructureInfo>();
                if (map.IsCustom && map.KeptCarGenerators.Any())
                    borrowed.Add(Description(content, library, m => m.CarGenerators.Count > 0, YmapWriter.CarGeneratorDescription, ref _carGenerator,
                        L("aucun YMAP du jeu avec des places de véhicules, utilisable comme modèle")));
                if (map.IsCustom && map.TimecycleModifiers.Any(z => !z.IsDeleted))
                    borrowed.Add(Description(content, library, m => m.TimecycleModifiers.Count > 0, YmapWriter.TimecycleDescription, ref _timecycle,
                        L("aucun YMAP du jeu avec des zones de timecycle, utilisable comme modèle")));
                if (map.IsCustom && (map.DistantLights.Count > 0 || map.LodLights.Count > 0)
                    && map.Entities.Count == 0 && map.CarGenerators.Count == 0 && map.TimecycleModifiers.Count == 0)
                {
                    // The project's own light files (lights only): built again from a game light file of the same kind,
                    // even when saved before (a file saved without lights has no array to grow).
                    var template = LightTemplate(content, library, distant: map.DistantLights.Count > 0);
                    header = template.Header;
                    written = writer.BuildLights(template.Content, map, Path.GetFileNameWithoutExtension(name), extents);
                }
                else if (map.IsCustom && map.SavedContent is { } saved)
                {
                    // A map read from the project's files (imported or saved earlier): starts from it, so that what
                    // the editor does not handle (lights, extensions...) is kept.
                    var template = Template(content, library);
                    header = template.Header;
                    var interiorInstance = map.KeptEntities.Any(e => e.SourceIndex < 0 && e.IsInterior) ? InteriorInstanceDescription(content, library) : null;
                    written = writer.Write(saved, map, extents, borrowed, template.Content, interiorInstance, library.GetArchetype);
                }
                else if (map.IsCustom)
                {
                    // The user's own map: built whole, with a game map file as model for the descriptions.
                    var template = Template(content, library);
                    header = template.Header;
                    var interiorInstance = map.KeptEntities.Any(e => e.IsInterior) ? InteriorInstanceDescription(content, library) : null;
                    written = writer.Build(template.Content, map, Path.GetFileNameWithoutExtension(name), extents, interiorInstance, library.GetArchetype, borrowed);
                }
                else
                {
                    // Starts from the file the map was read from: the user's saved version, or the game's.
                    var source = map.SavedContent ?? content.ReadFile(map.FilePath) ?? throw new InvalidDataException(L("fichier d'origine introuvable"));
                    header = content.GetResourceHeader(map.FilePath) ?? throw new InvalidDataException(L("en-tête de ressource introuvable"));
                    written = writer.Write(source, map, extents);
                }
                var file = ResourceFile.Write(header with { SystemFlags = (header.SystemFlags & 0xF0000000) | written.SystemPageFlags }, written.Content);
                Verify(file, map);

                // Written next to the target first: an interrupted save never leaves a broken file in place.
                string target = Path.Combine(folder, name);
                string temporary = target + ".tmp";
                File.WriteAllBytes(temporary, file);
                File.Move(temporary, target, overwrite: true);
                if (markSaved)
                    map.IsSaved = true;
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
        return report;
    }

    private static (MapLibrary Library, byte[] Content, ResourceHeader Header)? _template;

    // A game map file with ordinary objects (no interior, no grass), whose descriptions of the structures are used
    // for the user's own maps; the same one for a whole map loading.
    private static (byte[] Content, ResourceHeader Header) Template(GameContent content, MapLibrary library)
    {
        if (_template is { } cached && cached.Library == library)
            return (cached.Content, cached.Header);
        foreach (var map in library.Maps)
        {
            if (map.IsCustom || map.IsScripted || map.FilePath is null || (map.ContentFlags & (8 | 1024)) != 0
                || map.Entities.Count == 0 || map.Entities.Any(e => e.IsInterior))
                continue;
            if (content.ReadFile(map.FilePath) is not { } bytes || content.GetResourceHeader(map.FilePath) is not { } found)
                continue;
            // No graphics pages in a map with only objects: keep the version bits (top 4) of the graphics flags only.
            var header = found with { GraphicsFlags = found.GraphicsFlags & 0xF0000000 };
            _template = (library, bytes, header);
            return (bytes, header);
        }
        throw new InvalidDataException(L("aucun YMAP du jeu utilisable comme modèle"));
    }

    private static (MapLibrary Library, bool Distant, byte[] Content, ResourceHeader Header)? _lightTemplate;

    // A game distlodlights file (distant) or lodlights file, model of the project's light files.
    private static (byte[] Content, ResourceHeader Header) LightTemplate(GameContent content, MapLibrary library, bool distant)
    {
        if (_lightTemplate is { } cached && cached.Library == library && cached.Distant == distant)
            return (cached.Content, cached.Header);
        foreach (var map in library.Maps)
        {
            if (map.IsCustom || map.IsScripted || map.FilePath is null || map.Entities.Count > 0 || (distant ? map.DistantLights.Count : map.LodLights.Count) == 0
                || (distant ? map.LodLights.Count : map.DistantLights.Count) > 0)
                continue;
            if (content.ReadFile(map.FilePath) is not { } bytes || content.GetResourceHeader(map.FilePath) is not { } found)
                continue;
            var header = found with { GraphicsFlags = found.GraphicsFlags & 0xF0000000 };
            _lightTemplate = (library, distant, bytes, header);
            return (bytes, header);
        }
        throw new InvalidDataException(L("aucun fichier de lumières LOD du jeu utilisable comme modèle"));
    }

    private static (MapLibrary Library, Data.Native.Resources.MetaDocument.StructureInfo Info)? _interiorInstance;

    // The description of interior instances, from a game map file that places some (for interiors made by the user).
    private static Data.Native.Resources.MetaDocument.StructureInfo InteriorInstanceDescription(GameContent content, MapLibrary library)
    {
        if (_interiorInstance is { } cached && cached.Library == library)
            return cached.Info;
        foreach (var map in library.Maps)
        {
            if (map.IsCustom || map.FilePath is null || !map.Entities.Any(e => e.IsInterior) || content.ReadFile(map.FilePath) is not { } bytes)
                continue;
            if (YmapWriter.InteriorInstanceDescription(bytes) is { } info)
            {
                _interiorInstance = (library, info);
                return info;
            }
        }
        throw new InvalidDataException(L("aucun YMAP du jeu avec un intérieur, utilisable comme modèle"));
    }

    private static (MapLibrary Library, Data.Native.Resources.MetaDocument.StructureInfo Info)? _carGenerator, _timecycle;

    private delegate Data.Native.Resources.MetaDocument.StructureInfo? DescriptionReader(ReadOnlySpan<byte> source);

    // The description of a structure, from a game map file that has some (remembered for the map loading).
    private static Data.Native.Resources.MetaDocument.StructureInfo Description(GameContent content, MapLibrary library, Func<MapData, bool> has,
        DescriptionReader read, ref (MapLibrary Library, Data.Native.Resources.MetaDocument.StructureInfo Info)? cache, string missing)
    {
        if (cache is { } cached && cached.Library == library)
            return cached.Info;
        foreach (var map in library.Maps)
        {
            if (map.IsCustom || map.FilePath is null || !has(map) || content.ReadFile(map.FilePath) is not { } bytes)
                continue;
            if (read(bytes) is { } info)
            {
                cache = (library, info);
                return info;
            }
        }
        throw new InvalidDataException(missing);
    }

    // Reads the new file back as the game data would be, and checks that every object kept is where it should be.
    private static void Verify(byte[] file, MapData map)
    {
        var (_, content) = ResourceFile.Read(file);
        var written = new YmapReader().Read(content);
        var kept = map.KeptEntities.ToList();
        if (written.Entities.Count != kept.Count)
            throw new InvalidDataException(L("vérification échouée (nombre d'objets)"));
        for (int i = 0; i < written.Entities.Count; i++)
        {
            var a = written.Entities[i];
            var b = kept[i];
            if (a.ArchetypeHash != b.ArchetypeHash || a.Position != b.Position || a.StoredRotation != b.StoredRotation || a.Scale.Z != b.Scale.Z)
                throw new InvalidDataException(L($"vérification échouée (objet n° {i + 1})"));
        }
        var spots = map.KeptCarGenerators.ToList();
        if (written.CarGenerators.Count != spots.Count
            || written.CarGenerators.Where((s, i) => s.Position != spots[i].Position || s.Orientation != spots[i].Orientation || s.CarModel != spots[i].CarModel).Any())
            throw new InvalidDataException(L("vérification échouée (places de véhicules)"));
        var zones = map.TimecycleModifiers.Where(z => !z.IsDeleted).ToList();
        if (written.TimecycleModifiers.Count != zones.Count
            || written.TimecycleModifiers.Where((z, i) => z.BoundsMin != zones[i].BoundsMin || z.BoundsMax != zones[i].BoundsMax || z.NameHash != zones[i].NameHash).Any())
            throw new InvalidDataException(L("vérification échouée (zones de timecycle)"));
        if (!written.DistantLights.SequenceEqual(map.KeptDistantLights) || !written.LodLights.SequenceEqual(map.KeptLodLights)
            || written.DistantLights.Count > 0 && written.DistantStreetLights != map.KeptStreetLights)
            throw new InvalidDataException(L("vérification échouée (lumières LOD)"));
    }
}
