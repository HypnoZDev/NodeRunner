using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// Vehicle recording viewer: the game's recordings (.yvr) listed in Scene by mission, those ticked played together on
// the map, with a timeline over the bottom of the 3D view.
internal sealed class RecordingPanel(AppState state)
{
    private const float FrameStep = 1f / 30f;
    private const float FollowDistance = 9f;
    private const float FollowHeight = 3.5f;

    private readonly record struct Row(string Group, RecordingPlayer.Entry? Entry, int Count);

    private readonly PlaybackBar _bar = new("recording_timeline");
    private string _search = "";
    private readonly HashSet<string> _openGroups = new(StringComparer.OrdinalIgnoreCase);
    private List<RecordingPlayer.Entry>? _rowsFrom;
    private (string Search, int Opened)? _rowsFor;
    private List<Row> _rows = [];
    private Dictionary<string, List<RecordingPlayer.Entry>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private RecordingPlayer.Track? _followed;

    private RecordingPlayer Player => state.Recordings;

    public void Open()
    {
        if (!state.Features.CutsceneViewer)
        {
            state.Notify(L("La visionneuse d'enregistrements de véhicules est réservée à GTA V."), warning: true);
            return;
        }
        Player.Vehicles = state.Settings.RecordingVehicles;
        state.RecordingOpen = true;
    }

    public void Close()
    {
        Player.Close();
        _followed = null;
        _bar.Start();
        state.RecordingOpen = false;
    }

    // Once a frame before the interface: the time moves on, the camera goes to the first recording read or follows a car.
    public void BeginFrame()
    {
        if (state.RecordingOpen && !state.Features.CutsceneViewer)
            Close();
        if (!state.RecordingOpen)
            return;
        float duration = Player.Duration;
        if (Player.JustLoaded)
        {
            Player.JustLoaded = false;
            _bar.Start();
            if (Player.Tracks.FirstOrDefault(t => t.Recording is not null) is { } first)
                GoTo(first);
        }
        _bar.Advance(duration);
        Player.Time = _bar.ShownTime(duration);
        if (_followed is { } followed && !Player.Tracks.Contains(followed))
            _followed = null;
        if (_followed is { Recording: not null } car && !state.MapCovered)
        {
            var forward = Vector3.Transform(Vector3.UnitY, car.Pose.Rotation);
            forward = Vector3.Normalize(new Vector3(forward.X, forward.Y, 0f) + new Vector3(0f, 0f, 1e-4f));
            state.Camera.Position = car.Pose.Position - forward * FollowDistance + new Vector3(0f, 0f, FollowHeight);
            state.Camera.LookAt(car.Pose.Position + new Vector3(0f, 0f, 1f));
        }
    }

    private void GoTo(RecordingPlayer.Track track)
    {
        if (track.Recording is { Frames.Count: > 0 } recording)
            state.FocusOn(recording.Sample(Player.Time).Position, 8f);
    }

    // ---- Scene panel ----

    public unsafe void DrawList()
    {
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(L($"{Icons.Car}  Enregistrements de véhicules"));
        ImGui.PopFont();
        ImGui.SameLine();
        float close = ImGui.CalcTextSize(L($"{Icons.X}  Fermer")).X + ImGui.GetStyle().FramePadding.X * 2f;
        float room = ImGui.GetContentRegionAvail().X;
        if (room > close)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - close);
        if (Widgets.Button(L($"{Icons.X}  Fermer"), L("Ferme la visionneuse d'enregistrements : le panneau Scène revient à la carte")))
        {
            Close();
            return;
        }
        DrawShown();
        ImGui.Separator();

        if (Player.Entries(state.World) is not { } entries)
        {
            Widgets.Hint(state.World.Content is null ? L("La carte se charge...") : L($"{Icons.LoaderCircle}  Liste des enregistrements..."));
            return;
        }
        Widgets.SearchField("##recording_search", L("Rechercher une mission (ex. fam1, fbi4, trv)"), ref _search);
        if (!ReferenceEquals(_rowsFrom, entries) || _rowsFor != (_search, _openGroups.Count))
            BuildRows(entries);
        Widgets.Hint(L($"{_rows.Count(r => r.Entry is null)} missions · {entries.Count} enregistrements"));

        if (!ImGui.BeginChild("##recording_list", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_rows.Count, ImGui.GetFrameHeightWithSpacing());
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(_rows[i], i);
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    // One row per mission (group of recordings), then its recordings when it is open. A search opens the groups found.
    private void BuildRows(List<RecordingPlayer.Entry> entries)
    {
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _rows = [];
        if (!ReferenceEquals(_rowsFrom, entries))
            _groups = entries.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in _groups.Values)
        {
            var found = group.Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (found.Count == 0)
                continue;
            string key = group[0].Group;
            _rows.Add(new Row(key, null, found.Count));
            if (_openGroups.Contains(key))
                _rows.AddRange(found.Select(e => new Row(key, e, 0)));
        }
        _rowsFrom = entries;
        _rowsFor = (_search, _openGroups.Count);
    }

