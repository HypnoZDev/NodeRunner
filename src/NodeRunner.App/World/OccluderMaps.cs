using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// An occluder map file of the loaded map, with its occluders as read from the game (Occluders) and its boxes as
// edited (Boxes, same order and indices).
internal sealed class OccluderMapFile(string gamePath, MapOccluders occluders)
{
    public string GamePath { get; } = gamePath;

    public MapOccluders Occluders { get; } = occluders;

    public List<OccluderBox> Boxes { get; } = [.. occluders.Boxes];

    public string FileName => Path.GetFileName(GamePath);

    public bool IsBoxChanged(int index) => Boxes[index] != Occluders.Boxes[index];
}

// The occluders of the loaded map, by file, read once per map library: map files without objects (the game keeps
// occluders apart, *_occl_*.ymap). Their streaming boxes do not always cover their occluders, so all are read.
internal static class OccluderMaps
{
    private static readonly object Lock = new();
    private static (MapLibrary Library, IReadOnlyList<OccluderMapFile> Files)? _cached;

    // Changes at every edit of an occluder: the layer is built again.
    public static int Revision { get; private set; }

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

    public static void SetBox(OccluderMapFile file, OccluderBox box)
    {
        lock (Lock)
        {
            file.Boxes[box.Index] = box;
            Revision++;
        }
    }
}

// A box occluder moved, turned or resized.
internal sealed class OccluderBoxEdit(OccluderMapFile file, OccluderBox before, OccluderBox after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public OccluderMapFile File => file;
    public int Index => after.Index;

    public void Undo() => OccluderMaps.SetBox(file, before);
    public void Redo() => OccluderMaps.SetBox(file, after);
}
