using NodeRunner.Data.Audio;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Audio;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes the audio settings (audio\<resource>_game.dat151.rel) of the interiors placed by the exported maps that the
// game data does not fully cover: created or imported interiors, rooms added or renamed.
internal static class AudioGameDataFileSaver
{
    // files: names of the files in stream; interiorFiles: the .ytyp of the interiors changed or imported in the project (the
    // game's own interiors are left as the game has them). Returns the file's path in the resource ("audio/..."), or null.
    public static string? Save(WorldLoader world, IReadOnlyList<string> files, IReadOnlyList<string> interiorFiles, string resourceFolder,
        MapFileSaver.Report report)
    {
        if (world.Maps is not { } library)
            return null;
        var changed = interiorFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string stream = Path.Combine(resourceFolder, FiveMResource.StreamFolderName);
        var interiors = new Dictionary<uint, Archetype>();
        foreach (string mapFile in files.Where(f => f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
        {
            MapData map;
            try
            {
                map = new YmapReader().Read(ResourceFile.Read(File.ReadAllBytes(Path.Combine(stream, mapFile))).Content);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                continue; // Occlusion or grass only, or locked: no interiors to place.
            }
            foreach (var entity in map.Entities.Where(e => e.IsInterior))
            {
                if (library.GetArchetype(entity.ArchetypeHash) is { Kind: ArchetypeKind.Interior, InteriorRooms.Count: > 0 } type
                    && (type.IsCreated || library.GetArchetypeFile(type.NameHash) is { } file && changed.Contains(file)))
                    interiors.TryAdd(type.NameHash, type);
            }
        }
        if (interiors.Count == 0)
            return null;

        var data = world.WaitAudioGameData();
        var settings = new List<InteriorAudioSettings>();
        var rooms = new List<RoomAudioSettings>();
        foreach (var type in interiors.Values.OrderBy(t => t.NameHash))
        {
            if (data is not null && InteriorAudioBuilder.IsCovered(type, data))
                continue;
            var (interior, list) = InteriorAudioBuilder.Build(type, data);
            settings.Add(interior);
            rooms.AddRange(list);
        }
        if (settings.Count == 0)
            return null;

        string name = FiveMResource.AudioGameDataFile(Path.GetFileName(Path.TrimEndingDirectorySeparator(resourceFolder)));
        string target = Path.Combine(resourceFolder, name.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var bytes = GameDataWriter.Write(settings, rooms);
            var check = new GameDataReader().Read(bytes, name);
            if (check.Interiors.Count != settings.Count || check.Rooms.Count != rooms.Count)
                throw new InvalidDataException(L("vérification échouée"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
            return name;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{name} : {ex.Message}");
            return null;
        }
    }
}
