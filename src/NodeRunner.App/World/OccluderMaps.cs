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

    // Changed since the game's file (always false for the project's file).
    public bool HasChanges => !IsProject
        && (DeletedBoxes.Count > 0 || DeletedModels.Count > 0 || Enumerable.Range(0, Occluders.Boxes.Count).Any(IsBoxChanged));

    // The occluders left, as edited (indices as in Boxes and Occluders.Models).
    public MapOccluders Current => new(
        Boxes.Where(b => !DeletedBoxes.Contains(b.Index)).ToList(),
        Occluders.Models.Where(m => !DeletedModels.Contains(m.Index)).ToList());
}

// The occluders of the loaded map, by file, read once per map library: map files without objects (the game keeps
// occluders apart, *_occl_*.ymap). Their streaming boxes do not always cover their occluders, so all are read.
internal static class OccluderMaps
{
    private static readonly object Lock = new();
    private static (MapLibrary Library, List<OccluderMapFile> Files)? _cached;

    // Changes at every edit of an occluder: the layer is built again.
    public static int Revision { get; private set; }

    private static OccluderEdits _saved = OccluderEdits.Empty;
    private static int _savedRevision;
    private static readonly List<string> _errors = [];

    // The project's saved changes, put back on the files read for the next map loading.
    public static void UseSaved(OccluderEdits edits)
    {
        lock (Lock)
        {
            _saved = edits;
            _cached = null;
            _errors.Clear();
        }
    }

    public static OccluderEdits Saved
    {
        get { lock (Lock) return _saved; }
    }

    // Saved changes that could not be put back (game file missing or changed).
    public static List<string> Errors
    {
        get { lock (Lock) return [.. _errors]; }
    }

    // Changes made since the files were read or last saved.
    public static bool HasUnsavedChanges(WorldLoader world)
    {
        lock (Lock)
            return _cached is { } cached && cached.Library == world.Maps && Revision != _savedRevision;
    }

    public static void MarkSaved(OccluderEdits edits)
    {
        lock (Lock)
        {
            _saved = edits;
            _savedRevision = Revision;
        }
    }

    private static (int Revision, OccluderEdits Edits)? _captured;

    // The changes to save; the saved ones while the files of this map loading have not been read.
    public static OccluderEdits Capture(WorldLoader world, string projectFileName)
    {
        lock (Lock)
        {
            if (_cached is not { } cached || cached.Library != world.Maps)
                return _saved;
            if (_captured is { } c && c.Revision == Revision && c.Edits.ProjectFile == projectFileName)
                return c.Edits;
            var files = new Dictionary<string, SavedOccluderFile>(StringComparer.OrdinalIgnoreCase);
            List<SavedOccluderBox>? created = null;
            foreach (var file in cached.Files)
            {
                if (file.IsProject)
                {
                    created = file.Boxes.Where(b => !file.DeletedBoxes.Contains(b.Index)).Select(SavedOccluderBox.From).ToList();
                    continue;
                }
                if (!file.HasChanges)
                    continue;
                var changed = Enumerable.Range(0, file.Occluders.Boxes.Count)
                    .Where(i => file.IsBoxChanged(i) && !file.DeletedBoxes.Contains(i))
                    .ToDictionary(i => i, i => SavedOccluderBox.From(file.Boxes[i]));
                files[file.GamePath] = new SavedOccluderFile(file.Occluders.Boxes.Count, file.Occluders.Models.Count,
                    changed.Count > 0 ? changed : null,
                    file.DeletedBoxes.Count > 0 ? [.. file.DeletedBoxes.Order()] : null,
                    file.DeletedModels.Count > 0 ? [.. file.DeletedModels.Order()] : null);
            }
            var edits = new OccluderEdits(1, projectFileName, created is { Count: > 0 } ? created : null, files.Count > 0 ? files : null);
            _captured = (Revision, edits);
            return edits;
        }
    }

    // Puts the saved changes back on freshly read files.
    private static void Apply(OccluderEdits saved, List<OccluderMapFile> files)
    {
        foreach (var (path, edit) in saved.Files ?? [])
        {
            string name = Path.GetFileName(path);
            var file = files.Find(f => string.Equals(f.GamePath, path, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                _errors.Add($"{name} (occlusion) : fichier du jeu introuvable dans la carte chargée");
                continue;
            }
            if (file.Occluders.Boxes.Count != edit.BoxCount || file.Occluders.Models.Count != edit.ModelCount)
            {
                _errors.Add($"{name} (occlusion) : le fichier du jeu a changé depuis l'enregistrement");
                continue;
            }
            foreach (var (index, box) in edit.Changed ?? [])
            {
                if (index >= 0 && index < file.Boxes.Count)
                    file.Boxes[index] = box.ToBox(index);
            }
            file.DeletedBoxes.UnionWith((edit.DeletedBoxes ?? []).Where(i => i >= 0 && i < file.Boxes.Count));
            file.DeletedModels.UnionWith((edit.DeletedModels ?? []).Where(i => i >= 0 && i < file.Occluders.Models.Count));
        }
        if (saved.Created is { Count: > 0 } created && saved.ProjectFile is { } projectFile)
        {
            var file = new OccluderMapFile(projectFile, MapOccluders.Empty, isProject: true);
            for (int i = 0; i < created.Count; i++)
                file.Boxes.Add(created[i].ToBox(i));
            files.Add(file);
        }
    }

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
            _errors.Clear();
            Apply(_saved, files);
            _cached = (library, files);
            Revision++;
            _savedRevision = Revision;
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
