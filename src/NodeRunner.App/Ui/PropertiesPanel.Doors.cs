using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private (MapEntity Entity, int Structure, int Revision, MapLibrary Library, List<MapEntity> Doors)? _doors;

    // Selects the doors of the selected building or interior, ready for Suppr.
    private void DrawDoor(MapEntity entity, Archetype archetype, HashNames names)
    {
        if (!Widgets.BeginProperties("##door"))
            return;
        var shape = DoorShape.Of(archetype, names.TryGet(entity.ArchetypeHash));
        Widgets.PropertyText(L("Ouverture"), shape.Kind switch
        {
            DoorKind.Hinged => L("battante (gonds à l'origine du modèle)"),
            DoorKind.Sliding => L("coulissante"),
            DoorKind.Garage => L("porte de garage (monte)"),
            _ => L("fixe (ne s'ouvre pas)"),
        }, L("Déduit de la boîte du modèle : une porte battante a son origine sur les gonds, une porte coulissante ou de garage en son milieu. ")
           + L("L'arc d'ouverture est dessiné dans la vue 3D."), shape.Kind == DoorKind.Fixed ? UiTheme.Warning : null);
        Widgets.PropertyText(L("Taille"), L($"{archetype.BoundsMax.X - archetype.BoundsMin.X:0.00} x {shape.Height:0.00} m"),
            L("Largeur x hauteur du modèle, en mètres."));
        bool dynamic = (archetype.Flags & DoorCheck.DynamicFlag) != 0;
        Widgets.PropertyText(L("Dynamic"), dynamic ? L("oui") : L("non : la porte reste figée"),
            L($"Drapeau 0x{DoorCheck.DynamicFlag:X} du type (archétype) : sans lui, le jeu ne fait pas bouger la porte."),
            dynamic ? null : UiTheme.Warning);
        Widgets.PropertyText(L("Drapeau de porte"), (archetype.Flags & DoorCheck.DoorFlag) != 0 ? L("oui") : L("non"),
            L($"Drapeau 0x{DoorCheck.DoorFlag:X} du type : présent sur la moitié des portes du jeu, sur presque aucun autre objet."));
        DrawDoorTuning(entity, shape, names);
        if (shape.Kind == DoorKind.Hinged)
        {
            var clear = state.OpenDoors.ClearanceOf(entity, state);
            var blocked = new List<string>();
            if (clear is { MinBlocked: true } a)
                blocked.Add(L($"{MathF.Abs(a.ReachedMin):0}° au lieu de {MathF.Abs(a.WantedMin):0}°"));
            if (clear is { MaxBlocked: true } b)
                blocked.Add(L($"{MathF.Abs(b.ReachedMax):0}° au lieu de {MathF.Abs(b.WantedMax):0}°"));
            Widgets.PropertyText(L("Passage"),
                clear is null ? L("non vérifié (collisions pas encore lues)")
                : blocked.Count == 0 ? L("libre")
                : clear.Value.NoPassage ? L($"trop peu ouverte pour passer : {string.Join(L(" / "), blocked)}")
                : L($"libre (s'arrête sur un mur : {string.Join(L(" / "), blocked)})"),
                L("La porte est tournée pas à pas contre les collisions autour d'elle (murs, meubles des fichiers de carte et des intérieurs). ")
                + L("En jeu, elle s'arrêtera sur l'obstacle : partie rouge de l'arc dans la vue 3D. Souvent un pivot mal placé ou un mur trop près.\n")
                + L($"Une porte qui s'ouvre à {DoorClearance.Passage:0}° ou plus d'un côté laisse passer : s'arrêter contre le mur voisin est normal.\n")
                + L("Les meubles posés à part comptent par leur boîte (ceux que le jeu peut bouger) ; les petits objets ne comptent pas."),
                clear is { NoPassage: true } ? UiTheme.Warning : null);
        }
        var p = entity.Position;
        Widgets.PropertyText(L("Position (doorlock)"), L($"{p.X:0.000}, {p.Y:0.000}, {p.Z:0.000}"),
            L("Position exacte utilisée par les scripts de serrure (objCoords). Menu ... > Copier pour doorlock."));
        DoorChoice(entity, archetype, names);
        Widgets.PropertyText(L("Hash du modèle"), $"{unchecked((int)archetype.NameHash)} / 0x{archetype.NameHash:X8}",
            L("objHash dans qb-doorlock (décimal), model dans ox_doorlock."));
        Widgets.EndProperties();
        if (shape.Kind != DoorKind.Fixed
            && Widgets.Button(state.OpenDoors.IsOpen(entity) ? L($"{Icons.DoorOpen}  Fermer la porte") : L($"{Icons.DoorOpen}  Ouvrir la porte"),
                L("Ouvre la porte dans la vue 3D comme en jeu, pour voir où elle passe (rien n'est modifié ni enregistré).\n")
                + L("Raccourci : double-clic sur une porte dans la vue 3D."), ImGui.GetContentRegionAvail().X))
            state.ToggleDoors([entity]);
    }

    // What doortuning.ymt says of this door model: how far it opens, what opens it by itself, and a copy for a project.
    private void DrawDoorTuning(MapEntity entity, DoorShape shape, HashNames names)
    {
        if (shape.Kind == DoorKind.Fixed)
            return;
        if (state.World.DoorTunings is not { } tunings)
        {
            Widgets.PropertyText(L("Réglage (doortuning)"), L("lecture..."), null);
            return;
        }
        var tuning = tunings.OfModel(entity.ArchetypeHash);
        Widgets.PropertyText(L("Réglage (doortuning)"), tuning?.Name ?? L("par défaut du jeu"),
            tuning is null
                ? L("Ce modèle n'est pas dans doortuning.ymt : le jeu lui donne le réglage par défaut de son type de porte ")
                  + L("(s'ouvre dans les deux sens, ne se verrouille pas, aucune ouverture automatique).")
                : L($"Réglage donné à ce modèle par doortuning.ymt ({tunings.ModelCount} modèles de porte y ont un réglage)."));
        if (shape.Kind == DoorKind.Hinged)
        {
            var (min, max) = DoorShape.Swing(tuning);
            string side = tuning?.StdDoorRotDir is DoorRotation.Negative or DoorRotation.Positive ? L("un seul sens") : L("les deux sens");
            string limit = tuning is { RotationLimitAngle: > 0 } t ? L($"{t.RotationLimitAngle:0}°") : L("limite par défaut");
            Widgets.PropertyText(L("Ouverture max"), L($"{limit}, {side}"),
                L($"RotationLimitAngle et StdDoorRotDir. L'arc vert de la vue 3D va de {min:0}° à {max:0}° ")
                + L("(90° de chaque côté quand le réglage ne donne pas de limite)."));
        }
        if (tuning is not null)
        {
            var openers = new List<string>();
            var f = tuning.Flags;
            if (f.HasFlag(DoorTuningFlags.AutoOpensForAllVehicles))
                openers.Add(L("tous les véhicules"));
            if (f.HasFlag(DoorTuningFlags.AutoOpensForSPVehicleWithPedsOnly) || f.HasFlag(DoorTuningFlags.AutoOpensForMPVehicleWithPedsOnly))
                openers.Add(L("véhicules avec conducteur"));
            if (f.HasFlag(DoorTuningFlags.AutoOpensForSPPlayerPedsOnly) || f.HasFlag(DoorTuningFlags.AutoOpensForMPPlayerPedsOnly))
                openers.Add(L("joueurs"));
            if (f.HasFlag(DoorTuningFlags.AutoOpensForLawEnforcement))
                openers.Add(L("police"));
            Widgets.PropertyText(L("S'ouvre seule pour"), openers.Count == 0 ? L("personne") : string.Join(", ", openers),
                L("Drapeaux AutoOpensFor... du réglage (solo et Online confondus). ")
                + (tuning.HasTriggerBox ? L("La zone qui déclenche l'ouverture est dessinée en jaune dans la vue 3D.") : ""));
            Widgets.PropertyText(L("Verrouillée fermée"), tuning.ShouldLatchShut ? L("oui (se bloque en se refermant)") : L("non"),
                L("ShouldLatchShut : la porte se bloque une fois refermée, comme une porte à loquet."));
            Widgets.PropertyText(L("Poids"), L($"x{tuning.MassMultiplier:0.##}") + (tuning.BreakableByVehicle ? L(", cassable par un véhicule") : ""),
                L("MassMultiplier (plus lourd = plus dur à pousser) et BreakableByVehicle."));
        }
        DoorTuningCopy(entity, tunings, tuning, names);
    }

    // The XML to paste in a doortuning.meta: the chosen tuning given to this door model.
    private void DoorTuningCopy(MapEntity entity, DoorTunings tunings, DoorTuning? current, HashNames names)
    {
        Widgets.Property(L("Copier pour doortuning"), L("Copie le réglage choisi et la ligne qui le donne à ce modèle (ModelToTuneMapping), ")
            + L("à coller dans un fichier doortuning. Utile pour une porte d'un mapping, que le jeu ne connaît pas."));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##door_tuning_copy", L("Choisir un réglage à copier..."), ImGuiComboFlags.HeightLarge))
            return;
        string model = names.TryGet(entity.ArchetypeHash) ?? $"0x{entity.ArchetypeHash:X8}";
        foreach (var tuning in tunings.All)
        {
            if (ImGui.Selectable(tuning.Name, tuning == current))
            {
                ImGui.SetClipboardText(DoorTuningReader.Write(tuning, model));
                state.Notify(L($"Réglage {tuning.Name} copié pour {model}."));
            }
        }
        ImGui.EndCombo();
    }

    private string _doorQuery = "";
    private (MapLibrary Library, (uint Hash, string Name, float Width, float Height, DoorKind Kind)[] Types)? _doorTypes;

    // Another door model for the selected doors, the closest widths first.
    private void DoorChoice(MapEntity entity, Archetype archetype, HashNames names)
    {
        if (state.World.Maps is not { } library)
            return;
        Widgets.Property(L("Remplacer par"), L("Change le modèle des portes sélectionnées et recale la nouvelle dans l'encadrement : ")
            + L("gonds sur le même montant, ou centrée pour une porte coulissante / de garage, bas au même niveau. Ctrl+Z pour annuler. ")
            + L("Pas encore pour les portes des intérieurs (MLO)."));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##door_replace", L("Choisir une autre porte..."), ImGuiComboFlags.HeightLarge))
            return;
        if (ImGui.IsWindowAppearing())
        {
            _doorQuery = "";
            ImGui.SetKeyboardFocusHere();
        }
        if (_doorTypes is not { } cache || cache.Library != library)
        {
            var types = library.GetArchetypeHashes()
                .Select(h => (Hash: h, Name: names.TryGet(h), Type: library.GetArchetype(h)!))
                .Where(t => t.Type.Kind == ArchetypeKind.Base && Doors.IsDoor(t.Name))
                .Select(t => (t.Hash, t.Name!, t.Type.BoundsMax.X - t.Type.BoundsMin.X, t.Type.BoundsMax.Z - t.Type.BoundsMin.Z, DoorShape.Of(t.Type, t.Name).Kind))
                .ToArray();
            _doorTypes = cache = (library, types);
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##door_query", L("Filtrer (ex. v_ilev, garage)"), ref _doorQuery, 64);
        ImGui.Separator();
        float width = archetype.BoundsMax.X - archetype.BoundsMin.X;
        string query = _doorQuery.Trim();
        var matches = cache.Types.Where(t => t.Hash != entity.ArchetypeHash && (query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => MathF.Abs(t.Width - width)).ThenBy(t => t.Name, StringComparer.Ordinal).Take(MaxTypeChoices).ToList();
        if (matches.Count == 0)
            ImGui.TextDisabled(L("Aucune porte trouvée."));
        foreach (var t in matches)
        {
            string kind = t.Kind switch
            {
                DoorKind.Hinged => L("battante"),
                DoorKind.Sliding => L("coulissante"),
                DoorKind.Garage => L("garage"),
                _ => L("fixe"),
            };
            if (ImGui.Selectable(L($"{t.Name}  ·  {t.Width:0.00} x {t.Height:0.00} m, {kind}##door{t.Hash}")))
                state.ReplaceSelectedDoors(t.Hash);
        }
        ImGui.EndCombo();
    }

    private void DrawDoorButton(MapEntity entity, HashNames names)
    {
        if (state.World.Maps is not { } library)
            return;
        if (_doors is not { } cache || cache.Entity != entity || cache.Library != library
            || cache.Structure != library.StructureVersion || cache.Revision != entity.Revision)
        {
            cache = (entity, library.StructureVersion, entity.Revision, library, Doors.Of(entity, library, names.TryGet));
            _doors = cache;
        }
        var doors = cache.Doors;
        if (doors.Count == 0 || doors.Count == 1 && doors[0] == entity)
            return;
        // On a door, its own button opens it (with its other leaf), not every door of the interior.
        if (!doors.Contains(entity) && Widgets.Button(doors.Any(state.OpenDoors.IsOpen) ? L($"{Icons.DoorOpen}  Fermer les portes ({doors.Count})") : L($"{Icons.DoorOpen}  Ouvrir les portes ({doors.Count})"),
                L("Ouvre ces portes dans la vue 3D comme en jeu (rien n'est modifié ni enregistré)."), ImGui.GetContentRegionAvail().X))
            state.ToggleDoors(doors);
        if (doors.All(state.IsSelected) && state.SelectedEntities.Count == doors.Count)
            return;
        bool interior = entity.IsInterior || entity.Interior is not null;
        if (Widgets.Button(L($"{Icons.DoorOpen}  Sélectionner les portes ({doors.Count})"),
                (interior ? L("Sélectionner toutes les portes de cet intérieur.") : L("Sélectionner les portes posées sur ce bâtiment (dans sa boîte, à 1 m près).")) + "\n"
                + L("Ensuite : Suppr pour les supprimer, ou menu ... > Copier le script qui les cache.\n")
                + L("Une porte dessinée dans le modèle du bâtiment n'est pas un objet à part : elle ne peut pas être retirée ici."),
                ImGui.GetContentRegionAvail().X))
        {
            state.SelectAll(doors);
            state.Notify(L($"{doors.Count} portes sélectionnées : Suppr pour les supprimer."));
        }
    }
}
