using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Removes pieces of an object's model (a logo, a sign...): a click in the 3D view picks the piece under the mouse (its
// triangles touching each other), then the model file is written again with those triangles emptied. Nothing else of
// the file changes. Its textures can also be replaced (in the model file, or in its .ytd), in place. The files go to
// the project's stream folder; a file of the project keeps its first version in the originals folder.
internal sealed class ModelPartTool
{
    private const int MaxEdges = 30000;
    private const float BoxMargin = 0.02f;

    private readonly ResourceHeader _header;
    private readonly byte[] _content;
    private List<DrawableReader.StoredGeometry> _all;
    private List<DrawableReader.StoredGeometry> _shown;
    private List<(Vector3[] Positions, ushort[] Indices)> _meshes;
    private readonly string _extension;
    private readonly HashSet<MeshTriangle> _selected = [];

    // The object of the map the tool was opened on, null when opened on a model alone (model workshop).
    public MapEntity? Entity { get; private init; }
    public string Path { get; }

    public int SelectedCount => _selected.Count;

    // Changes each time the chosen triangles change.
    public int Revision { get; private set; }

    private readonly uint _modelHash;

    private ModelPartTool(string path, uint modelHash, ResourceHeader header, byte[] content, List<DrawableReader.StoredGeometry> all)
    {
        Path = path;
        _modelHash = modelHash;
        _header = header;
        _content = content;
        _extension = System.IO.Path.GetExtension(path);
        (_all, _shown, _meshes) = Split(all);
    }

    private static (List<DrawableReader.StoredGeometry>, List<DrawableReader.StoredGeometry>, List<(Vector3[], ushort[])>) Split(
        List<DrawableReader.StoredGeometry> all)
    {
        int level = all.Min(g => g.Level);
        var shown = all.Where(g => g.Level == level).ToList();
        return (all, shown, shown.Select(g => (g.Positions, g.Indices)).ToList());
    }

    // The tool for this object's model, or an error message.
    public static (ModelPartTool? Tool, string? Error) Open(MapEntity entity, WorldLoader world)
    {
        if (world.Maps?.GetArchetype(entity.ArchetypeHash) is not { } archetype || world.Models?.Locate(archetype) is not { } location)
            return (null, L("Modèle 3D introuvable."));
        var (tool, error) = Open(location, world);
        return (tool is null ? null : new ModelPartTool(tool.Path, tool._modelHash, tool._header, tool._content, tool._all) { Entity = entity }, error);
    }

    public static (ModelPartTool? Tool, string? Error) Open(Data.Models.ModelLocation location, WorldLoader world)
    {
        if (world.Content is not { } content)
            return (null, L("Modèle 3D introuvable."));
        try
        {
            if (content.ReadFile(location.Path) is not { } data || content.GetResourceHeader(location.Path) is not { } header)
                return (null, L("Modèle 3D introuvable."));
            var all = DrawableReader.StoredGeometries(data, header, System.IO.Path.GetExtension(location.Path), location.ModelHash);
            if (all.Count == 0)
                return (null, L("Ce modèle n'a aucune partie lisible."));
            return (new ModelPartTool(location.Path, location.ModelHash, header, data, all), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, ex.Message);
        }
    }

    // Click on the map: the ray in world space, through the object's placement.
    public bool Click(Vector3 origin, Vector3 direction, bool add)
    {
        if (Entity is null || !Matrix4x4.Invert(Entity.Transform, out var toModel))
            return false;
        return ClickModel(Vector3.Transform(origin, toModel), Vector3.TransformNormal(direction, toModel), add);
    }