    private void DrawRow(Row row, int index)
    {
        if (row.Entry is not { } entry)
        {
            bool open = _openGroups.Contains(row.Group);
            var members = _groups.GetValueOrDefault(row.Group) ?? [];
            int shown = members.Count(e => Player.Find(e) is not null);
            bool all = shown > 0 && shown == row.Count;
            if (ImGui.Checkbox($"##recording_group_{index}", ref all))
            {
                foreach (var member in members.Where(Matches).ToList())
                    Player.Show(member, all);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(L("Joue (ou retire) tous les enregistrements de cette mission"));
            ImGui.SameLine();
            string played = shown > 0 ? L($" · {shown} joués") : "";
            if (ImGui.Selectable($"{(open ? Icons.ChevronDown : Icons.ChevronRight)}  {row.Group}  ({row.Count}{played})###recording_g_{index}", false))
            {
                if (!_openGroups.Remove(row.Group))
                    _openGroups.Add(row.Group);
            }
            return;
        }
        ImGui.Indent();
        var track = Player.Find(entry);
        bool on = track is not null;
        if (ImGui.Checkbox($"{entry.Name}##recording_{index}", ref on))
            Player.Show(entry, on);
        if (track is not null)
        {
            ImGui.SameLine();
            ColorDot(track.Color);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(entry.Path);
        ImGui.Unindent();
    }

    private bool Matches(RecordingPlayer.Entry entry) =>
        _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(w => entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static void ColorDot(Vector4 color)
    {
        float size = ImGui.GetTextLineHeight() * 0.6f;
        var at = ImGui.GetCursorScreenPos() + new Vector2(size * 0.5f, (ImGui.GetFrameHeight() - size) * 0.5f);
        ImGui.GetWindowDrawList().AddRectFilled(at, at + new Vector2(size), ImGui.GetColorU32(color), size * 0.25f);
        ImGui.Dummy(new Vector2(size * 2f, ImGui.GetFrameHeight()));
    }

    // The recordings played: their colour, length, a button to follow a car with the camera.
    private void DrawShown()
    {
        if (Player.Tracks.Count == 0)
        {
            Widgets.Hint(L("Coche un ou plusieurs enregistrements : ils se jouent ensemble sur la carte, chaque voiture sur son trajet."), wrap: true);
            return;
        }
        Widgets.Hint(L($"{Player.Tracks.Count} joués · {Player.Duration:0.0} s"));
        RecordingPlayer.Track? removed = null;
        foreach (var track in Player.Tracks)
        {
            ImGui.PushID(track.Entry.Path);
            ColorDot(track.Color);
            ImGui.SameLine();
            if (Widgets.IconButton(Icons.Video, "follow", _followed == track
                    ? L("La caméra suit cette voiture (clic : caméra libre)")
                    : L("Suivre cette voiture avec la caméra"), enabled: track.Recording is not null, active: _followed == track))
                _followed = _followed == track ? null : track;
            ImGui.SameLine();
            if (Widgets.IconButton(Icons.Crosshair, "goto", L("Aller à cette voiture"), enabled: track.Recording is not null))
            {
                _followed = null;
                GoTo(track);
            }
            ImGui.SameLine();
            if (Widgets.IconButton(Icons.X, "remove", L("Retirer cet enregistrement")))
                removed = track;
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (track.Error is { } error)
                ImGui.TextColored(UiTheme.Error, $"{track.Entry.Name} : {error}");
            else if (track.Recording is { } recording)
            {
                if (ImGui.Selectable(L($"{track.Entry.Name}  ·  {track.Vehicle}  ·  {track.Pose.Speed * 3.6f:0} km/h") + "###recording_pick", Player.Selected == track))
                    Player.Selected = Player.Selected == track ? null : track;
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(L($"{recording.Duration:0.0} s · clic : détails et choix du véhicule dans Propriétés"));
            }
            else
                ImGui.TextUnformatted(L($"{track.Entry.Name}  ·  {Icons.LoaderCircle} lecture..."));
            ImGui.PopID();
        }
        if (removed is not null)
            Player.Show(removed.Entry, false);
        if (Widgets.Button(L($"{Icons.Trash}  Tout retirer"), L("Retire tous les enregistrements joués")))
        {
            Player.Close();
            _followed = null;
            _bar.Start();
        }
    }

    // ---- Properties panel: the car chosen ----

    private string _vehicleSearch = "";

    public void DrawProperties()
    {
        if (Player.Selected is not { Recording: { } recording } track)
            return;
        ImGui.PushFont(UiTheme.SemiboldFont);
        ColorDot(track.Color);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L($"{Icons.Car}  Enregistrement {track.Entry.Name}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {track.Entry.Path}", wrap: true);
        Widgets.Hint(L($"Mission « {recording.Group} », numéro {recording.Number} · {recording.Duration:0.0} s · {recording.Frames.Count} points"));

        var pose = track.Pose;
        float heading = RecordingPlayer.Heading(pose.Rotation);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la voiture")))
        {
            _followed = null;
            GoTo(track);
        }
        ImGui.SameLine();
        string position = state.FormatPosition(pose.Position, heading);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), position))
            ImGui.SetClipboardText(position);
        ImGui.SameLine();
        string script = Script(recording);
        if (Widgets.Button(L($"{Icons.Copy}  Copier pour FiveM"), script))
            ImGui.SetClipboardText(script);

        ImGui.SeparatorText(L($"À {Player.Time:0.00} s"));
        if (ImGui.BeginTable("##recording_values", 2, ImGuiTableFlags.SizingStretchProp))
        {
            Value(L("Vitesse"), L($"{pose.Speed * 3.6f:0} km/h"));
            Value(L("Cap"), $"{heading:0.0}°");
            Value(L("Volant"), $"{pose.Steering * 180f / MathF.PI:0}°");
            Value(L("Accélérateur"), pose.Gas < 0f ? L($"{-pose.Gas * 100f:0} % (marche arrière)") : $"{pose.Gas * 100f:0} %");
            Value(L("Frein"), $"{pose.Brake * 100f:0} %");
            Value(L("Frein à main"), pose.Handbrake ? L("serré") : L("desserré"));
            Value(L("Position"), $"{pose.Position.X:0.00}, {pose.Position.Y:0.00}, {pose.Position.Z:0.00}");
            ImGui.EndTable();
        }

        ImGui.SeparatorText(L("Véhicule"));
        Widgets.Hint(L("Le fichier ne dit pas quel véhicule roule : c'est le script de la mission qui le choisit. Choisis-en un pour l'aperçu ; il est retenu pour cet enregistrement."), wrap: true);
        if (track.Model is null && track.Opened == track.Vehicle)
            ImGui.TextColored(UiTheme.Error, L($"{Icons.TriangleAlert}  Véhicule inconnu : {track.Vehicle}"));
        DrawVehicleChoice(track);
    }

