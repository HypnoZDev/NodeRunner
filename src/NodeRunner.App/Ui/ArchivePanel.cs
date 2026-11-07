using System.Numerics;
using System.Text;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using Node = NodeRunner.App.World.ArchiveTree.Node;

namespace NodeRunner.App.Ui;

// Dockable "Explorateur d'archives" panel: the game's archives as a tree (or a search through every file), the size
// and type of each file, and actions by type: model in the viewer, YMAP on the map, text shown, extraction to disk.
internal sealed unsafe class ArchivePanel(AppState state, Action<string> openInViewer, Action<MapData> revealMap)
{
    public const string Title = $"{Icons.Package}  Explorateur d'archives###Archives";
    private const string TextTitle = $"{Icons.File}  Texte###ArchiveText";
    private const int MaxSearchResults = 5000;
    private const int MaxTextBytes = 2 * 1024 * 1024;

    private Task<ArchiveTree>? _building;
    private ArchiveTree? _tree;
    private RpfFileSystem? _treeFiles;
    private readonly HashSet<Node> _expanded = [];
    private List<Node> _rows = [];
    private bool _rowsDirty = true;
    private string _search = "";
    private string _rowsSearch = "";
    private Node? _selected;

    private string? _textName;
    private string _text = "";
    private bool _showText;

    private Task<(int Written, int Failed)>? _extraction;
    private string _extractionName = "";
    private string _extractionFolder = "";
    private (int Done, int Total) _extractionProgress;
    private CancellationTokenSource? _extractionCancel;

