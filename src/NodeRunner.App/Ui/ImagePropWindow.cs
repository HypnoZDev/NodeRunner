using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "Prop from an image" window: a round plaque with the image on it (a badge, a logo), added to the prop library.
internal sealed class ImagePropWindow(AppState state)
{
    public const string Title = $"{Icons.Star}  Prop depuis une image###PropImage";

    private const string ImageFilter = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";

    private bool _open;
    private string? _image;
    private string _name = "";
    private float _diameter = 1f;
    private float _thicknessCm = 3f;
    private bool _backImage = true;

    public void Open()
    {
        _open = true;
        if (_image is null)
            ChooseImage();
    }

    private void ChooseImage()
    {
        state.DialogRequest = owner =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = L("Image du prop"),
                Filter = L($"Images|{ImageFilter}|Tous les fichiers (*.*)|*.*"),
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            _image = dialog.FileName;
            _name = ImageProp.SuggestName(dialog.FileName);
        };
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
        Widgets.Hint(L("Un médaillon rond avec l'image sur sa face (un insigne, un logo), son bord de la couleur du tour de l'image, ")
            + L("et sa collision. Il est ajouté à la bibliothèque de props : à placer ensuite comme les autres, puis à exporter pour FiveM. ")
            + L("Une image carrée donne le meilleur résultat (sinon son centre est gardé)."), wrap: true);
        ImGui.Dummy(new Vector2(0, 4));

        string name = _name.Trim();
        bool valid = InteriorCreation.IsValidName(name);
        bool free = valid && state.IsFreePropName(name);
        if (Widgets.BeginProperties("##image_prop", 0.28f))
        {
            Widgets.Property(L("Image"), L("Image .png, .jpg... (la transparence prend la couleur du tour de l'image)"));
            float browse = 90f;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(_image is null ? UiTheme.TextMuted : UiTheme.Text,
                Widgets.Fit(_image is null ? L("aucune image") : Path.GetFileName(_image), ImGui.GetContentRegionAvail().X - browse - 8f));
            ImGui.SameLine();
            if (Widgets.Button(L("Choisir...##image_prop"), width: browse))
                ChooseImage();

            Widgets.Property(L("Nom"), L("Nom du prop (archétype) et de ses fichiers .ydr et .ytyp"));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##image_prop_name", L("ex. badge_secret_service"), ref _name, 48);

            Widgets.Property(L("Diamètre"), L("Largeur du médaillon dans le jeu"));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat("##image_prop_diameter", ref _diameter, 0.01f, ImageProp.MinDiameter, ImageProp.MaxDiameter, "%.2f m"))
                _diameter = Math.Clamp(_diameter, ImageProp.MinDiameter, ImageProp.MaxDiameter);

            Widgets.Property(L("Épaisseur"));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat("##image_prop_thickness", ref _thicknessCm, 0.1f, ImageProp.MinThickness * 100f, ImageProp.MaxThickness * 100f, "%.1f cm"))
                _thicknessCm = Math.Clamp(_thicknessCm, ImageProp.MinThickness * 100f, ImageProp.MaxThickness * 100f);

            Widgets.Property(L("Dos"));
            ImGui.Checkbox(L("Image aussi au dos##image_prop_back"), ref _backImage);

            var (text, color) = !valid ? (L("lettres minuscules, chiffres et _ seulement, en commençant par une lettre"), UiTheme.Error)
                : !free ? (L("ce nom est déjà celui d'un objet du jeu ou d'un projet"), UiTheme.Error)
                : (L($"bibliothèque : {name}/{name}.ydr, {name}.ytyp"), UiTheme.TextMuted);
            Widgets.Property(L("Fichiers"));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
            Widgets.EndProperties();
        }

        ImGui.Dummy(new Vector2(0, 4));
        bool ready = free && _image is not null;
        ImGui.BeginDisabled(!ready);
        if (Widgets.PrimaryButton(L($"{Icons.Check}  Créer"), 120f) && ready)
        {
            state.ImagePropRequest = (_image!, new ImageProp.Options(name, _diameter, _thicknessCm / 100f, _backImage));
            _open = false;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Widgets.Button(L("Annuler"), width: 90f))
            _open = false;
    }
}