    private static void Value(string name, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(name);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    // The lines a FiveM script uses to play this recording on a vehicle of its own.
    private static string Script(Data.Recordings.VehicleRecording recording) =>
        $"""
        RequestVehicleRecording({recording.Number}, "{recording.Group}")
        while not HasVehicleRecordingBeenLoaded({recording.Number}, "{recording.Group}") do Wait(0) end
        StartPlaybackRecordedVehicle(vehicle, {recording.Number}, "{recording.Group}", true)
        """;

    private void DrawVehicleChoice(RecordingPlayer.Track track)
    {
        Widgets.SearchField("##recording_vehicle", L($"Modèle (actuel : {track.Vehicle})"), ref _vehicleSearch);
        var names = Player.VehicleNames(state.World);
        string search = _vehicleSearch.Trim();
        var found = names.Where(n => search.Length == 0 || n.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        float rows = Math.Min(found.Count, 8);
        if (ImGui.BeginChild("##recording_vehicles", new Vector2(0f, (rows + 0.5f) * ImGui.GetTextLineHeightWithSpacing()), ImGuiChildFlags.Borders))
        {
            foreach (string name in found)
            {
                if (ImGui.Selectable(name, name == track.Vehicle))
                    SetVehicle([track], name);
            }
        }
        ImGui.EndChild();
        var group = Player.Tracks.Where(t => t.Recording?.Group == track.Recording?.Group && t.Vehicle != track.Vehicle).ToList();
        if (group.Count > 0 && Widgets.Button(L($"Mettre {track.Vehicle} sur les {group.Count} autres de la mission"),
                L("Même véhicule pour les autres enregistrements joués de cette mission")))
            SetVehicle(group, track.Vehicle);
    }

    private void SetVehicle(IEnumerable<RecordingPlayer.Track> tracks, string vehicle)
    {
        foreach (var track in tracks)
        {
            track.Vehicle = vehicle;
            if (vehicle == RecordingPlayer.DefaultVehicle)
                state.Settings.RecordingVehicles.Remove(track.Entry.Name);
            else
                state.Settings.RecordingVehicles[track.Entry.Name] = vehicle;
        }
        state.SaveSettings();
    }

    // ---- Timeline over the bottom of the 3D view ----

    public void DrawTimeline()
    {
        float duration = Player.Duration;
        if (!state.RecordingOpen || state.MapCovered || duration <= 0f)
            return;
        var (position, size) = DockLayout.SceneArea;
        float width = Math.Min(size.X - 24f, 980f);
        float height = PlaybackBar.Height + 16f;
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowPos(new Vector2(position.X + (size.X - width) * 0.5f, position.Y + size.Y - height - 12f));
        ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 6));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        bool visible = ImGui.Begin("##recording_timeline", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (visible)
        {
            OverlayStyle.WindowGlass(strong: true, rounding: ImGui.GetStyle().WindowRounding);
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                _bar.Keys(duration, FrameStep);
            _bar.Draw(duration, FrameStep, L($"{Player.Tracks.Count} véhicules"), null, () =>
            {
                if (Widgets.IconButton(Icons.Video, "recording_follow", _followed is not null
                        ? L("La caméra suit une voiture (clic : caméra libre)")
                        : L("Caméra libre (clic : suivre la première voiture)"), active: _followed is not null))
                    _followed = _followed is null ? Player.Tracks.FirstOrDefault(t => t.Recording is not null) : null;
            });
        }
        ImGui.End();
    }
}
