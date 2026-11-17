using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Cutscenes;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.App.Ui;

// Cutscene viewer: the game's cutscenes listed in Scene, the one chosen played on the map at its place, with a timeline
// over the bottom of the 3D view.
internal sealed class CutscenePanel(AppState state)
{
    private const float FrameStep = 1f / 30f;

    private sealed record Entry(string Name, string Path, bool Online);

    private readonly PlaybackBar _bar = new("cutscene_timeline");
    private GameContent? _for;
    private Task<List<Entry>>? _list;
    private string _search = "";
    private int _filter; // 0 all, 1 story, 2 Online.
    private (string Search, int Filter)? _rowsFor;
    private List<Entry> _rows = [];

    private static string[] Filters => [L("Toutes"), L("Histoire"), L("Online")];

    private CutscenePlayer Player => state.Cutscenes;

    // The view follows the cutscene's camera (else the map's camera, free).
    private bool _film = true;
    private bool _filmNear;
    private const float FilmNearPlane = 0.05f;
    private float _freeNearPlane = 0.5f;

    public void Open()
    {
        if (state.IsGta4)
        {
            state.Notify(L("La visionneuse de cinématiques est réservée à GTA V."), warning: true);
            return;
        }
        state.CutsceneOpen = true;
    }

    public void Close()
    {
        Player.Close();
        FollowFilm();
        state.CutsceneOpen = false;
    }

    // Once a frame before the interface: the time moves on, the camera goes to a cutscene just read.
    public void BeginFrame()
    {
        if (state.CutsceneOpen && state.IsGta4)
            Close();
        if (Player.Cutscene is not { } cutscene)
            return;
        if (Player.JustLoaded)
        {
            Player.JustLoaded = false;
            _bar.Start();
            GoTo(cutscene);
        }
        _bar.Advance(cutscene.Duration);
        Player.Time = _bar.ShownTime(cutscene.Duration);
        FollowFilm();
    }

    // Puts the map's camera where the cutscene's is (close shots: a near plane closer than on the map).
    private void FollowFilm()
    {
        bool film = _film && state.CutsceneOpen && !state.MapCovered && Player.Shot() is not null;
        if (film && Player.Shot() is { } shot)
        {
            if (!_filmNear)
            {
                _freeNearPlane = state.Camera.NearPlane;
                _filmNear = true;
            }
            state.Camera.NearPlane = FilmNearPlane;
            state.Camera.Position = shot.Position;
            state.Camera.LookAt(shot.Position + shot.Forward);
            state.Camera.FieldOfView = Math.Clamp(shot.FieldOfView, 5f, 120f) * MathF.PI / 180f;
        }
        else if (_filmNear)
        {
            state.Camera.NearPlane = _freeNearPlane;
            _filmNear = false;
        }
    }

    // Where the first shot is taken from, looking at the scene; else just in front of the scene.
    private void GoTo(Cutscene cutscene)
    {
        var target = cutscene.Offset + new Vector3(0f, 0f, 1f);
        var shot = cutscene.Events.FirstOrDefault(e => e.Kind == CutsceneEventKind.CameraCut)?.Args?.Position;
        if (shot is { } local && local != Vector3.Zero && Vector3.Transform(local, cutscene.SceneTransform) is var from
            && Vector3.Distance(from, target) is > 0.5f and < 200f)
        {
            state.Camera.Position = from;
            state.Camera.LookAt(target);
        }
        else
            state.FocusOn(target, 6f);
    }

