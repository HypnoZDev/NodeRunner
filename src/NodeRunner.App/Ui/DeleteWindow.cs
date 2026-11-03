using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

/// <summary>
/// Confirmation of a deletion (Suppr): what goes with the object (its level-of-detail chain), what stays, the map
/// files changed. The objects that go are framed in red in the 3D view meanwhile (MainForm).
/// </summary>
internal sealed class DeleteWindow(AppState state)
{
    public const string Title = $"{Icons.Trash}  Supprimer###Delete";
    private const int MaxListed = 6;

    private DeletionPlan? _shown;

    public void Draw()
    {
        if (state.PendingDeletion is not { } plan)
        {
            _shown = null;
            return;
        }
        if (plan != _shown)
        {
            _shown = plan;
            ImGui.SetNextWindowFocus();
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(460f, 0f), new Vector2(460f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool open = true;
        bool visible = ImGui.Begin(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent(plan);
        ImGui.End();
        if (!open)
            state.CancelDelete();
    }

    private void DrawContent(DeletionPlan plan)
    {
        var names = state.World.Names ?? new HashNames();
        bool group = plan.Targets.Count > 1;
        string name = group ? $"les {plan.Targets.Count} objets sélectionnés" : names.Describe(plan.Target.ArchetypeHash);

        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(Widgets.Fit($"Supprimer {name} ?", ImGui.GetContentRegionAvail().X));
        ImGui.PopFont();
        Widgets.Hint(group ? "Une seule étape d'annulation pour tout le groupe." : $"{LevelName(plan.Target)} · {MapName(plan.Target)}");
        ImGui.Dummy(new Vector2(0, 4));

        if (Widgets.BeginProperties("##delete", 0.40f))
        {
            Widgets.PropertyText("Objets supprimés", $"{plan.Entities.Count}");
            if (group)
                ListRows("Sélection", "Les objets choisis.", plan.Targets, names);
            ListRows("Versions détaillées", "Remplacent les objets de près (enfants dans la chaîne LOD) : elles partent avec eux.", plan.Children, names);
            ListRows("Versions simplifiées", "Ne représentaient que ces objets de loin (parents LOD sans autre enfant) : elles partent aussi.", plan.Parents, names);
            ListRows("Reste", "Représente aussi d'autres objets : reste visible de loin.", plan.KeptParents, names);
            var maps = plan.Maps.Select(m => m.FilePath is { } p ? Path.GetFileName(p) : "?").Order().ToList();
            Widgets.PropertyText("YMAP modifiés", string.Join(", ", maps.Take(3)) + (maps.Count > 3 ? $" (+{maps.Count - 3})" : ""),
                string.Join("\n", maps));
            Widgets.EndProperties();
        }

        if (plan.KeptParents is [var kept, ..])
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(UiTheme.Warning, plan.KeptParents.Count == 1
                ? $"{Icons.TriangleAlert}  {names.Describe(kept.ArchetypeHash)} ({LevelName(kept)}) reste : "
                    + (group ? "il représente aussi d'autres objets, une partie de la sélection restera donc visible de loin."
                        : "il représente aussi d'autres objets, celui-ci restera donc visible de loin.")
                : $"{Icons.TriangleAlert}  {plan.KeptParents.Count} versions simplifiées restent : elles représentent aussi d'autres objets, "
                    + "une partie de la sélection restera donc visible de loin.");
            ImGui.PopTextWrapPos();
        }
        ImGui.Dummy(new Vector2(0, 2));
        Widgets.Hint("Collision : celle du décor (bâtiments, sol...) est dans des fichiers .ybn, pas encore modifiés : "
            + "en jeu, elle peut rester à la place de l'objet. Les props ont leur propre collision et partent avec lui.", wrap: true);
        Widgets.Hint("Annulable (Ctrl+Z). Les fichiers ne changent qu'à l'enregistrement (Ctrl+S).", wrap: true);
        ImGui.Dummy(new Vector2(0, 6));

        float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        string label = plan.Entities.Count == 1 ? $"{Icons.Trash}  Supprimer" : $"{Icons.Trash}  Supprimer ({plan.Entities.Count} objets)";
        if (Widgets.Button(label, "Entrée", width) || ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            state.ConfirmDelete();
        ImGui.SameLine();
        if (Widgets.Button("Annuler", "Échap", width) || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            state.CancelDelete();
    }

    /// <summary>Objects listed as links: a click places the camera in front of one (the window stays open).</summary>
    private void ListRows(string label, string tooltip, IReadOnlyList<MapEntity> entities, HashNames names)
    {
        if (entities.Count == 0)
            return;
        Widgets.Property(label, tooltip);
        ImGui.BeginGroup();
        for (int i = 0; i < entities.Count && i < MaxListed; i++)
        {
            var entity = entities[i];
            if (Widgets.Link($"{names.Describe(entity.ArchetypeHash)} ({LevelName(entity)})##{label}{i}", "Aller voir cet objet"))
                state.FocusOn(entity);
        }
        if (entities.Count > MaxListed)
            Widgets.Hint($"... et {entities.Count - MaxListed} autres");
        ImGui.EndGroup();
    }

    private static string MapName(MapEntity entity) => entity.Map?.FilePath is { } path ? Path.GetFileName(path) : "?";

    private static string LevelName(MapEntity entity) => entity.LodLevel switch
    {
        LodLevel.Hd => "HD",
        LodLevel.OrphanHd => "HD sans LOD",
        LodLevel.Lod => "LOD",
        var level => level.ToString().ToUpperInvariant(),
    };
}
