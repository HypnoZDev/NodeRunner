using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// Shown after importing a mapping whose models are locked by FiveM's Asset Escrow: what it means, what NodeRunner
// shows instead.
internal sealed class ProtectedMappingWindow
{
    public const string Title = $"{Icons.TriangleAlert}  Mapping protégé###ProtectedMapping";
    private const int MaxListed = 8;
    private const float Width = 540f;

    private ImportResult? _result;
    private bool _focus;
    private bool _showFiles;

    public void Open(ImportResult result)
    {
        _result = result;
        _focus = true;
        _showFiles = false;
    }

    public void Draw()
    {
        if (_result is not { } result)
            return;
        if (_focus)
        {
            ImGui.SetNextWindowFocus();
            _focus = false;
        }
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(Width, 0f), new Vector2(Width, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 16));
        bool open = true;
        bool visible = ImGui.Begin(L(Title), ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent(result, ref open);
        ImGui.End();
        if (!open)
            _result = null;
    }

    private void DrawContent(ImportResult result, ref bool open)
    {
        float width = ImGui.GetContentRegionAvail().X;
        DrawBanner(width);
        ImGui.Dummy(new Vector2(0, 6));

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextUnformatted(L($"« {result.Project.Name} » a été chiffré par son créateur avec la protection Asset Escrow de FiveM (Cfx.re). ")
            + L($"{result.Protected.Count} fichier(s) de modèles 3D sont verrouillés : seul un serveur FiveM qui possède la licence de ce mapping peut les lire."));
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.TextColored(UiTheme.TextMuted, L($"{Program.Name} ne contourne pas cette protection. À la place :"));
        Bullet(L("les objets protégés sont affichés en boîtes orange translucides, à la taille donnée par leur .ytyp ;"));
        Bullet(L("tu peux quand même les sélectionner, les déplacer, les tourner et les supprimer ;"));
        Bullet(L("à l'export, les fichiers chiffrés sont recopiés tels quels : en jeu, ils s'affichent sur ton serveur s'il a la licence."));
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 6));

        if (Widgets.BeginProperties("##protected", 0.42f))
        {
            Widgets.PropertyText(L("Fichiers copiés"), $"{result.Copied}");
            Widgets.PropertyText(L("Fichiers lisibles"), $"{result.Copied - result.Protected.Count}",
                L("YMAP, YTYP, textures, collisions... : lus et affichés normalement."), UiTheme.Success);
            Widgets.PropertyText(L("Fichiers protégés"), $"{result.Protected.Count}", null, UiTheme.Warning);
            Widgets.EndProperties();
        }
        if (Widgets.Link(_showFiles ? L("Masquer la liste") : L("Voir les fichiers protégés"), null))
            _showFiles = !_showFiles;
        if (_showFiles)
        {
            ImGui.PushFont(UiTheme.SmallFont);
            foreach (var file in result.Protected.Take(MaxListed))
                ImGui.TextColored(UiTheme.TextMuted, $"{Icons.TriangleAlert}  {file}");
            if (result.Protected.Count > MaxListed)
                Widgets.Hint(L($"... et {result.Protected.Count - MaxListed} autres (liste complète dans État > Problèmes)"));
            ImGui.PopFont();
        }
        ImGui.Dummy(new Vector2(0, 4));
        Widgets.Hint(L("Les fichiers d'origine ne sont jamais modifiés. Pour voir les vrais modèles, demande au créateur une version non protégée."), wrap: true);
        ImGui.Dummy(new Vector2(0, 8));

        if (Widgets.Button(L("J'ai compris"), L("Entrée"), width)
            || ImGui.IsWindowFocused() && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false) || ImGui.IsKeyPressed(ImGuiKey.Escape, false)))
            open = false;
    }

    // Warning strip: orange edge, tinted background, title.
    private static void DrawBanner(float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float height = ImGui.GetTextLineHeight() * 2.6f;
        var warning = UiTheme.Warning;
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(warning with { W = 0.12f }), 4f);
        draw.AddRectFilled(start, start + new Vector2(4f, height), ImGui.GetColorU32(warning), 4f, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(start + new Vector2(14f, (height - ImGui.GetTextLineHeight() * 1.9f) / 2f));
        ImGui.BeginGroup();
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextColored(warning, L($"{Icons.TriangleAlert}  Attention : ce mapping est protégé"));
        ImGui.PopFont();
        ImGui.PushFont(UiTheme.SmallFont);
        ImGui.TextColored(UiTheme.TextMuted, L("Fichiers chiffrés par FiveM (Asset Escrow)"));
        ImGui.PopFont();
        ImGui.EndGroup();
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private static void Bullet(string text)
    {
        ImGui.TextColored(UiTheme.Warning, "•");
        ImGui.SameLine();
        ImGui.TextUnformatted(text);
    }
}
