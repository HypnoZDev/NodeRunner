using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// An occluder map file of the loaded map, with its occluders as read from the game (Occluders), its boxes as edited
// (Boxes: same indices, new boxes at the end) and what was deleted. The project's own file (IsProject) starts empty and
// holds the boxes created by hand.
internal sealed class OccluderMapFile(string gamePath, MapOccluders occluders, bool isProject = false)
{
    public string GamePath { get; } = gamePath;

    public MapOccluders Occluders { get; } = occluders;

    public bool IsProject { get; } = isProject;

    public List<OccluderBox> Boxes { get; } = [.. occluders.Boxes];

    public HashSet<int> DeletedBoxes { get; } = [];

    public HashSet<int> DeletedModels { get; } = [];

    public string FileName => Path.GetFileName(GamePath);

    public bool IsBoxChanged(int index) => index >= Occluders.Boxes.Count || Boxes[index] != Occluders.Boxes[index];

    public bool IsDeleted(OcclusionLayerPart kind, int index) =>
        (kind == OcclusionLayerPart.Boxes ? DeletedBoxes : DeletedModels).Contains(index);

    public int BoxCount => Boxes.Count - DeletedBoxes.Count;

    public int ModelCount => Occluders.Models.Count - DeletedModels.Count;
}

// The occluders of the loaded map, by file, read once per map library: map files without objects (the game keeps
// occluders apart, *_occl_*.ymap). Their streaming boxes do not always cover their occluders, so all are read.
internal static class OccluderMaps
{
    private static readonly object Lock = new();
    private static (MapLibrary Library, List<OccluderMapFile> Files)? _cached;

    // Changes at every edit of an occluder: the layer is built again.
    public static int Revision { get; private set; }

    public static IReadOnlyList<OccluderMapFile> Get(WorldLoader world)
    {
        if (world.Maps is not { } library || world.Content is not { } content)
            return [];
        lock (Lock)
        {
            if (_cached is { } cached && cached.Library == library)
                return [.. cached.Files];
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
            return [.. files];
        }
    }

    // The project's occluder file (boxes created by hand), made on first use.
    public static OccluderMapFile? ProjectFile(WorldLoader world, string fileName)
    {
        Get(world);
        lock (Lock)
        {
            if (_cached is not { } cached)
                return null;
            if (cached.Files.Find(f => f.IsProject) is { } file)
                return file;
            file = new OccluderMapFile(fileName, MapOccluders.Empty, isProject: true);
            cached.Files.Add(file);
            Revision++;
            return file;
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

    // A new box at the end of the file's list; its Index is set here.
    public static OccluderBox AddBox(OccluderMapFile file, OccluderBox box)
    {
        lock (Lock)
        {
            box = box with { Index = file.Boxes.Count };
            file.Boxes.Add(box);
            Revision++;
            return box;
        }
    }

    public static void SetDeleted(OccluderMapFile file, OcclusionLayerPart kind, int index, bool deleted)
    {
        lock (Lock)
        {
            var set = kind == OcclusionLayerPart.Boxes ? file.DeletedBoxes : file.DeletedModels;
            if (deleted) set.Add(index);
            else set.Remove(index);
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

// Occluders added (deleted = false) or deleted (deleted = true): undoing flips them back. An added box stays in its
// file's list, only marked as deleted while undone.
internal sealed class OccluderPresenceEdit(IReadOnlyList<OccluderSelection> occluders, bool deleted, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public IReadOnlyList<OccluderSelection> Occluders => occluders;
    public bool Deletes => deleted;

    public void Undo() => Apply(!deleted);
    public void Redo() => Apply(deleted);

    private void Apply(bool delete)
    {
        foreach (var o in occluders)
            OccluderMaps.SetDeleted(o.File, o.Kind, o.Index, delete);
    }
}
