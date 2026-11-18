using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private Archetype? _healthOf;
    private List<MloCheck.Issue>? _healthIssues;

    // What breaks an interior in game, on request (nothing is changed).
    private void DrawInteriorHealth(MapEntity interior, Archetype archetype, MapLibrary library, HashNames names)
    {
        Widgets.Hint(L("Cherche ce qui casse un intérieur en jeu : portails mal reliés ou mal formés, objets rattachés à aucune salle, ")
            + L("modèles introuvables... Rien n'est modifié."), wrap: true);
        if (_healthOf != archetype)
            _healthIssues = null;
        if (Widgets.Button(L($"{Icons.ScanEye}  Vérifier cet intérieur")))
        {
            _healthOf = archetype;
            _healthIssues = MloCheck.Check(archetype, hash => library.GetArchetype(hash) is not null);
        }
        if (_healthIssues is not { } issues)
            return;

        int errors = issues.Count(i => MloCheck.IsError(i.Kind));
        if (issues.Count == 0)
        {
            ImGui.TextColored(UiTheme.Success, L($"{Icons.Check}  Aucun problème trouvé"));
            return;
        }
        ImGui.TextColored(errors > 0 ? UiTheme.Error : UiTheme.Warning,
            L($"{errors} erreur(s), {issues.Count - errors} avertissement(s)"));
        for (int i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            bool error = MloCheck.IsError(issue.Kind);
            ImGui.TextColored(error ? UiTheme.Error : UiTheme.Warning, error ? Icons.X : Icons.TriangleAlert);
            ImGui.SameLine();
            string text = HealthText(issue, archetype, names);
            if (HealthTarget(issue, interior) is { } go)
            {
                if (Widgets.Link($"{text}##health{i}", HealthTooltip(issue.Kind)))
                    go();
            }
            else
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(HealthTooltip(issue.Kind));
            }
        }
    }

    private static string HealthText(MloCheck.Issue issue, Archetype archetype, HashNames names)
    {
        string entity = issue.Entity is { } e ? names.Describe(e.ArchetypeHash) : "";
        string set = issue.Index >= 0 && issue.Index < archetype.InteriorEntitySets.Count
            ? names.Describe(archetype.InteriorEntitySets[issue.Index].NameHash) : "";
        return issue.Kind switch
        {
            MloCheck.Kind.PortalUnknownRoom => L($"Portail {issue.Index} : relié à une salle qui n'existe pas"),
            MloCheck.Kind.PortalCorners => L($"Portail {issue.Index} : {(int)issue.Value} coin(s) au lieu de 4"),
            MloCheck.Kind.PortalFlat => L($"Portail {issue.Index} : surface nulle (coins alignés ou confondus)"),
            MloCheck.Kind.PortalTwisted => L($"Portail {issue.Index} : coins pas à plat ({issue.Value:0.00} m d'écart)"),
            MloCheck.Kind.EntityUnknownRoom => L($"{entity} : rattaché à une salle ou un portail qui n'existe pas"),
            MloCheck.Kind.EntityNoRoom => L($"{entity} : rattaché à aucune salle, le jeu ne l'affiche jamais"),
            MloCheck.Kind.MissingModel => L($"{entity} : modèle introuvable (ni dans le jeu ni dans le projet)"),
            MloCheck.Kind.RoomWithoutPortal => L($"Salle {RoomLabel(archetype, issue.Index)} : aucun portail"),
            MloCheck.Kind.NoExteriorPortal => L("Aucun portail vers l'extérieur (salle 0)"),
            MloCheck.Kind.RoomBadBox => L($"Salle {RoomLabel(archetype, issue.Index)} : boîte vide ou inversée"),
            MloCheck.Kind.RoomHuge => L($"Salle {RoomLabel(archetype, issue.Index)} : boîte démesurée ({issue.Value:0} m)"),
            MloCheck.Kind.PortalDuplicate => L($"Portail {issue.Index} : identique au portail {(int)issue.Value}"),
            MloCheck.Kind.EntitySetEmpty => L($"Entity set {set} : vide"),
            MloCheck.Kind.EntitySetDuplicate => L($"Entity set {set} : nom déjà utilisé par un autre"),
            MloCheck.Kind.TooManyRooms => L($"{(int)issue.Value} salles : les collisions n'en repèrent que {MloCheck.MaxCollisionRooms}"),
            _ => issue.Kind.ToString(),
        };
    }

    private static string HealthTooltip(MloCheck.Kind kind) => kind switch
    {
        MloCheck.Kind.PortalUnknownRoom => L("Le jeu ne sait pas quelles salles ce portail relie : il les cache ou plante l'affichage de l'intérieur."),
        MloCheck.Kind.PortalCorners or MloCheck.Kind.PortalFlat or MloCheck.Kind.PortalTwisted =>
            L("Un portail est un rectangle plat de 4 coins : sinon le jeu voit mal à travers et des salles disparaissent selon l'angle de vue."),
        MloCheck.Kind.EntityUnknownRoom or MloCheck.Kind.EntityNoRoom =>
            L("Le jeu n'affiche que les objets rangés dans une salle (ou un portail) existante. Changer sa salle dans ses propriétés."),
        MloCheck.Kind.MissingModel => L("L'objet sera invisible en jeu. Arrive aussi dans quelques intérieurs du jeu (objets retirés par Rockstar)."),
        MloCheck.Kind.RoomWithoutPortal => L("La salle ne se voit depuis aucune autre : elle n'apparaît que quand le joueur est dedans. Parfois voulu."),
        MloCheck.Kind.NoExteriorPortal => L("L'intérieur ne se voit pas du dehors : normal s'il est atteint seulement par téléportation."),
        MloCheck.Kind.RoomBadBox or MloCheck.Kind.RoomHuge => L("La boîte de la salle sert au jeu à savoir dans quelle salle est le joueur."),
        MloCheck.Kind.PortalDuplicate => L("Deux portails au même endroit entre les mêmes salles : l'un des deux est en trop."),
        MloCheck.Kind.EntitySetEmpty or MloCheck.Kind.EntitySetDuplicate => L("Un script qui active cet entity set n'affichera rien ou pas ce qui est attendu."),
        MloCheck.Kind.TooManyRooms => L("Le numéro de salle des collisions ne va que jusqu'à 31 : au-delà, le jeu se trompe de salle (lumière, son)."),
        _ => "",
    };

    // Selecting what the line is about: the room, the portal or the object (its copy in this interior).
    private Action? HealthTarget(MloCheck.Issue issue, MapEntity interior)
    {
        var place = new RoomSelection(interior, issue.Index);
        switch (issue.Kind)
        {
            case MloCheck.Kind.RoomWithoutPortal or MloCheck.Kind.RoomBadBox or MloCheck.Kind.RoomHuge:
                return () =>
                {
                    state.SelectRoom(place);
                    state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
                };
            case MloCheck.Kind.PortalCorners or MloCheck.Kind.PortalFlat or MloCheck.Kind.PortalTwisted or MloCheck.Kind.PortalDuplicate
                or MloCheck.Kind.PortalUnknownRoom:
                return () =>
                {
                    state.SelectPortal(place);
                    state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.InnerPortals), true);
                    state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals), true);
                };
        }
        if (issue.Entity is not { } definition)
            return null;
        var placed = interior.InteriorEntities.Concat(interior.EntitySets.SelectMany(s => s.Entities))
            .FirstOrDefault(e => e.Definition == definition && !e.IsDeleted);
        if (placed is null)
            return null;
        return () =>
        {
            state.Select(placed);
            state.FocusOn(placed);
        };
    }
}