    public Node? ExtractRequested { get; set; }

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(760f, 560f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(L(Title), ref open))
        {
            UpdateTree();
            DrawContent();
        }
        ImGui.End();
        if (_showText)
            DrawTextWindow();
    }

    private void UpdateTree()
    {
        var files = state.World.Files;
        if (files != _treeFiles)
        {
            _treeFiles = files;
            _tree = null;
            _selected = null;
            _expanded.Clear();
            _rowsDirty = true;
            _building = files is null ? null : Task.Run(() => ArchiveTree.Build(files));
        }
        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            _tree = finished.IsCompletedSuccessfully ? finished.Result : null;
            _rowsDirty = true;
        }
    }

    private void DrawContent()
    {
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (Widgets.SearchField("##archive_search", "Chercher un fichier (ex. prop_bench, hw1_26, .ymt)", ref _search))
            _rowsDirty = true;

        if (_tree is null)
        {
            Widgets.Hint(state.World.Files is null ? "En attente de l'ouverture du jeu..." : "Lecture du contenu des archives...");
            return;
        }

        if (_rowsDirty || _rowsSearch != _search)
            BuildRows();
        bool searching = _search.Trim().Length >= 2;
        Widgets.Hint(searching
            ? (_rows.Count == 0 ? "Aucun fichier" : _rows.Count >= MaxSearchResults ? $"{Widgets.Count(MaxSearchResults)} premiers fichiers" : $"{Widgets.Count(_rows.Count)} fichier(s)")
            : $"{Widgets.Count(_tree.AllFiles.Count)} fichiers dans les archives du jeu");

        DrawExtractionProgress();

        float infoHeight = _selected is null ? 0 : ImGui.GetTextLineHeightWithSpacing() * 2 + 8;
        if (ImGui.BeginTable("##archive_rows", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, -infoHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Nom", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Taille", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableHeadersRow();

            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(_rows.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRow(_rows[i], searching);
            }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }

        if (_selected is { } selected)
            DrawInfo(selected);
    }

    private void BuildRows()
    {
        _rowsDirty = false;
        _rowsSearch = _search;
        _rows = [];
        string search = _search.Trim();
        if (search.Length >= 2)
        {
            foreach (var file in _tree!.AllFiles)
            {
                if (file.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    _rows.Add(file);
                    if (_rows.Count >= MaxSearchResults)
                        break;
                }
            }
            return;
        }
        AddVisible(_tree!.Root);
    }

    private void AddVisible(Node node)
    {
        foreach (var child in node.Children)
        {
            _rows.Add(child);
            if (!child.IsFile && _expanded.Contains(child))
                AddVisible(child);
        }
    }

    private void Toggle(Node node)
    {
        if (!_expanded.Remove(node))
            _expanded.Add(node);
        _rowsDirty = true;
    }

    private void DrawRow(Node node, bool searching)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(node.Path);

        if (!searching)
            ImGui.Indent(node.Depth * 16f + 1f);
        string icon = node.IsFile ? Icons.File : node.IsArchive ? Icons.Package : Icons.FolderOpen;
        string chevron = node.IsFile || searching ? "   " : _expanded.Contains(node) ? Icons.ChevronDown : Icons.ChevronRight;
        string label = searching ? $"{icon}  {node.Path}" : $"{chevron} {icon}  {node.Name}";
        if (ImGui.Selectable(label, _selected == node, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            _selected = node;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                if (node.IsFile)
                    DefaultAction(node);
                else
                    Toggle(node);
            }
            else if (!node.IsFile && !searching && ImGui.GetMousePos().X < ImGui.GetItemRectMin().X + 22f)
            {
                Toggle(node); // click on the chevron
            }
        }
        if (ImGui.BeginPopupContextItem("##archive_menu"))
        {
            _selected = node;
            DrawMenu(node);
            ImGui.EndPopup();
        }
        if (!searching)
            ImGui.Unindent(node.Depth * 16f + 1f);

        ImGui.TableNextColumn();
        if (node.IsFile)
        {
            if (_tree!.Files.GetInfo(node.Path) is { } info)
                ImGui.TextUnformatted(Size(info.Size));
        }
        else
        {
            ImGui.TextDisabled($"{Widgets.Count(node.FileCount)} fich.");
        }
        ImGui.TableNextColumn();
        ImGui.TextDisabled(node.IsFile ? Kind(node.Name) : node.IsArchive ? "Archive RPF" : "Dossier");
        ImGui.PopID();
    }

    private void DrawMenu(Node node)
    {
        if (node.IsFile)
        {
            string extension = Path.GetExtension(node.Name).ToLowerInvariant();
            if (extension is ".ydr" or ".yft" && ImGui.MenuItem($"{Icons.Box}  Voir dans la visionneuse"))
                openInViewer(node.Path);
            if (extension == ".ymap" && ImGui.MenuItem($"{Icons.MapPinned}  Aller sur la carte"))
                GoToMap(node);
            if (IsTextFile(node.Name) && ImGui.MenuItem($"{Icons.File}  Afficher le texte"))
                ShowText(node);
            if (ImGui.MenuItem($"{Icons.ArrowDownToLine}  Extraire..."))
                ExtractRequested = node;
        }
        else if (ImGui.MenuItem($"{Icons.ArrowDownToLine}  Extraire le dossier ({Widgets.Count(node.FileCount)} fichiers)...", null, false, _extraction is null))
        {
            ExtractRequested = node;
        }
        if (ImGui.MenuItem($"{Icons.Copy}  Copier le chemin"))
        {
            ImGui.SetClipboardText(node.Path);
            state.Notify("Chemin copié");
        }
    }

    private void DefaultAction(Node node)
    {
        switch (Path.GetExtension(node.Name).ToLowerInvariant())
        {
            case ".ydr" or ".yft":
                openInViewer(node.Path);
                break;
            case ".ymap":
                GoToMap(node);
                break;
            default:
                if (IsTextFile(node.Name))
                    ShowText(node);
                break;
        }
    }

    private void GoToMap(Node node)
    {
        var map = state.World.Maps?.Maps.FirstOrDefault(m => string.Equals(m.FilePath, node.Path, StringComparison.OrdinalIgnoreCase));
        if (map is null)
        {
            state.Notify("Ce YMAP n'est pas affiché dans la carte chargée (version remplacée par un DLC, ou carte de mission masquée)", warning: true);
            return;
        }
        var size = map.EntitiesExtentsMax - map.EntitiesExtentsMin;
        state.FocusOn((map.EntitiesExtentsMin + map.EntitiesExtentsMax) * 0.5f, Math.Max(size.Length() * 0.5f, 10f));
        revealMap(map);
    }

    private static readonly HashSet<string> TextExtensions =
        [".xml", ".meta", ".txt", ".dat", ".ini", ".lua", ".json", ".csv", ".cfg", ".ide", ".ipl", ".log", ".pso.xml"];

    private static bool IsTextFile(string name) => TextExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    private void ShowText(Node node)
    {
        try
        {
            var bytes = _tree!.Files.ReadFile(node.Path) ?? [];
            bool truncated = bytes.Length > MaxTextBytes;
            var span = bytes.AsSpan(0, Math.Min(bytes.Length, MaxTextBytes));
            int printable = 0;
            foreach (byte b in span)
            {
                if (b >= 32 || b is 9 or 10 or 13)
                    printable++;
            }
            _text = span.Length > 0 && printable < span.Length * 0.9
                ? "(Ce fichier n'est pas du texte : données binaires.)"
                : Encoding.UTF8.GetString(span) + (truncated ? "\n\n(... fichier tronqué à 2 Mo)" : "");
            _textName = node.Path;
            _showText = true;
            ImGui.SetWindowFocus(TextTitle);
        }
        catch (InvalidDataException ex)
        {
            state.Notify($"Lecture impossible : {ex.Message}", warning: true);
        }
    }

    private void DrawTextWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(720f, 520f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(L(TextTitle), ref _showText, ImGuiWindowFlags.NoDocking))
        {
            Widgets.Hint(_textName ?? "");
            ImGui.SameLine();
            if (Widgets.Button("Copier le texte"))
                ImGui.SetClipboardText(_text);
            ImGui.InputTextMultiline("##archive_text", ref _text, (uint)_text.Length + 1, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.End();
    }

    private void DrawInfo(Node node)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(node.Path);
        if (node.IsFile && _tree!.Files.GetInfo(node.Path) is { } info)
        {
            string details = $"{Kind(node.Name)} · {Size(info.Size)}";
            if (info.StoredSize != info.Size)
                details += $" ({Size(info.StoredSize)} dans l'archive)";
            if (info.IsResource)
                details += $" · ressource version {info.ResourceVersion}";
            if (info.IsEncrypted)
                details += " · chiffré";
            Widgets.Hint(details);
        }
        else
        {
            Widgets.Hint($"{(node.IsArchive ? "Archive RPF" : "Dossier")} · {Widgets.Count(node.FileCount)} fichiers");
        }
    }

    public void Extract(Node node, string target)
    {
        if (_tree is not { } tree)
            return;
        if (node.IsFile)
        {
            try
            {
                if (tree.Extract(node.Path) is { } bytes)
                {
                    File.WriteAllBytes(target, bytes);
                    state.Notify($"{node.Name} extrait");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                state.Notify($"Extraction impossible : {ex.Message}", warning: true);
            }
            return;
        }

        if (_extraction is not null)
            return;
        _extractionName = node.Name;
        _extractionFolder = target;
        _extractionProgress = (0, node.FileCount);
        _extractionCancel = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p => _extractionProgress = p);
        var cancel = _extractionCancel.Token;
        _extraction = Task.Run(() => tree.ExtractAll(node, target, progress, cancel), cancel);
    }

    private void DrawExtractionProgress()
    {
        if (_extraction is not { } task)
            return;
        if (task.IsCompleted)
        {
            _extraction = null;
            if (task.IsCompletedSuccessfully)
            {
                var (written, failed) = task.Result;
                state.Notify(failed == 0 ? $"{_extractionName} : {Widgets.Count(written)} fichiers extraits"
                    : $"{_extractionName} : {Widgets.Count(written)} fichiers extraits, {Widgets.Count(failed)} illisibles", warning: failed > 0);
            }
            else
            {
                state.Notify($"Extraction de {_extractionName} arrêtée", warning: true);
            }
            return;
        }
        var (done, total) = _extractionProgress;
        ImGui.ProgressBar(total == 0 ? 0 : (float)done / total, new Vector2(-90f, 0),
            $"Extraction de {_extractionName} : {Widgets.Count(done)} / {Widgets.Count(total)}");
        ImGui.SameLine();
        if (Widgets.Button("Arrêter", width: 80f))
            _extractionCancel?.Cancel();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Vers {_extractionFolder}");
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} Mo",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} Go",
    };

    private static string Kind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".ydr" => "Modèle",
        ".ydd" => "Modèles (dictionnaire)",
        ".yft" => "Modèle (véhicule, cassable)",
        ".ytd" => "Textures",
        ".ymap" => "Carte (YMAP)",
        ".ytyp" => "Types (YTYP)",
        ".ybn" => "Collision",
        ".ynv" => "Navmesh",
        ".ynd" => "Pathnodes",
        ".ymt" => "Données (YMT)",
        ".ymf" => "Manifeste (YMF)",
        ".ycd" => "Animations",
        ".awc" => "Audio",
        ".gxt2" => "Textes du jeu",
        ".ysc" => "Script",
        ".meta" or ".xml" => "Texte XML",
        ".txt" => "Texte",
        ".dat" => "Données",
        ".rpf" => "Archive RPF",
        "" => "Fichier",
        var other => other[1..].ToUpperInvariant(),
    };
}
