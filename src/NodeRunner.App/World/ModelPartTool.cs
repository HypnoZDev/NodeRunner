using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Models;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Removes pieces of an object's model (a logo, a sign...): a click in the 3D view picks the piece under the mouse (its
// triangles touching each other), then the model file is written again with those triangles emptied. Nothing else of
// the file changes. The file goes to the project's stream folder; a file of the project keeps its first version in
// the originals folder.
internal sealed class ModelPartTool
{
    private const int MaxEdges = 30000;
    private const float BoxMargin = 0.02f;

    private readonly ResourceHeader _header;
    private readonly byte[] _content;
    private readonly List<DrawableReader.StoredGeometry> _all;
    private readonly List<DrawableReader.StoredGeometry> _shown;
    private readonly List<(Vector3[] Positions, ushort[] Indices)> _meshes;
    private readonly HashSet<MeshTriangle> _selected = [];

    public MapEntity Entity { get; }
    public string Path { get; }

    public int SelectedCount => _selected.Count;

    private ModelPartTool(MapEntity entity, string path, ResourceHeader header, byte[] content, List<DrawableReader.StoredGeometry> all)
    {
        Entity = entity;
        Path = path;
        _header = header;
        _content = content;
        _all = all;
        int level = all.Min(g => g.Level);
        _shown = all.Where(g => g.Level == level).ToList();
        _meshes = _shown.Select(g => (g.Positions, g.Indices)).ToList();
    }

    // The tool for this object's model, or an error message.
    public static (ModelPartTool? Tool, string? Error) Open(MapEntity entity, WorldLoader world)
    {
        if (world.Maps?.GetArchetype(entity.ArchetypeHash) is not { } archetype || world.Models?.Locate(archetype) is not { } location
            || world.Content is not { } content)
            return (null, L("Modèle 3D introuvable."));
        try
        {
            if (content.ReadFile(location.Path) is not { } data || content.GetResourceHeader(location.Path) is not { } header)
                return (null, L("Modèle 3D introuvable."));
            var all = DrawableReader.StoredGeometries(data, header, System.IO.Path.GetExtension(location.Path), location.ModelHash);
            if (all.Count == 0)
                return (null, L("Ce modèle n'a aucune partie lisible."));
            return (new ModelPartTool(entity, location.Path, header, data, all), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, ex.Message);
        }
    }

    // Click: the piece under the ray (world space) replaces the selection, or is added / removed with add.
    public bool Click(Vector3 origin, Vector3 direction, bool add)
    {
        if (!Matrix4x4.Invert(Entity.Transform, out var toModel))
            return false;
        var localOrigin = Vector3.Transform(origin, toModel);
        var localDirection = Vector3.TransformNormal(direction, toModel);
        if (ModelParts.Hit(_meshes, localOrigin, localDirection) is not { } found)
        {
            if (!add)
                _selected.Clear();
            return false;
        }
        var hit = found.Triangle;
        var (positions, indices) = _meshes[hit.Mesh];
        var piece = ModelParts.Connected(positions, indices, hit.Triangle).Select(t => new MeshTriangle(hit.Mesh, t)).ToList();
        if (!add)
            _selected.Clear();
        if (add && piece.All(_selected.Contains))
            _selected.ExceptWith(piece);
        else
            _selected.UnionWith(piece);
        return true;
    }

    public void Clear() => _selected.Clear();

    // Edges of the selected triangles, in the model's space.
    public IEnumerable<(Vector3 From, Vector3 To)> Edges()
    {
        int edges = 0;
        foreach (var (mesh, t) in _selected)
        {
            var (p, idx) = _meshes[mesh];
            var a = p[idx[t * 3]];
            var b = p[idx[t * 3 + 1]];
            var c = p[idx[t * 3 + 2]];
            yield return (a, b);
            yield return (b, c);
            yield return (c, a);
            if ((edges += 3) >= MaxEdges)
                yield break;
        }
    }

    private const string GameMark = ".jeu";

    private static string OriginalOf(AppState state, string streamFile) =>
        state.World.ProjectFiles?.OriginalOf(streamFile)
        ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(streamFile)!)!,
            LooseFileOverlay.OriginalsFolderName, System.IO.Path.GetFileName(streamFile));

    // The project's changed copy of this model, when the tool changed it.
    public static string? ChangedFile(AppState state, string path)
    {
        if (state.World.ProjectFiles?.StreamFileOf(path) is not { } file)
            return null;
        string original = OriginalOf(state, file);
        return File.Exists(original) || File.Exists(original + GameMark) ? file : null;
    }

    // Puts the model back as it was before the tool changed it.
    public static string Restore(AppState state, string path)
    {
        if (ChangedFile(state, path) is not { } file)
            return L("Ce modèle n'a pas été modifié.");
        string original = OriginalOf(state, file);
        try
        {
            if (File.Exists(original))
                File.Move(original, file, overwrite: true);
            else
            {
                File.Delete(file);
                File.Delete(original + GameMark);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return L($"Impossible de remettre le modèle : {ex.Message}");
        }
        state.World.RequestMapReload();
        return L($"{System.IO.Path.GetFileName(file)} remis comme avant. Rechargement de la carte...");
    }

    // Writes the model without the selected piece; returns the message to show and whether it worked.
    public (bool Done, string Message) Apply(AppState state)
    {
        if (_selected.Count == 0)
            return (false, L("Cliquez d'abord sur le morceau à retirer."));
        var triangles = _selected.Select(s => (_shown[s.Mesh].IndicesAt, s.Triangle)).ToList();
        // The simpler versions of the model (seen from farther away): their triangles in the same place.
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var group in _selected.GroupBy(s => s.Mesh))
        {
            var (positions, indices) = _meshes[group.Key];
            var box = ModelParts.Box(positions, indices, group.Select(s => s.Triangle));
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }
        min -= new Vector3(BoxMargin);
        max += new Vector3(BoxMargin);
        int farther = 0;
        foreach (var geometry in _all.Where(g => !_shown.Contains(g)))
        {
            foreach (int t in ModelParts.Inside(geometry.Positions, geometry.Indices, min, max))
            {
                triangles.Add((geometry.IndicesAt, t));
                farther++;
            }
        }

        string fileName = System.IO.Path.GetFileName(Path);
        string? target = state.World.ProjectFiles?.StreamFileOf(Path);
        if (target is null)
        {
            if (state.SaveFolder is not { } stream)
                return (false, L("Ouvrez ou créez un projet : le modèle modifié y est enregistré (dossier stream)."));
            target = System.IO.Path.Combine(stream, fileName);
        }
        try
        {
            var bytes = DrawablePatch.HideTriangles(_header, _content, triangles);
            // The first version is kept to put it back: the project's file itself, or a mark for a game's file (put
            // back by deleting the copy).
            string original = OriginalOf(state, target);
            if (!File.Exists(original) && !File.Exists(original + GameMark))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(original)!);
                if (File.Exists(target))
                    File.Copy(target, original);
                else
                    File.WriteAllBytes(original + GameMark, []);
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            SafeFile.WriteAllBytes(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return (false, L($"Enregistrement impossible : {ex.Message}"));
        }
        int count = _selected.Count;
        _selected.Clear();
        state.World.RequestMapReload();
        return (true, farther > 0
            ? L($"{count} triangles retirés de {fileName} (+ {farther} dans ses versions simplifiées). Rechargement de la carte...")
            : L($"{count} triangles retirés de {fileName}. Rechargement de la carte..."));
    }
}
