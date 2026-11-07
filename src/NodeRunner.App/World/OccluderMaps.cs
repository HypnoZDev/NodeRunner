using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;

namespace NodeRunner.App.World;

// An occluder map file of the loaded map, with its occluders.
internal sealed record OccluderMapFile(string GamePath, MapOccluders Occluders)
{
    public string FileName => Path.GetFileName(GamePath);
}

// The occluders of the loaded map, by file, read once per map library: map files without objects (the game keeps
// occluders apart, *_occl_*.ymap). Their streaming boxes do not always cover their occluders, so all are read.
internal static class OccluderMaps
{
    private static readonly object Lock = new();
    private static (MapLibrary Library, IReadOnlyList<OccluderMapFile> Files)? _cached;

    public static IReadOnlyList<OccluderMapFile> Get(WorldLoader world)
    {
        if (world.Maps is not { } library || world.Content is not { } content)
            return [];
        lock (Lock)
        {
            if (_cached is { } cached && cached.Library == library)
                return cached.Files;
            var reader = new OccluderFile();
            var files = new List<OccluderMapFile>();
            foreach (var map in library.Maps)
            {
                if (map.IsCustom || map.FilePath is not { } path || map.Entities.Count > 0 || content.ReadFile(path) is not { } bytes)
                    continue;
                try
                {
                    if (reader.ReadOccluders(bytes) is { IsEmpty: false } occluders)
                        files.Add(new OccluderMapFile(path, occluders));
                }
                catch (InvalidDataException)
                {
                    // Grass maps (data in the graphics part) and damaged files: no occluders to read.
                }
            }
            _cached = (library, files);
            return files;
        }
    }
}
