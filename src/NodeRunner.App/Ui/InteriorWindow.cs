using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "New interior" window: the name of the interior (its archetype and its .ytyp file); created in front of the camera,
// empty or as a copy of another interior.
internal sealed class InteriorWindow(AppState state)
{
    public const string Title = $"{Icons.House}  Nouvel intérieur###NouvelInterieur";

    private bool _open;
    private bool _focusName;
    private string _name = "";
    private MapEntity? _source;

    public void Open()
    {
        _open = true;
        _focusName = true;
        _source = null;
        _name = state.SuggestInteriorName();
    }

    // A copy of interior (an instance of it) under a new name.
    public void OpenDuplicate(MapEntity interior)
    {
        _open = true;
        _focusName = true;
        _source = interior;
        _name = state.SuggestDuplicateName(interior.ArchetypeHash);
    }

    public void Draw()
    {
        if (!_open)
            return;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(480f, 0f), new Vector2(480f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (_source is { } source)
            Widgets.Hint(L($"Une copie de l'intérieur {state.World.Names?.Describe(source.ArchetypeHash) ?? ""} sous un nouveau nom, posée devant la caméra : ")
                + L("mêmes objets, entity sets, salles et portails, enregistrés dans un .ytyp du projet portant son nom. ")
                + L("La modifier ne change pas l'original."), wrap: true);
        else
            Widgets.Hint(L("Un intérieur (MLO) vide, posé devant la caméra : la salle « limbo » (l'extérieur), une salle de 6 x 6 m ")
                + L("et une porte vers l'extérieur. Il est enregistré dans un .ytyp du projet portant son nom."), wrap: true);
        ImGui.Dummy(new Vector2(0, 4));

        string name = _name.Trim();
        bool valid = InteriorCreation.IsValidName(name);
        bool free = valid && state.IsFreeInteriorName(name);
        bool enter = false;
        if (Widgets.BeginProperties("##new_interior", 0.28f))
        {
            Widgets.Property(L("Nom"), L("Nom de l'intérieur (archétype) et de son fichier .ytyp"));
            if (_focusName)
            {
                ImGui.SetKeyboardFocusHere();
                _focusName = false;
            }
            ImGui.SetNextItemWidth(-1);
            enter = ImGui.InputTextWithHint("##interior_name", L("ex. hopital_int"), ref _name, 48, ImGuiInputTextFlags.EnterReturnsTrue);

            var (text, color) = !valid ? (L("lettres minuscules, chiffres et _ seulement, en commençant par une lettre"), UiTheme.Error)
                : !free ? (L("ce nom est déjà utilisé par un type d'objet ou un .ytyp"), UiTheme.Error)
                : (L($"stream/{name}.ytyp"), UiTheme.TextMuted);
            Widgets.Property(L("Fichier"));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
            Widgets.EndProperties();
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!free);
        if ((Widgets.PrimaryButton(_source is null ? L($"{Icons.Check}  Créer") : L($"{Icons.Check}  Dupliquer"), 120f) || enter) && free)
        {
            if (_source is { } copied)
                state.DuplicateInterior(copied, name);
            else
                state.CreateInterior(name);
            _open = false;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Widgets.Button(L("Annuler"), width: 90f))
            _open = false;
    }
}