    private List<Entry>? Entries()
    {
        if (state.World.Content is not { } content)
            return null;
        if (!ReferenceEquals(content, _for))
        {
            _for = content;
            _rowsFor = null;
            _list = Task.Run(() => content.EnumerateFiles(".cut")
                .Select(p => new Entry(Path.GetFileNameWithoutExtension(p), p,
                    p.Contains(@"\dlcpacks\mp", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\cuts_mp", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        return _list is { IsCompletedSuccessfully: true } listed ? listed.Result : null;
    }

    // ---- Scene panel ----

    public unsafe void DrawList()
    {
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(L($"{Icons.Film}  Cinématiques"));
        ImGui.PopFont();
        ImGui.SameLine();
        float close = ImGui.CalcTextSize(L($"{Icons.X}  Fermer")).X + ImGui.GetStyle().FramePadding.X * 2f;
        float room = ImGui.GetContentRegionAvail().X;
        if (room > close)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - close);
        if (Widgets.Button(L($"{Icons.X}  Fermer"), L("Ferme la visionneuse de cinématiques : le panneau Scène revient à la carte")))
        {
            Close();
            return;
        }
        DrawCurrent();
        ImGui.Separator();

        if (Entries() is not { } entries)
        {
            Widgets.Hint(state.World.Content is null ? L("La carte se charge...") : L($"{Icons.LoaderCircle}  Liste des cinématiques..."));
            return;
        }
        int filter = _filter;
        if (Widgets.Segmented("cutscene_filter", Filters, ref filter,
                [L("Toutes les cinématiques de l'installation"), L("Celles du mode histoire et des mises à jour solo"),
                 L("Celles des mises à jour Online présentes dans l'installation (lues hors ligne)")]))
            _filter = filter;
        Widgets.SearchField("##cutscene_search", L("Rechercher (ex. fbi_5, trevor, heist)"), ref _search);
        if (_rowsFor != (_search, _filter))
        {
            var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _rows = entries.Where(e => (_filter == 0 || _filter == 2 == e.Online)
                                       && words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            _rowsFor = (_search, _filter);
        }
        Widgets.Hint(L($"{_rows.Count} cinématiques"));

        if (!ImGui.BeginChild("##cutscene_list", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_rows.Count, ImGui.GetTextLineHeightWithSpacing());
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = _rows[i];
                if (ImGui.Selectable($"{Icons.Film}  {entry.Name}##cutscene_{i}", entry.Path == Player.Path))
                    Player.Open(state.World, entry.Path);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(entry.Path);
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    // The cutscene chosen: what it is, where, and a button to go there.
    private void DrawCurrent()
    {
        if (Player.Error is { } error)
        {
            ImGui.TextColored(UiTheme.Error, L($"{Icons.TriangleAlert}  {error}"));
            return;
        }
        if (Player.Cutscene is not { } cutscene)
        {
            Widgets.Hint(Player.Path is null ? L("Choisis une cinématique : elle se joue sur la carte, à sa place.") : L($"{Icons.LoaderCircle}  Lecture..."), wrap: true);
            return;
        }
        ImGui.TextUnformatted(cutscene.Name);
        int peds = cutscene.Objects.Count(o => o.Kind == CutsceneObjectKind.Ped);
        int props = cutscene.Objects.Count(o => o.Kind is CutsceneObjectKind.Prop or CutsceneObjectKind.Weapon);
        int vehicles = cutscene.Objects.Count(o => o.Kind == CutsceneObjectKind.Vehicle);
        Widgets.Hint(L($"{cutscene.Duration:0.0} s · {cutscene.CameraCuts.Count + 1} plans · {peds} personnages · {props} objets · {vehicles} véhicules"));
        Widgets.Hint(Player.IsLoading ? L($"{Icons.LoaderCircle}  Chargement des modèles...") : L($"{Player.ShownCount} / {Player.ActorCount} affichés à cet instant"));
        if (Widgets.Button(L($"{Icons.Map}  Aller à la scène"), L("Place la caméra au point de vue du premier plan")))
            GoTo(cutscene);
    }

    // ---- Timeline over the bottom of the 3D view ----

    public void DrawTimeline()
    {
        if (!state.CutsceneOpen || state.MapCovered || Player.Cutscene is not { } cutscene)
            return;
        var (position, size) = DockLayout.SceneArea;
        float width = Math.Min(size.X - 24f, 980f);
        float height = PlaybackBar.Height + 16f;
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowPos(new Vector2(position.X + (size.X - width) * 0.5f, position.Y + size.Y - height - 12f));
        ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 6));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        bool visible = ImGui.Begin("##cutscene_timeline", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (visible)
        {
            OverlayStyle.WindowGlass(strong: true, rounding: ImGui.GetStyle().WindowRounding);
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                _bar.Keys(cutscene.Duration, FrameStep);
            var (cut, _) = cutscene.CutAt(Player.Time);
            _bar.Draw(cutscene.Duration, FrameStep, L($"plan {cut + 1} / {cutscene.CameraCuts.Count + 1}"), cutscene.CameraCuts, () =>
            {
                if (Widgets.IconButton(Icons.Video, "cutscene_film", _film
                        ? L("Caméra du film : la vue suit la caméra de la cinématique (clic : caméra libre)")
                        : L("Caméra libre : la vue se déplace comme sur la carte (clic : caméra du film)"), active: _film))
                    _film = !_film;
            });
        }
        ImGui.End();
    }
}
