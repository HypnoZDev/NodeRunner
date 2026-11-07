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
    public sealed record Report(List<string> Saved, List<string> Errors);

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
                var extents = MapExtentsCalculator.Compute(map, library.GetArchetype);
                MapFileContent written;
                ResourceHeader header;
                if (map.IsCustom)
                {
                    // The user's own map: built whole, with a game map file as model for the descriptions.
                    var template = Template(content, library);
                    header = template.Header;
                    written = writer.Build(template.Content, map, Path.GetFileNameWithoutExtension(name), extents);
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
    }
}