    // Click: the piece under the ray (model space) replaces the selection, or is added / removed with add.
    public bool ClickModel(Vector3 localOrigin, Vector3 localDirection, bool add)
    {
        if (ModelParts.Hit(_meshes, localOrigin, localDirection) is not { } found)
        {
            if (!add && _selected.Count > 0)
            {
                _selected.Clear();
                Revision++;
            }
            return false;
        }
        Revision++;
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

    public void Clear()
    {
        _selected.Clear();
        Revision++;
    }

    // Edges of the selected triangles, in the model's space (moved by preview while the piece is dragged).
    public IEnumerable<(Vector3 From, Vector3 To)> Edges(Matrix4x4? preview = null)
    {
        int edges = 0;
        var m = preview ?? Matrix4x4.Identity;
        foreach (var (mesh, t) in _selected)
        {
            var (p, idx) = _meshes[mesh];
            var a = Vector3.Transform(p[idx[t * 3]], m);
            var b = Vector3.Transform(p[idx[t * 3 + 1]], m);
            var c = Vector3.Transform(p[idx[t * 3 + 2]], m);
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

    // Changes made in memory and not saved yet, last one last: the file changed and how to undo it.
    private enum ChangeKind { Removed, Moved, Texture }

    private sealed record Change(ChangeKind Kind, string File, Action Undo);
    private readonly List<Change> _changes = [];

    // Texture dictionaries (.ytd) read for a change of their textures, by path.
    private readonly Dictionary<string, (ResourceHeader Header, byte[] Content)> _dictionaries = new(StringComparer.OrdinalIgnoreCase);

    public int ChangeCount => _changes.Count;
    public int RemovedCount => _changes.Count(c => c.Kind == ChangeKind.Removed);
    public int MovedCount => _changes.Count(c => c.Kind == ChangeKind.Moved);
    public int TextureChangeCount => _changes.Count(c => c.Kind == ChangeKind.Texture);

    public ResourceHeader Header => _header;

    // The model's data with the changes so far (not saved).
    public byte[] Content => _content;

    // Copies of the texture dictionaries changed so far (not saved).
    public Dictionary<string, (ResourceHeader Header, byte[] Content)> ChangedDictionaries() =>
        _dictionaries.Where(d => _changes.Any(c => Same(c.File, d.Key)))
            .ToDictionary(d => d.Key, d => (d.Value.Header, (byte[])d.Value.Content.Clone()), StringComparer.OrdinalIgnoreCase);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The texture of this file (the model itself for its embedded textures, or a .ytd), with the data holding it.
    public (StoredTexture Texture, byte[] Content)? FindTexture(WorldLoader world, string source, uint nameHash, out string? error)
    {
        error = null;
        try
        {
            byte[] content;
            List<StoredTexture> textures;
            if (Same(source, Path))
            {
                content = _content;
                textures = DrawableReader.StoredTextures(_content, _header, System.IO.Path.GetExtension(Path), _modelHash);
            }
            else
            {
                if (!_dictionaries.TryGetValue(source, out var dictionary))
                {
                    if (world.Content?.ReadFile(source) is not { } data || world.Content.GetResourceHeader(source) is not { } header)
                    {
                        error = L("Dictionnaire de textures introuvable.");
                        return null;
                    }
                    _dictionaries[source] = dictionary = (header, data);
                }
                content = dictionary.Content;
                textures = YtdReader.StoredTextures(dictionary.Content, dictionary.Header);
            }
            if (textures.FirstOrDefault(t => t.NameHash == nameHash) is not { } texture)
            {
                error = L("Texture introuvable dans son fichier.");
                return null;
            }
            return (texture, content);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // Puts new pixels (from TextureImport.Convert) in the texture, in memory; the file is written by Save.
    public string? ReplaceTexture(WorldLoader world, string source, uint nameHash, byte[] pixels)
    {
        if (FindTexture(world, source, nameHash, out var error) is not var (texture, content))
            return error;
        var before = texture.Write(content, pixels);
        _changes.Add(new Change(ChangeKind.Texture, Same(source, Path) ? Path : source, () => texture.Restore(content, before)));
        return null;
    }

    // Gives a texture of a .ytd another size (from TextureImport.Resize): the dictionary is written again whole, in
    // memory; the file is written by Save. Returns an error message, or null.
    public string? ResizeTexture(WorldLoader world, string source, TextureData resized)
    {
        if (Same(source, Path))
            return L("Cette texture est dans le fichier du modèle : sa taille ne peut pas encore changer (seulement celles des .ytd).");
        if (FindTexture(world, source, resized.NameHash, out var error) is null)
            return error;
        var old = _dictionaries[source];
        try
        {
            var (header, content) = Data.Native.Resources.ResourceFile.Read(YtdReader.Rebuild(old.Content, old.Header, resized));
            _dictionaries[source] = (header, content);
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
        _changes.Add(new Change(ChangeKind.Texture, source, () => _dictionaries[source] = old));
        return null;
    }

    // Removes the chosen piece from the model in memory (and the same place of its simpler versions); the file is
    // written by Save. Returns the triangles removed and how many of them are in the simpler versions.
    public (int Count, int Farther) RemoveSelected()
    {
        if (_selected.Count == 0)
            return (0, 0);
        var triangles = ChosenTriangles(out int farther);
        var before = DrawablePatch.HideTriangles(_content, triangles.Select(t => (t.Geometry.IndicesAt, t.Triangle)));
        _changes.Add(new Change(ChangeKind.Removed, Path, () => DrawablePatch.Restore(_content, before)));
        Refresh();
        int count = _selected.Count;
        Clear();
        return (count, farther);
    }

    // The chosen triangles, and those of the simpler versions of the model (seen from farther away) in the same place.
    private List<(DrawableReader.StoredGeometry Geometry, int Triangle)> ChosenTriangles(out int farther)
    {
        var triangles = _selected.Select(s => (Geometry: _shown[s.Mesh], s.Triangle)).ToList();
        var (min, max) = SelectionBox();
        min -= new Vector3(BoxMargin);
        max += new Vector3(BoxMargin);
        farther = 0;
        foreach (var geometry in _all.Where(g => !_shown.Contains(g)))
        {
            foreach (int t in ModelParts.Inside(geometry.Positions, geometry.Indices, min, max))
            {
                triangles.Add((geometry, t));
                farther++;
            }
        }
        return triangles;
    }

    private (Vector3 Min, Vector3 Max) SelectionBox()
    {
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var group in _selected.GroupBy(s => s.Mesh))
        {
            var (positions, indices) = _meshes[group.Key];
            var box = ModelParts.Box(positions, indices, group.Select(s => s.Triangle));
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }
        return (min, max);
    }

    // The middle of the chosen piece (model space), null when nothing is chosen.
    public Vector3? SelectionCenter
    {
        get
        {
            if (_selected.Count == 0)
                return null;
            var (min, max) = SelectionBox();
            return (min + max) * 0.5f;
        }
    }

    // Moves and turns the chosen piece (and the same place of the simpler versions) in memory; the piece stays chosen.
    // Returns an error message, or null.
    public string? MoveSelected(Matrix4x4 move)
    {
        if (_selected.Count == 0 || move.IsIdentity)
            return null;
        var parts = ChosenTriangles(out _)
            .GroupBy(t => t.Geometry)
            .Select(g => (g.Key, (IReadOnlyCollection<int>)g
                .SelectMany(t => new[] { g.Key.Indices[t.Triangle * 3], g.Key.Indices[t.Triangle * 3 + 1], g.Key.Indices[t.Triangle * 3 + 2] })
                .Select(i => (int)i).ToHashSet()))
            .ToList();
        if (DrawablePatch.MoveVertices(_content, parts, move) is not { } before)
            return L("Les sommets de ce modèle sont dans un format que NodeRunner ne sait pas encore modifier.");
        _changes.Add(new Change(ChangeKind.Moved, Path, () => DrawablePatch.Restore(_content, before)));
        Refresh();
        Revision++;
        return null;
    }

    // Undoes the last change made in memory (a piece removed, a texture replaced).
    public bool UndoLast()
    {
        if (_changes.Count == 0)
            return false;
        var last = _changes[^1];
        last.Undo();
        _changes.RemoveAt(_changes.Count - 1);
        if (last.Kind == ChangeKind.Texture)
            return true;
        Refresh();
        if (last.Kind == ChangeKind.Moved)
            Revision++;
        else
            Clear();
        return true;
    }

    // The geometries as the data now holds them (emptied triangles are not picked any more, moved pieces are where they
    // went).
    private void Refresh() =>
        (_all, _shown, _meshes) = Split(DrawableReader.StoredGeometries(_content, _header, _extension, _modelHash));

    // Writes the changed files (the model, its texture dictionaries) into the project's stream folder, and reloads the
    // map once.
    public (bool Done, string Message) Save(AppState state)
    {
        if (state.World.ProjectFiles is null && state.SaveFolder is null)
            return (false, L("Ouvrez ou créez un projet : le modèle modifié y est enregistré (dossier stream)."));
        var files = new List<(string Path, ResourceHeader Header, byte[] Content)>();
        if (_changes.Any(c => Same(c.File, Path)))
            files.Add((Path, _header, _content));
        foreach (var (path, (header, content)) in _dictionaries)
        {
            if (_changes.Any(c => Same(c.File, path)))
                files.Add((path, header, content));
        }
        foreach (var (path, header, content) in files)
        {
            if (WriteFile(state, path, header, content) is { } error)
                return (false, error);
        }
        int pieces = RemovedCount, moved = MovedCount, textures = TextureChangeCount;
        _changes.Clear();
        _dictionaries.Clear();
        state.World.RequestMapReload();
        string names = string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f.Path)));
        return (true, moved == 0 && textures == 0 ? L($"{names} enregistré ({pieces} morceau(x) retiré(s)). Rechargement de la carte...")
            : moved == 0 ? L($"{names} enregistré(s) ({pieces} morceau(x) retiré(s), {textures} texture(s) remplacée(s)). Rechargement de la carte...")
            : L($"{names} enregistré(s) ({pieces} morceau(x) retiré(s), {moved} déplacement(s), {textures} texture(s) remplacée(s)). Rechargement de la carte..."));
    }

    // One file into the project's stream folder; an error message, or null.
    private static string? WriteFile(AppState state, string path, ResourceHeader header, byte[] content)
    {
        string? target = state.World.ProjectFiles?.StreamFileOf(path);
        if (target is null)
        {
            if (state.SaveFolder is not { } stream)
                return L("Ouvrez ou créez un projet : le modèle modifié y est enregistré (dossier stream).");
            target = System.IO.Path.Combine(stream, System.IO.Path.GetFileName(path));
        }
        try
        {
            var bytes = Data.Native.Resources.ResourceFile.Write(header, content);
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
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return L($"Enregistrement impossible : {ex.Message}");
        }
    }

    // Map tool: removes the chosen piece and writes the file at once.
    public (bool Done, string Message) Apply(AppState state)
    {
        if (_selected.Count == 0)
            return (false, L("Cliquez d'abord sur le morceau à retirer."));
        var (count, farther) = RemoveSelected();
        var (done, message) = Save(state);
        if (!done)
            return (false, message);
        string fileName = System.IO.Path.GetFileName(Path);
        return (true, farther > 0
            ? L($"{count} triangles retirés de {fileName} (+ {farther} dans ses versions simplifiées). Rechargement de la carte...")
            : L($"{count} triangles retirés de {fileName}. Rechargement de la carte..."));
    }
}
