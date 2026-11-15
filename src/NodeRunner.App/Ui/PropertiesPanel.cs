using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// Properties panel (docked on the right) of the selected object: name and quick actions, then a label / value grid in
// sections: placement (editable, each change can be undone), files, level of detail (parent and children can be
// selected in turn), entity sets of an interior (shown or hidden live), render settings.
internal sealed partial class PropertiesPanel(AppState state, Action<uint> showModel, CollisionLayer collisionLayer)
{
    private readonly CollisionProperties _collision = new(state, collisionLayer);

    public const string Title = $"{Icons.SlidersHorizontal}  Propriétés###Propriétés";

    private const int MaxChildrenListed = 40;
    private const float MinScale = 0.01f;

    // Rotation angles shown for the selection: kept while the user edits them (converting back from the
    // rotation would make them jump, e.g. 180° around X and Z instead of 180° around Y).
    private MapEntity? _anglesEntity;
    private int _anglesRevision = -1;
    private Vector3 _angles;

    private Placement _editStart;

    private string _setName = "";
    private (bool Ok, string Text)? _setNameResult;
    private MapEntity? _setNameInterior;

    // Entity flags whose meaning is known (see MapEntity.IsDrawnInMainView).
    private static (uint Flag, string Text)[] KnownFlags =>
    [
        (0x8, L("LOD dans la carte parente")),
        (0x80000, L("projette des ombres fixes")),
        (0x100000, L("projette des ombres mobiles")),
        (0x800000, L("ombres seulement")),
        (0x1000000, L("absent des reflets")),
        (0x2000000, L("reflets seulement")),
        (0x8000000, L("reflets de l'eau seulement")),
        (0x20000000, L("miroirs seulement")),
    ];

    public void Draw(ref bool visible)
    {
        if (!ImGui.Begin(L(Title), ref visible))
        {
            ImGui.End();
            return;
        }
        if (state.Tool == TransformTool.Collision)
        {
            _collision.Draw();
            ImGui.End();
            return;
        }
        if (state.IsGta4)
        {
            DrawGta4();
            ImGui.End();
            return;
        }

        DrawModeSwitch();
        if (state.Selection is { } entity && state.World.Maps is { } library)
        {
            var names = state.World.Names ?? new HashNames();
            if (state.SelectedEntities.Count is var count and > 1)
            {
                Widgets.Hint(L($"{count} objets sélectionnés : propriétés du dernier cliqué. Les poignées 3D et Suppr agissent sur tout le groupe. ")
                    + L("Ctrl+clic pour en ajouter ou en retirer."), wrap: true);
                ImGui.Dummy(new Vector2(0, 2));
            }
            DrawHeader(entity, names);
            if (Widgets.Section(L("Transformation"), "transform"))
                DrawPlacement(entity, library, names);
            if (!Simple && Widgets.Section(L("Fichiers"), "files"))
                DrawFiles(entity, library, names);
            if (!Simple && Doors.IsDoor(names.TryGet(entity.ArchetypeHash)) && library.GetArchetype(entity.ArchetypeHash) is { } doorType
                && Widgets.Section(L("Porte"), "door"))
                DrawDoor(entity, doorType, names);
            if (!Simple && Widgets.Section(L("Niveau de détail"), "lod"))
                DrawLevelOfDetail(entity, names);
            if ((entity.IsInterior ? entity : entity.Interior) is { IsInterior: true } interior
                && Widgets.Section(L($"Entity sets ({interior.EntitySets.Count})"), "entity_sets"))
                DrawEntitySets(interior, library, names);
            if (!Simple && (entity.IsInterior ? entity : entity.Interior) is { IsInterior: true } placed
                && library.GetArchetype(placed.ArchetypeHash) is { } interiorType)
            {
                if (Widgets.Section(L($"Salles ({Math.Max(0, interiorType.InteriorRooms.Count - 1)})"), "rooms", defaultOpen: false))
                    DrawRoomList(placed, interiorType);
                if (Widgets.Section(L("Occlusion audio"), "audio_occlusion", defaultOpen: false))
                    DrawAudioOcclusion(placed, interiorType, library, names);
                if (Widgets.Section(L("Réglages audio"), "interior_audio", defaultOpen: false))
                    DrawInteriorAudio(placed, interiorType, names);
            }
            if (!Simple && Widgets.Section(L("Réglages"), "flags", defaultOpen: false))
                DrawFlags(entity, library);
            if (Simple)
                Widgets.Hint(L("Mode Simple : fichiers, LOD, réglages et salles sont dans le mode Avancé."), wrap: true);
        }
        else if (state.SelectedPathNode is { } node && state.World.PathNetwork is { } network)
            DrawPathNode(node, network);
        else if (state.SelectedScenario is { } scenario)
            DrawScenario(scenario, state.World.Names ?? new HashNames());
        else if (state.SelectedOccluder is { } occluder)
            DrawOccluder(occluder);
        else if (state.SelectedCarGenerator is { Spot: { } spot } carGenerator)
            DrawCarGenerator(carGenerator, spot, state.World.Names ?? new HashNames());
        else if (state.SelectedTimecycle is { Zone: { } zone } timecycle)
            DrawTimecycle(timecycle, zone, state.World.Names ?? new HashNames());
        else if (state.SelectedLodLight is { Light: { } light } lodLight)
            DrawLodLight(lodLight, light);
        else if (state.SelectedRoom is { } room && state.World.Maps is { } roomLibrary
                 && roomLibrary.GetArchetype(room.Interior.ArchetypeHash) is { } roomInterior && room.Index < roomInterior.InteriorRooms.Count)
            DrawRoom(room, roomInterior, roomLibrary);
        else if (state.SelectedPortal is { } portal && state.World.Maps is { } portalLibrary
                 && portalLibrary.GetArchetype(portal.Interior.ArchetypeHash) is { } portalInterior && portal.Index < portalInterior.InteriorPortals.Count)
            DrawPortal(portal, portalInterior, portalLibrary);
        else
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(UiTheme.TextMuted, L("Aucun objet sélectionné"));
            Widgets.Hint(L("Clic gauche sur un objet de la vue 3D, ou dans la liste Scène, pour voir et modifier ses propriétés."), wrap: true);
        }
        ImGui.End();
    }

    private bool Simple => state.Settings.SimpleProperties;

    private void DrawModeSwitch()
    {
        int mode = Simple ? 0 : 1;
        if (Widgets.Segmented("##properties_mode", [L("Simple"), L("Avancé")], ref mode,
                [L("Seulement l'essentiel : position, rotation, échelle et actions courantes."), L("Toutes les propriétés : fichiers, LOD, réglages, salles...")]))
            state.Settings.SimpleProperties = mode == 0;
        ImGui.Dummy(new Vector2(0, 2));
    }

    private void DrawHeader(MapEntity entity, HashNames names)
    {
        string name = names.Describe(entity.ArchetypeHash);
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(Widgets.Fit(name, ImGui.GetContentRegionAvail().X));
        ImGui.PopFont();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(name);
        if (entity.Map?.FilePath is { } mapPath)
        {
            Widgets.Hint($"{Icons.File}  {Path.GetFileName(mapPath)}");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(mapPath);
        }
        ImGui.Dummy(new Vector2(0, 4));

        // Actions: go to the object, copy its position for scripts (Lua, JSON or C#), more copies in the "..." menu.
        float more = ImGui.GetFrameHeight();
        float buttonWidth = (ImGui.GetContentRegionAvail().X - more - ImGui.GetStyle().ItemSpacing.X * 2f) / 2f;
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à l'objet"), L("Placer la caméra devant l'objet (double-clic dans la liste Scène)"), buttonWidth))
            state.FocusOn(entity);
        ImGui.SameLine();
        string position = state.FormatPosition(entity.Position);
        string withHeading = state.FormatPosition(entity.Position, FiveMFormat.Heading(entity));
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), L($"Copier {position}\nClic droit : avec le cap ({withHeading})\n")
            + L($"Format : {PositionText.FormatName(state.CopyFormat)} (à changer dans le menu ...)"), buttonWidth))
            ImGui.SetClipboardText(position);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(withHeading);
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Ellipsis, "more_copies", L("Autres copies, désélectionner")))
            ImGui.OpenPopup("more_copies_menu");
        int deletable = state.DeletableSelection.Count;
        bool deleteButton = Simple && deletable > 0;
        if (entity.CanEdit)
        {
            float groundWidth = deleteButton
                ? (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f
                : ImGui.GetContentRegionAvail().X;
            if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Poser au sol"), L("Monter ou descendre l'objet jusqu'à la collision en dessous (touche Fin)"),
                groundWidth))
                state.PutSelectionOnGround();
            if (deleteButton)
                ImGui.SameLine();
        }
        if (deleteButton && Widgets.Button(L($"{Icons.Trash}  Supprimer"),
                deletable > 1 ? L($"Supprimer les {deletable} objets sélectionnés (Suppr)") : L("Supprimer l'objet (Suppr)"),
                ImGui.GetContentRegionAvail().X))
            state.RequestDelete();
        if (entity.CanEdit)
        {
            if (entity.Interior is null && !entity.IsInterior)
            {
                bool follow = state.Settings.CollisionFollowsObjects;
                if (ImGui.Checkbox(L("La collision suit l'objet"), ref follow))
                    state.Settings.CollisionFollowsObjects = follow;
                ImGui.SetItemTooltip(L("En déplaçant ou en tournant un bâtiment (3 m de haut ou plus), ses surfaces de collision (.ybn) bougent avec lui. Le sol en dessous reste en place."));
            }
        }
        DrawDoorButton(entity, names);
        if (ImGui.BeginPopup("more_copies_menu"))
        {
            if (ImGui.MenuItem(L($"{Icons.Copy}  Copier position et cap : {withHeading}")))
                ImGui.SetClipboardText(withHeading);
            if (state.SelectedEntities.Count is var selected and > 1
                && ImGui.MenuItem(L($"{Icons.Copy}  Copier les {selected} objets sélectionnés (liste : nom, position, cap)")))
                ImGui.SetClipboardText(state.FormatSelection());
            if (ImGui.BeginMenu(L($"{Icons.Copy}  Format de copie : {PositionText.FormatName(state.CopyFormat)}")))
            {
                foreach (var format in Enum.GetValues<CopyFormat>())
                {
                    string example = PositionText.Position(new Vector3(215.76f, -810.12f, 30.73f), null, format);
                    if (ImGui.MenuItem($"{PositionText.FormatName(format)}  ·  {example}", null, state.CopyFormat == format))
                        state.CopyFormat = format;
                }
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem(L($"{Icons.Copy}  Copier le nom : {name}")))
                ImGui.SetClipboardText(name);
            if (entity.Map?.FilePath is { } path && ImGui.MenuItem(L($"{Icons.Copy}  Copier le nom du YMAP : {Path.GetFileNameWithoutExtension(path)}")))
                ImGui.SetClipboardText(Path.GetFileNameWithoutExtension(path));
            ImGui.Separator();
            var doors = state.SelectedEntities.Where(e => Doors.IsDoor(names.TryGet(e.ArchetypeHash))).ToList();
            if (doors.Count > 0 && ImGui.MenuItem(L($"{Icons.Copy}  Copier le script qui cache {(doors.Count > 1 ? L($"les {doors.Count} portes") : L("la porte"))} (CreateModelHide)")))
            {
                ImGui.SetClipboardText(ModelHideScript.Hide(
                    doors.Select(d => new HiddenModel(d.ArchetypeHash, names.TryGet(d.ArchetypeHash), d.Position)).ToList(), state.CopyFormat));
                state.Notify(L("Script copié : à mettre dans le client.lua d'une ressource. Aucun YMAP à exporter."));
            }
            if (doors.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(L("Autre façon de retirer une porte : la cacher par script au lieu de supprimer l'objet.\n")
                    + L("Aucun YMAP modifié, donc aucun conflit avec les autres ressources.\nMarche aussi pour les portes créées par les scripts du jeu."));
            if (doors.Count > 0 && ImGui.BeginMenu(L($"{Icons.DoorOpen}  Copier pour doorlock ({doors.Count})")))
            {
                var items = doors.Select(d => new DoorItem(d.ArchetypeHash, names.TryGet(d.ArchetypeHash), d.Position, FiveMFormat.Heading(d),
                    state.World.Maps?.GetArchetype(d.ArchetypeHash) is { } type ? DoorShape.Of(type, names.TryGet(d.ArchetypeHash)).Kind : DoorKind.Hinged)).ToList();
                int pairs = DoorLockScript.Group(items).Count(g => g.Count > 1);
                void Copy(DoorLockFormat format, bool csharp)
                {
                    ImGui.SetClipboardText(DoorLockScript.Write(items, format, csharp));
                    state.Notify(pairs > 0
                        ? L($"Configuration copiée : {doors.Count} portes, dont {pairs} portes doubles.")
                        : L($"Configuration copiée : {doors.Count} portes."));
                }
                if (ImGui.MenuItem(L("qb-doorlock (Config.DoorList)")))
                    Copy(DoorLockFormat.QbDoorlock, false);
                ImGui.SetItemTooltip(L("Une entrée par porte (deux portes proches = porte double), verrouillée, métier police.\nÀ coller dans un fichier du dossier configs de qb-doorlock."));
                if (ImGui.MenuItem(L("Natives FiveM Lua (AddDoorToSystem)")))
                    Copy(DoorLockFormat.Natives, false);
                if (ImGui.MenuItem(L("Natives FiveM C# (AddDoorToSystem)")))
                    Copy(DoorLockFormat.Natives, true);
                ImGui.Separator();
                Widgets.Hint(L("Modèle et position exacte de chaque porte : les scripts de serrure les comparent au centimètre près."));
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Box}  Voir le modèle dans la visionneuse")))
                showModel(entity.ArchetypeHash);
            if (ImGui.MenuItem(L($"{Icons.X}  Désélectionner"), L("Échap")))
                state.Select(null);
            ImGui.Separator();
            int group = state.DeletableSelection.Count;
            if (ImGui.MenuItem(group > 1 ? L($"{Icons.Trash}  Supprimer les {group} objets sélectionnés...") : L($"{Icons.Trash}  Supprimer..."), L("Suppr"), false, group > 0))
                state.RequestDelete();
            ImGui.EndPopup();
        }
        ImGui.Dummy(new Vector2(0, 2));
    }

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
        var p = entity.Position;
        Widgets.PropertyText(L("Position (doorlock)"), L($"{p.X:0.000}, {p.Y:0.000}, {p.Z:0.000}"),
            L("Position exacte utilisée par les scripts de serrure (objCoords). Menu ... > Copier pour doorlock."));
        DoorChoice(entity, archetype, names);
        Widgets.PropertyText(L("Hash du modèle"), $"{unchecked((int)archetype.NameHash)} / 0x{archetype.NameHash:X8}",
            L("objHash dans qb-doorlock (décimal), model dans ox_doorlock."));
        Widgets.EndProperties();
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
        if (doors.Count == 0 || doors.All(state.IsSelected) && state.SelectedEntities.Count == doors.Count)
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

    private void DrawPlacement(MapEntity entity, MapLibrary library, HashNames names)
    {
        if (!Widgets.BeginProperties("##placement"))
            return;

        if (entity.CanEdit)
        {
            DrawPlacementFields(entity, names);
        }
        else
        {
            var p = entity.Position;
            var r = EulerAngles.FromQuaternion(entity.Orientation);
            Widgets.PropertyText(L("Position"), $"X {p.X:0.00}   Y {p.Y:0.00}   Z {p.Z:0.00}");
            Widgets.PropertyText(L("Rotation"), $"X {r.X:0.0}°   Y {r.Y:0.0}°   Z {r.Z:0.0}°");
            Widgets.PropertyText(L("Échelle"), L($"{entity.Scale.X:0.###} (horiz.)   {entity.Scale.Z:0.###} (vert.)"));
        }
        if (!Simple)
            Widgets.PropertyText(L("Distance d'affichage"), $"{library.DrawDistance(entity):0} m");
        Widgets.EndProperties();

        if (entity.Interior is not null)
            Widgets.Hint(L("Objet défini par l'intérieur (.ytyp) : le déplacer le déplace dans toutes les copies de cet intérieur ; Ctrl+S écrit le .ytyp modifié."), wrap: true);
    }

    // Fields to move, turn and scale the object: drag with the mouse, or double-click to type a value.
    private void DrawPlacementFields(MapEntity entity, HashNames names)
    {
        if (entity != _anglesEntity || entity.Revision != _anglesRevision)
        {
            _anglesEntity = entity;
            _angles = EulerAngles.FromQuaternion(entity.Orientation);
        }

        string name = names.Describe(entity.ArchetypeHash);
        var before = Placement.Of(entity);
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        Widgets.Property(L("Position"), L("Mètres. X = est, Y = nord, Z = haut."));
        var position = entity.Position;
        float width = Widgets.SplitWidth(3);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            float value = position[i];
            if (Widgets.AxisField($"##position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
            {
                position[i] = value;
                entity.SetPlacement(position, entity.Orientation, entity.Scale);
            }
            TrackEdit(entity, before, L($"Déplacer {name}"));
        }

        Widgets.Property(L("Rotation (°)"), L("Degrés autour des axes X, Y et Z."));
        var angles = _angles;
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            float value = angles[i];
            if (Widgets.AxisField($"##rotation{i}", ref value, axes[i], width, 0.5f, "%.1f°"))
            {
                angles[i] = value;
                _angles = angles;
                entity.SetPlacement(entity.Position, EulerAngles.ToQuaternion(angles), entity.Scale);
            }
            TrackEdit(entity, before, L($"Tourner {name}"));
        }

        // Interiors cannot be scaled in the game. It stores one horizontal scale (X and Y) and one vertical scale (Z).
        if (!entity.IsInterior)
        {
            Widgets.Property(L("Échelle"), L("Horizontale (X et Y), puis verticale (Z)."));
            var scale = new Vector2(entity.Scale.X, entity.Scale.Z);
            float scaleWidth = Widgets.SplitWidth(2);
            for (int i = 0; i < 2; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                float value = scale[i];
                if (Widgets.AxisField($"##scale{i}", ref value, i == 0 ? UiTheme.AxisNeutral : UiTheme.AxisZ, scaleWidth, 0.005f, "%.3f", MinScale, 100f))
                {
                    scale[i] = Math.Max(value, MinScale);
                    entity.SetPlacement(entity.Position, entity.Orientation, new Vector3(scale.X, scale.X, scale.Y));
                }
                TrackEdit(entity, before, L($"Redimensionner {name}"));
            }
        }

        _anglesRevision = entity.Revision;
    }

    // Called after a field: remembers the placement when it is grabbed, records an undo step when it is released.
    private void TrackEdit(MapEntity entity, Placement before, string description)
    {
        if (ImGui.IsItemActivated())
            _editStart = before;
        if (ImGui.IsItemDeactivatedAfterEdit() && Placement.Of(entity) != _editStart)
            state.History.Add(state.FollowCollision(new PlacementEdit(entity, _editStart, Placement.Of(entity), description)));
    }

    private void DrawFiles(MapEntity entity, MapLibrary library, HashNames names)
    {
        if (!Widgets.BeginProperties("##files"))
            return;

        var archetype = library.GetArchetype(entity.ArchetypeHash);
        if (ArchetypeEdit.CanChange(entity))
            ArchetypeChoice(entity, library, names);
        else
            Widgets.PropertyText(L("Type (archétype)"), names.Describe(entity.ArchetypeHash));
        if (archetype is not null)
        {
            FileRow(L("Défini dans"), library.GetArchetypeFile(entity.ArchetypeHash));
            if (archetype.Kind == ArchetypeKind.Interior)
                Widgets.PropertyText(L("Intérieur (MLO)"), L($"{entity.InteriorEntities.Count} objets, {archetype.InteriorRooms.Count} salles"));
            else
                FileRow(L("Modèle 3D"), state.World.Models?.Locate(archetype)?.Path);
            if (archetype.TextureDictionaryHash != 0)
                Widgets.PropertyText(L("Textures"), $"{names.Describe(archetype.TextureDictionaryHash)}.ytd");
            var size = archetype.BoundsMax - archetype.BoundsMin;
            Widgets.PropertyText(L("Taille"), $"{size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} m");
        }
        FileRow(L("Placé par"), entity.Map?.FilePath);

        if (entity.Interior is { } interior)
        {
            Widgets.Property(L("Dans l'intérieur"));
            if (Widgets.Link($"{names.Describe(interior.ArchetypeHash)}##interior", L("Sélectionner l'intérieur")))
                state.Select(interior);
            if (entity.Definition is not null && library.GetArchetype(interior.ArchetypeHash) is { Kind: ArchetypeKind.Interior } mlo)
                RoomChoice(entity, interior, mlo);
            else if (entity.InteriorRoom >= 0)
                Widgets.PropertyText(L("Salle"), entity.InteriorRoom == 0 ? L("0 (limbo, vue de l'extérieur)") : $"{entity.InteriorRoom}");
            if (entity.InteriorPortal >= 0)
                Widgets.PropertyText(L("Attaché au portail"), $"{entity.InteriorPortal}");
            if (interior.EntitySets.Count > 0)
                EntitySetChoice(entity, interior, names);
        }
        Widgets.EndProperties();

        if (archetype is null)
            ImGui.TextColored(UiTheme.Warning, L("Type introuvable dans les fichiers .ytyp."));
        else if (archetype.Kind == ArchetypeKind.Interior
                 && Widgets.Button(L($"{Icons.Copy}  Dupliquer sous un nouveau nom..."),
                     L("Une copie de cet intérieur avec ses objets, sous un autre nom (son propre .ytyp) : la modifier ne change pas l'original.")))
            state.DuplicateInteriorRequest = entity;
    }

    // The room of an object of an interior, to change (the game draws an object only when its room is visible); warns
    // when the object stands in another room's box.
    private const int MaxTypeChoices = 40;
    private string _typeQuery = "";
    private string? _typeListQuery;
    private List<(uint Hash, string Name)> _typeMatches = [];
    private (MapLibrary Library, int Count, (uint Hash, string Name)[] Types)? _typeNames;

    // The object's type, to replace by another one (search by name, or a name / hash typed then Enter).
    private void ArchetypeChoice(MapEntity entity, MapLibrary library, HashNames names)
    {
        Widgets.Property(L("Type (archétype)"), L("Remplace le modèle de l'objet par un autre type, à la même place. ")
            + L("S'applique aux objets sélectionnés (pas aux objets des intérieurs) ; Ctrl+Z pour annuler."));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##archetype", names.Describe(entity.ArchetypeHash), ImGuiComboFlags.HeightLarge))
            return;
        if (ImGui.IsWindowAppearing())
        {
            _typeQuery = "";
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        bool enter = ImGui.InputTextWithHint("##archetype_query", L("Rechercher un type (ex. prop_pot_plant_02c)"), ref _typeQuery, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);

        string query = _typeQuery.Trim();
        if (_typeListQuery != query || _typeNames?.Library != library)
        {
            _typeListQuery = query;
            _typeMatches = FindTypes(query, library, names);
        }
        if (enter && query.Length > 0)
        {
            // Exact name or hash typed (a type whose name is unknown), else the first match.
            uint? parsed = Data.Hashing.HashText.TryParse(query);
            uint typed = parsed ?? Joaat.Hash(query.ToLowerInvariant());
            uint? chosen = IsPlaceable(library, typed) ? typed : _typeMatches.Count > 0 ? _typeMatches[0].Hash : null;
            if (chosen is { } c)
            {
                if (parsed is null && c == typed && Data.Hashing.HashText.IsName(query) && names.TryGet(c) is null)
                    names.Add(query);
                state.SetSelectionArchetype(c);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.Separator();
        if (_typeMatches.Count == 0)
            ImGui.TextDisabled(query.Length == 0 ? L("Taper un nom pour chercher.") : L("Aucun type trouvé."));
        foreach (var (hash, name) in _typeMatches)
        {
            if (ImGui.Selectable($"{name}##type{hash}", hash == entity.ArchetypeHash) && hash != entity.ArchetypeHash)
                state.SetSelectionArchetype(hash);
        }
        ImGui.EndCombo();
    }

    private static bool IsPlaceable(MapLibrary library, uint hash) =>
        library.GetArchetype(hash) is { } archetype && archetype.Kind != ArchetypeKind.Interior;

    private List<(uint Hash, string Name)> FindTypes(string query, MapLibrary library, HashNames names)
    {
        if (query.Length == 0)
            return [];
        if (_typeNames is not { } cached || cached.Library != library)
        {
            var types = library.GetArchetypeHashes().Where(h => IsPlaceable(library, h))
                .Select(h => (h, names.Describe(h))).ToArray();
            _typeNames = cached = (library, types.Length, types);
        }
        return cached.Types
            .Select(t => (t.Hash, t.Name, At: t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.At >= 0)
            .OrderBy(t => t.At == 0 ? (t.Name.Length == query.Length ? 0 : 1) : 2).ThenBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(MaxTypeChoices)
            .Select(t => (t.Hash, t.Name))
            .ToList();
    }

    private void RoomChoice(MapEntity entity, MapEntity interior, Archetype mlo)
    {
        Widgets.Property(L("Salle"), L("Le jeu n'affiche un objet que quand sa salle est visible : un objet rangé dans la mauvaise salle ")
            + L("disparaît selon l'endroit où se trouve le joueur. S'applique aux objets sélectionnés du même intérieur, dans toutes ")
            + L("les copies de l'intérieur ; enregistré dans son .ytyp."));
        if (ImGui.BeginCombo("##entity_room", entity.InteriorRoom < 0 ? L("Aucune") : RoomLabel(mlo, entity.InteriorRoom), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(L("Automatique (salle autour de l'objet)")))
                state.SetSelectionRoom(AppState.AutomaticRoom);
            ImGui.Separator();
            for (int r = 0; r < mlo.InteriorRooms.Count; r++)
            {
                if (ImGui.Selectable($"{RoomLabel(mlo, r)}##room{r}", r == entity.InteriorRoom) && r != entity.InteriorRoom)
                    state.SetSelectionRoom(r);
            }
            ImGui.EndCombo();
        }

        int around = Adding.RoomAt(interior, mlo, entity.Position + new Vector3(0, 0, 0.3f));
        if (around > 0 && around != entity.InteriorRoom)
        {
            Widgets.Property("");
            ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert} Posé dans {RoomLabel(mlo, around)}"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L("L'objet est dans la boîte d'une autre salle : en jeu, il peut disparaître. Choisir « Automatique » ")
                    + L("dans la liste pour le ranger dans la salle où il se trouve."));
        }
    }

    // The entity set holding an object of an interior, to change: the object goes into another set or out of its set.
    private void EntitySetChoice(MapEntity entity, MapEntity interior, HashNames names)
    {
        string None = L("Aucun (toujours affiché)");
        var sets = interior.EntitySets;
        int current = entity.EntitySet is { } set ? sets.ToList().IndexOf(set) : -1;
        Widgets.Property(L("Entity set"), L("Groupe d'objets activé par script. Changer le set range l'objet dans un autre set (ou l'en sort), ")
            + L("dans toutes les copies de l'intérieur ; enregistré dans son .ytyp."));
        string label = current >= 0 ? names.Describe(sets[current].NameHash) + (sets[current].IsActive ? "" : L(" (masqué)")) : None;
        if (!ImGui.BeginCombo("##entity_set", label, ImGuiComboFlags.HeightLarge))
            return;
        for (int i = -1; i < sets.Count; i++)
        {
            if (ImGui.Selectable(i < 0 ? None : $"{names.Describe(sets[i].NameHash)}##set{i}", i == current) && i != current)
                state.MoveSelectionToEntitySet(i);
        }
        ImGui.EndCombo();
    }

    // The optional groups of objects of an interior: a box per set to show or hide it (not saved: in the game,
    // scripts activate them), the script lines that activate the shown sets, and a field to name a set.
    private void DrawEntitySets(MapEntity interior, MapLibrary library, HashNames names)
    {
        var sets = interior.EntitySets;
        Widgets.Hint(L("Variantes de mobilier activées par les scripts du jeu (styles d'appartement, améliorations...). ")
            + L("Masquées par défaut, comme en jeu. Non enregistré dans les YMAP."), wrap: true);
        ImGui.Dummy(new Vector2(0, 2));

        float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
        if (Widgets.Button(L($"{Icons.Eye}  Tout afficher"), L("Afficher tous les entity sets de cet intérieur"), width))
            SetAll(library, sets, true);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.EyeOff}  Tout masquer"), L("Masquer tous les entity sets de cet intérieur"), width))
            SetAll(library, sets, false);
        ImGui.SameLine();
        var shown = sets.Where(s => s.IsActive).ToList();
        string format = state.CopyFormat == CopyFormat.CSharp ? "C#" : "Lua";
        if (Widgets.Button(L($"{Icons.Copy}  Copier le script"), shown.Count == 0
                ? L("Cochez d'abord les entity sets à activer.")
                : L($"Copier les lignes {format} qui activent les {shown.Count} entity sets affichés (GetInteriorAtCoords, ")
                    + L("ActivateInteriorEntitySet, RefreshInterior).\nFormat : celui de « Copier la position » (menu ...)."),
                width, enabled: shown.Count > 0))
        {
            var items = shown.Select(s => new EntitySetItem(s.NameHash, names.TryGet(s.NameHash))).ToList();
            ImGui.SetClipboardText(EntitySetScript.Activate(names.Describe(interior.ArchetypeHash), PointInside(interior, library),
                items, state.CopyFormat));
            int unknown = items.Count(i => i.Name is null);
            state.Notify(unknown == 0
                ? L($"Script copié ({items.Count} entity sets).")
                : L($"Script copié : {unknown} entity sets sur {items.Count} sans nom connu (lignes en commentaire)."), warning: unknown > 0);
        }

        // Naming: the files only keep hashes; a typed name whose hash is one of these sets becomes its name.
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##set_name", L("Nommer un entity set (taper son nom, Entrée)"), ref _setName, 96,
                ImGuiInputTextFlags.EnterReturnsTrue))
            NameEntitySet(interior, sets, library, names);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(L("Le jeu ne stocke que le hash des noms. Tapez le nom d'un entity set (ex. tiré d'un script) : ")
                + L("s'il correspond, il est retenu jusqu'au rechargement de la carte.\nPour les garder : un nom par ligne dans data/strings.txt."));
        // The answer stays under the field (a message in the status bar alone went unnoticed).
        if (_setNameResult is { } result && _setNameInterior == interior)
            ImGui.TextColored(result.Ok ? UiTheme.Success : UiTheme.Warning, result.Text);
        ImGui.Dummy(new Vector2(0, 2));

        for (int i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            bool active = set.IsActive;
            if (ImGui.Checkbox($"{names.Describe(set.NameHash)}##set{i}", ref active))
                library.SetEntitySetActive(set, active);
            if (ImGui.BeginPopupContextItem($"set_menu{i}"))
            {
                if (ImGui.MenuItem(L("Renommer...")))
                {
                    _renamedSet = i;
                    _renamedInterior = interior;
                    _focusRename = true;
                    _setNewName = names.TryGet(set.NameHash) ?? "";
                }
                ImGui.EndPopup();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(L("Clic droit : renommer"));
            ImGui.SameLine();
            int count = set.Entities.Count(e => !e.IsDeleted);
            ImGui.TextColored(UiTheme.TextMuted, count == 1 ? L("1 objet") : L($"{count} objets"));

            if (_renamedSet == i && _renamedInterior == interior)
            {
                ImGui.SetNextItemWidth(-1);
                if (_focusRename)
                {
                    ImGui.SetKeyboardFocusHere();
                    _focusRename = false;
                }
                if (ImGui.InputTextWithHint($"##rename{i}", L("Nouveau nom (Entrée ; Échap pour annuler)"), ref _setNewName, 96, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    RenameEntitySet(interior, i, names);
                    _renamedSet = -1;
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    _renamedSet = -1;
            }
        }

        // A new set, empty: objects are put in it with the "Entity set" list of their properties.
        ImGui.Dummy(new Vector2(0, 2));
        float buttonWidth = ImGui.CalcTextSize(L("Créer")).X + ImGui.GetStyle().FramePadding.X * 2 + 24f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        bool create = ImGui.InputTextWithHint("##new_set", L("Nom du nouvel entity set"), ref _newSetName, 96, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        create |= Widgets.Button(L($"{Icons.Boxes}  Créer"), L("Ajouter un entity set vide à cet intérieur (dans toutes ses copies, enregistré dans son .ytyp).\n")
            + L("Ensuite, sélectionner des objets de l'intérieur et choisir ce set dans leur ligne « Entity set »."), buttonWidth,
            enabled: CleanSetName(_newSetName).Length > 0);
        if (create)
            CreateEntitySet(interior, names);
    }

    private int _renamedSet = -1;
    private MapEntity? _renamedInterior;
    private bool _focusRename;
    private string _setNewName = "";
    private string _newSetName = "";

    private static string CleanSetName(string name) => name.Trim().Trim('"', '\'', '`', '«', '»', ' ', ' ');

    private void CreateEntitySet(MapEntity interior, HashNames names)
    {
        string name = CleanSetName(_newSetName);
        if (name.Length == 0 || state.World.Maps is not { } library)
            return;
        uint hash = Joaat.Hash(name);
        if (interior.EntitySets.Any(s => s.NameHash == hash))
        {
            state.Notify(L($"Cet intérieur a déjà un entity set « {name} »."), warning: true);
            return;
        }
        names.Add(name);
        state.History.Add(EntitySetEdit.Create(library, interior, hash, L($"Créer l'entity set {name}")));
        library.SetEntitySetActive(interior.EntitySets[^1], true);
        _newSetName = "";
        state.Notify(L($"Entity set « {name.ToLowerInvariant()} » créé, dans toutes les copies de l'intérieur (enregistré dans son .ytyp au Ctrl+S)."));
    }

    private void RenameEntitySet(MapEntity interior, int index, HashNames names)
    {
        string name = CleanSetName(_setNewName);
        if (name.Length == 0 || state.World.Maps is not { } library || index >= interior.EntitySets.Count)
            return;
        uint hash = Joaat.Hash(name);
        if (hash == interior.EntitySets[index].NameHash)
        {
            names.Add(name);
            return;
        }
        if (interior.EntitySets.Any(s => s.NameHash == hash))
        {
            state.Notify(L($"Cet intérieur a déjà un entity set « {name} »."), warning: true);
            return;
        }
        string old = names.Describe(interior.EntitySets[index].NameHash);
        names.Add(name);
        state.History.Add(EntitySetEdit.Rename(library, interior, index, hash, L($"Renommer l'entity set {old}")));
        state.Notify(L($"Entity set renommé en « {name.ToLowerInvariant()} » (enregistré dans son .ytyp au Ctrl+S). ")
            + L("Les scripts qui l'activent doivent utiliser ce nouveau nom."));
    }

    private static void SetAll(MapLibrary library, IEnumerable<PlacedEntitySet> sets, bool active)
    {
        foreach (var set in sets)
            library.SetEntitySetActive(set, active);
    }

    private void NameEntitySet(MapEntity interior, IReadOnlyList<PlacedEntitySet> sets, MapLibrary library, HashNames names)
    {
        // A name copied from a script or a message often comes with its quotes.
        string name = _setName.Trim().Trim('"', '\'', '`', '«', '»', ' ', ' ');
        if (name.Length == 0)
            return;
        uint hash = Joaat.Hash(name);
        _setNameInterior = interior;
        if (sets.Any(s => s.NameHash == hash))
        {
            names.Add(name);
            _setNameResult = (true, L($"Nommé : {name.ToLowerInvariant()} (0x{hash:X8})."));
            _setName = "";
        }
        else if (library.FindEntitySetInteriors(hash) is { Count: > 0 } others)
        {
            // Not a set of this interior, but of another one: the name is kept all the same.
            names.Add(name);
            string where = string.Join(", ", others.Take(3).Select(names.Describe)) + (others.Count > 3 ? "..." : "");
            _setNameResult = (true, L($"Nom retenu, mais c'est un entity set d'un autre intérieur : {where}."));
            _setName = "";
        }
        else
            _setNameResult = (false, L($"Aucun entity set ne s'appelle « {name} » (hash 0x{hash:X8}). Vérifiez l'orthographe."));
        state.Notify(_setNameResult.Value.Text, warning: !_setNameResult.Value.Ok);
    }

    // A point inside the interior, for GetInteriorAtCoords: the centre of its first real room (room 0 is the
    // outside).
    private static Vector3 PointInside(MapEntity interior, MapLibrary library)
    {
        var rooms = library.GetArchetype(interior.ArchetypeHash)?.InteriorRooms;
        if (rooms is not { Count: > 1 })
            return interior.Position;
        var room = rooms[1];
        return Vector3.Transform((room.BoundsMin + room.BoundsMax) * 0.5f, interior.Transform);
    }

    private void DrawLevelOfDetail(MapEntity entity, HashNames names, Action<MapEntity>? select = null)
    {
        select ??= e => state.Select(e);
        if (!Widgets.BeginProperties("##lod"))
            return;

        Widgets.PropertyText(L("Niveau"), LevelName(entity.LodLevel));
        if (entity.ChildLodDistance > 0f)
            Widgets.PropertyText(L("Remplacé par ses enfants"), L($"à moins de {entity.ChildLodDistance:0} m"));

        Widgets.Property(L("Parent"));
        if (entity.Parent is { } parent)
        {
            if (Widgets.Link($"{names.Describe(parent.ArchetypeHash)}##parent", L($"Sélectionner le parent : {LevelName(parent.LodLevel)}")))
                select(parent);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("aucun");
        }

        var children = entity.Children.Where(c => !c.IsDeleted).ToList();
        Widgets.Property(L("Enfants"));
        if (children.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("aucun");
        }
        else if (ImGui.TreeNode($"{children.Count}###children"))
        {
            for (int i = 0; i < children.Count && i < MaxChildrenListed; i++)
            {
                if (Widgets.Link($"{names.Describe(children[i].ArchetypeHash)}##child{i}", L("Sélectionner cet enfant")))
                    select(children[i]);
            }
            if (children.Count > MaxChildrenListed)
                Widgets.Hint(L($"... et {children.Count - MaxChildrenListed} autres"));
            ImGui.TreePop();
        }
        Widgets.EndProperties();
    }

    private static void DrawFlags(MapEntity entity, MapLibrary library)
    {
        if (!Widgets.BeginProperties("##flags"))
            return;

        Widgets.Property(L("De l'objet"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"0x{entity.Flags:X8}");
        var known = KnownFlags.Where(f => (entity.Flags & f.Flag) != 0).Select(f => f.Text).ToList();
        if (known.Count > 0)
            ImGui.TextWrapped(string.Join(", ", known));

        if (library.GetArchetype(entity.ArchetypeHash) is { } archetype)
        {
            Widgets.Property(L("Du type"));
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"0x{archetype.Flags:X8}");
            if (archetype.IsShadowOrReflectionProxy)
                ImGui.TextWrapped(L("doublure pour les ombres et reflets (jamais affichée)"));
        }
        Widgets.EndProperties();
    }

    private static void FileRow(string label, string? path) =>
        Widgets.PropertyText(label, path is null ? "?" : Path.GetFileName(path), path);

    private static string LevelName(LodLevel level) => level switch
    {
        LodLevel.Hd => L("détail (HD)"),
        LodLevel.OrphanHd => L("détail sans LOD"),
        LodLevel.Lod => L("LOD (vue moyenne)"),
        _ => L($"{level.ToString().ToUpperInvariant()} (vue lointaine)"),
    };

    private Vector3 _nodeEditStart;

    private void DrawPathNodePosition(PathNode node, PathNetwork network)
    {
        Widgets.Property(L("Position"), L("Mètres. X = est, Y = nord, Z = haut. Aussi avec les poignées (outil Déplacer, touche 2)."));
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
        var before = node.Position;
        var position = before;
        float width = Widgets.SplitWidth(3);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            float value = position[i];
            if (Widgets.AxisField($"##node_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
            {
                position[i] = value;
                network.MoveNode(node, position);
            }
            if (ImGui.IsItemActivated())
                _nodeEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && node.Position != _nodeEditStart)
                state.History.Add(new PathNodeMoveEdit(network, node, _nodeEditStart, node.Position, L($"Déplacer le nœud {node.Id.AreaId}:{node.Id.NodeId}")));
        }
    }

    private OccluderBox? _boxEditStart;

    // Centre, size and rotation of a box occluder, changed live; one undo step when a field is released, the box then
    // rounded as the game's file stores it.
    private void DrawOccluderBoxFields(OccluderSelection occluder)
    {
        var file = occluder.File;
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
        float width = Widgets.SplitWidth(3);

        void Track(OccluderBox before)
        {
            if (ImGui.IsItemActivated())
                _boxEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _boxEditStart is { } start)
            {
                var end = file.Boxes[occluder.Index].Quantized();
                OccluderMaps.SetBox(file, end);
                if (end != start)
                    state.History.Add(new OccluderBoxEdit(file, start, end, L($"Modifier la boîte d'occlusion {occluder.Index}")));
                _boxEditStart = null;
            }
        }

        Widgets.Property(L("Centre"), L("Mètres. X = est, Y = nord, Z = haut. Arrondi à 25 cm (précision du format du jeu). Aussi avec les poignées (touche 2)."));
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            var box = file.Boxes[occluder.Index];
            var center = box.Center;
            float value = center[i];
            if (Widgets.AxisField($"##occl_center{i}", ref value, axes[i], width, 0.05f, "%.2f"))
            {
                center[i] = value;
                OccluderMaps.SetBox(file, box with { Center = center });
            }
            Track(box);
        }

        Widgets.Property(L("Taille"), L("Longueur (X), largeur (Y) et hauteur (Z) de la boîte, en mètres, dans son propre sens. Aussi avec les poignées (touche 4)."));
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            var box = file.Boxes[occluder.Index];
            var size = box.Size;
            float value = size[i];
            if (Widgets.AxisField($"##occl_size{i}", ref value, axes[i], width, 0.05f, "%.2f", OccluderBox.PositionUnit, 8000f))
            {
                size[i] = MathF.Max(OccluderBox.PositionUnit, value);
                OccluderMaps.SetBox(file, box with { Size = size });
            }
            Track(box);
        }

        Widgets.Property(L("Rotation Z"), L("Degrés. Une boîte d'occlusion ne tourne qu'autour de l'axe vertical. Aussi avec les poignées (touche 3)."));
        {
            var box = file.Boxes[occluder.Index];
            float degrees = (box.Angle * 180f / MathF.PI + 360f) % 360f;
            if (Widgets.AxisField("##occl_angle", ref degrees, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                OccluderMaps.SetBox(file, box with { Angle = degrees * MathF.PI / 180f });
            Track(box);
        }

        if (file.IsBoxChanged(occluder.Index))
            Widgets.PropertyText(L("État"), L("modifiée (pas encore enregistrée)"), L("L'enregistrement des boîtes d'occlusion arrive à l'étape suivante."), UiTheme.Warning);
    }

    private void DrawOccluder(OccluderSelection occluder)
    {
        var (min, max) = occluder.Bounds();
        var size = max - min;
        var center = occluder.Center;
        bool isBox = occluder.Box is not null;
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted($"{Icons.ScanEye}  {(isBox ? "Boîte d'occlusion" : "Maillage d'occlusion")} {occluder.Index}");
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {occluder.File.FileName}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(occluder.File.GamePath);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à l'occluder")))
            state.FocusOn(center, MathF.Max(size.Length() / 2, 5f));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), state.FormatPosition(center)))
            ImGui.SetClipboardText(state.FormatPosition(center));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedOccluder();
            return;
        }

        if (Widgets.Section(isBox ? L("Boîte") : L("Maillage"), "occluder") && Widgets.BeginProperties("##occluder"))
        {
            if (occluder.Box is not null)
                DrawOccluderBoxFields(occluder);
            else if (occluder.Model is { } model)
            {
                Widgets.PropertyText(L("Triangles"), Widgets.Count(model.TriangleCount));
                Widgets.PropertyText(L("Sommets"), Widgets.Count(model.Vertices.Count), L("255 au plus par maillage."));
                Widgets.PropertyText(L("Emprise"), $"{size.X:0} x {size.Y:0} x {size.Z:0} m");
                Widgets.PropertyText(L("Centre"), $"{center.X:0.#}, {center.Y:0.#}, {center.Z:0.#}");
            }
            Widgets.PropertyText(L("Fichier"), occluder.File.FileName, occluder.File.GamePath);
            var occluders = occluder.File.Occluders;
            Widgets.PropertyText(L("Dans ce fichier"), L($"{occluders.Boxes.Count} boîte(s), {occluders.Models.Count} maillage(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu ne dessine pas ce qui est caché derrière un occluder. Échap pour désélectionner ; ")
            + L("masquez le calque Occlusion pour sélectionner les bâtiments."), wrap: true);
    }

    private CarGenerator? _spotEditStart;
    private const float MinSpotSide = 0.5f;

    private static string HashText(uint hash, HashNames names) => hash == 0 ? "" : names.Describe(hash);

    // A typed name or hash (0x..., or decimal): its hash, 0 for an empty text; null when the text is not valid.
    private uint? ParseHashText(string text, HashNames names)
    {
        text = text.Trim().Trim('"', '\'');
        if (text.Length == 0)
            return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                return hex;
        }
        else if (text.All(char.IsAsciiDigit) && uint.TryParse(text, out uint number))
            return number;
        else if (!text.Any(char.IsWhiteSpace))
        {
            names.Add(text);
            return Joaat.Hash(text);
        }
        state.Notify(L("Nom invalide : un nom sans espace ou un hash (0x...) est attendu."), warning: true);
        return null;
    }

    // Heading as in FiveM: degrees from north, counterclockwise (the front of a car is its Y axis).
    private static readonly uint NoneHash = NodeRunner.Data.Hashing.Joaat.Hash("none");

    // Heading of a scenario point in degrees, as the game counts it (0 = north, turning left).
    private static float ScenarioHeading(NodeRunner.Data.Scenarios.ScenarioPoint point) =>
        ((point.Heading * 180f / MathF.PI) % 360f + 360f) % 360f;

    private void DrawScenario(NodeRunner.Data.Scenarios.ScenarioPoint point, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Users}  Point de scénario {point.Index}"));
        ImGui.PopFont();
        if (point.Region is { } region)
        {
            Widgets.Hint($"{Icons.File}  {region.Name}.ymt");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && region.Path is { } path)
                ImGui.SetTooltip(path);
        }
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller au point")))
            state.FocusOn(point.Position, 4f);
        ImGui.SameLine();
        string copied = state.FormatPosition(point.Position, ScenarioHeading(point));
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        if (Widgets.Button(L($"{Icons.Copy}  Dupliquer"), L("Nouveau point à 2 m, avec les mêmes réglages, à placer ensuite avec les poignées.")))
        {
            state.AddScenarioPoint(point);
            return;
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedScenario();
            return;
        }

        string Name(uint hash, string empty) => hash == 0 || hash == NoneHash ? empty : names.Describe(hash);
        if (state.World.Scenarios is not { } set)
            return;
        string change = L($"Modifier le point de scénario {point.Index}");
        void Live(NodeRunner.Data.Scenarios.ScenarioValues after) => set.SetValues(point, after);
        void Commit(NodeRunner.Data.Scenarios.ScenarioValues after)
        {
            var before = point.Values;
            if (after == before)
                return;
            Live(after);
            state.History.Add(new ScenarioEdit(set, point, before, after, change));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(NodeRunner.Data.Scenarios.ScenarioValues before)
        {
            if (ImGui.IsItemActivated())
                _scenarioEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _scenarioEditStart is { } start)
            {
                if (point.Values != start)
                    state.History.Add(new ScenarioEdit(set, point, start, point.Values, change));
                _scenarioEditStart = null;
            }
        }
        void ByteField(string id, byte value, int min, int max, string format, Func<NodeRunner.Data.Scenarios.ScenarioValues, byte, NodeRunner.Data.Scenarios.ScenarioValues> with)
        {
            var current = point.Values;
            int v = value;
            if (ImGui.DragInt(id, ref v, 0.2f, min, max, format))
                Live(with(current, (byte)Math.Clamp(v, min, max)));
            Track(current);
        }
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Scénario"), "scenario") && Widgets.BeginProperties("##scenario"))
        {
            Widgets.Property(L("Position"), L("Pied du poteau, en mètres. X = est, Y = nord, Z = haut."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = point.Values;
                var position = current.Position;
                float value = position[i];
                if (Widgets.AxisField($"##scenario_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    position[i] = value;
                    Live(current with { Position = position });
                }
                Track(current);
            }

            Widgets.Property(L("Cap"), L("Sens dans lequel le PNJ ou le véhicule regarde, en degrés depuis le nord (comme le cap d'un vector4 FiveM)."));
            {
                var current = point.Values;
                float value = ScenarioHeading(point);
                if (Widgets.AxisField("##scenario_heading", ref value, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                    Live(current with { Heading = ((value % 360f + 360f) % 360f) * MathF.PI / 180f });
                Track(current);
            }

            Widgets.Property(L("Type"), L("Activité jouée à ce point (liste des scénarios du jeu, scenarios.meta)."));
            if (point.Region is not null)
            {
                var types = set.Types.ToList();
                string shown = point.TypeName ?? names.Describe(point.TypeHash);
                if (ImGui.BeginCombo("##scenario_type", shown, ImGuiComboFlags.HeightLarge))
                {
                    foreach (var (hash, name) in types)
                    {
                        if (ImGui.Selectable(name, hash == point.TypeHash))
                            Commit(point.Values with { TypeHash = hash });
                    }
                    ImGui.EndCombo();
                }
            }
            else
                ImGui.TextUnformatted(point.TypeName ?? names.Describe(point.TypeHash));
            Widgets.PropertyText(L("Catégorie"), NodeRunner.App.World.ScenarioLayer.Label(point.Category));

            Widgets.Property(L("Horaires"), L("Heure de début et de fin (0 à 24). Les deux pareilles = toute la journée."));
            {
                float half = Widgets.SplitWidth(2);
                ImGui.SetNextItemWidth(half);
                ByteField("##scenario_start", point.StartHour, 0, 24, L("de %d h"), (v, b) => v with { StartHour = b });
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetNextItemWidth(half);
                ByteField("##scenario_end", point.EndHour, 0, 24, L("à %d h"), (v, b) => v with { EndHour = b });
            }

            Widgets.Property(L("Probabilité"), L("Chance d'utiliser le point, en %. 0 = celle du type."));
            ByteField("##scenario_probability", point.Probability, 0, 100, point.Probability == 0 ? L("celle du type") : "%d %%", (v, b) => v with { Probability = b });

            Widgets.Property(L("Disponible"));
            {
                string[] modes = [L("solo et Online"), L("Online seulement"), L("solo seulement")];
                int mode = Math.Min((int)point.Availability, 2);
                if (ImGui.Combo("##scenario_availability", ref mode, modes, modes.Length))
                    Commit(point.Values with { Availability = (byte)mode });
            }

            Widgets.Property(L("Rayon"), L("Mètres autour du point où le PNJ peut se placer ; 0 = sur le point."));
            ByteField("##scenario_radius", point.Radius, 0, 255, "%d m", (v, b) => v with { Radius = b });

            Widgets.Property(L("Le PNJ part après"), L("Secondes avant que le PNJ quitte le point ; 0 = quand il veut."));
            ByteField("##scenario_leave", point.TimeTillPedLeaves, 0, 255, "%d s", (v, b) => v with { TimeTillPedLeaves = b });

            Widgets.PropertyText(point.Category == NodeRunner.Data.Scenarios.ScenarioCategory.Vehicle ? L("Véhicules") : L("Modèles"),
                Name(point.ModelSetHash, L("tous")), L("Groupe de modèles (model set) qui peut apparaître ici."));
            if (Name(point.GroupHash, "") is { Length: > 0 } group)
                Widgets.PropertyText(L("Groupe"), group, L("Les points d'un groupe sont activés ou coupés ensemble (par un script)."));
            if (Name(point.InteriorHash, "") is { Length: > 0 } interior)
                Widgets.PropertyText(L("Intérieur"), interior);
            if (Name(point.RequiredMapHash, "") is { Length: > 0 } map)
                Widgets.PropertyText(L("YMAP requis"), map, L("Le point n'est utilisé que si ce fichier de carte est chargé."));
            if (!Simple)
                Widgets.PropertyText(L("Drapeaux"), $"0x{point.Flags:X8}");
            Widgets.EndProperties();
            Widgets.Hint(L("Enregistré dans le .ymt du projet au Ctrl+S, ajouté à l'export FiveM."), wrap: true);
        }
    }

    private NodeRunner.Data.Scenarios.ScenarioValues? _scenarioEditStart;

    private static float CarHeading(CarGenerator spot) => ((spot.Angle * 180f / MathF.PI - 90f) % 360f + 360f) % 360f;

    private void DrawCarGenerator(CarGeneratorSelection selection, CarGenerator spot, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Car}  Place de véhicule {selection.Index}"));
        ImGui.PopFont();
        string file = Path.GetFileName(selection.Map.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {file}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && selection.Map.FilePath is { } path)
            ImGui.SetTooltip(path);
        float heading = CarHeading(spot);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la place")))
            state.FocusOn(spot.Position, MathF.Max(spot.Length, 4f));
        ImGui.SameLine();
        string copied = state.FormatPosition(spot.Position, heading);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedCarGenerator();
            return;
        }

        var map = selection.Map;
        int index = selection.Index;
        CarGenerator Current() => map.CarGenerators[index];
        void Live(CarGenerator after) => map.SetCarGenerator(index, after);
        void Commit(CarGenerator after, string description)
        {
            var before = Current();
            if (after == before)
                return;
            Live(after);
            state.History.Add(new CarGeneratorEdit(map, index, before, after, description));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(CarGenerator before, string description)
        {
            if (ImGui.IsItemActivated())
                _spotEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _spotEditStart is { } start)
            {
                if (Current() != start)
                    state.History.Add(new CarGeneratorEdit(map, index, start, Current(), description));
                _spotEditStart = null;
            }
        }
        string Change(string what) => L($"Modifier la place de véhicule {index} ({what})");
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Place"), "cargen_place") && Widgets.BeginProperties("##cargen_place"))
        {
            Widgets.Property(L("Position"), L("Centre de la place, en mètres. X = est, Y = nord, Z = haut. Aussi avec les poignées (touche 2)."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var position = current.Position;
                float value = position[i];
                if (Widgets.AxisField($"##cargen_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    position[i] = value;
                    Live(current with { Position = position });
                }
                Track(current, Change(L("position")));
            }

            Widgets.Property(L("Cap"), L("Sens de l'avant du véhicule, en degrés depuis le nord (comme le cap d'un vector4 FiveM). Aussi avec les poignées (touche 3)."));
            {
                var current = Current();
                float value = CarHeading(current);
                if (Widgets.AxisField("##cargen_heading", ref value, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                {
                    float angle = (value + 90f) * MathF.PI / 180f;
                    Live(current with { Orientation = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * current.Length });
                }
                Track(current, Change(L("cap")));
            }

            Widgets.Property(L("Longueur"), L("Mètres, le long du véhicule. Aussi avec les poignées (touche 4)."));
            {
                var current = Current();
                float value = current.Length;
                if (Widgets.AxisField("##cargen_length", ref value, UiTheme.AxisX, ImGui.GetContentRegionAvail().X, 0.02f, "%.2f m", MinSpotSide, 100f))
                {
                    var forward = current.Length > 1e-4f ? current.Orientation / current.Length : Vector2.UnitX;
                    Live(current with { Orientation = forward * MathF.Max(MinSpotSide, value) });
                }
                Track(current, Change(L("longueur")));
            }

            Widgets.Property(L("Largeur"), L("Mètres, en travers du véhicule. Aussi avec les poignées (touche 4)."));
            {
                var current = Current();
                float value = current.Width;
                if (Widgets.AxisField("##cargen_width", ref value, UiTheme.AxisY, ImGui.GetContentRegionAvail().X, 0.02f, "%.2f m", MinSpotSide, 100f))
                    Live(current with { Width = MathF.Max(MinSpotSide, value) });
                Track(current, Change(L("largeur")));
            }
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Véhicule"), "cargen_vehicle") && Widgets.BeginProperties("##cargen_vehicle"))
        {
            var preview = state.SpotPreview;
            string shown = preview.ModelName is not { } shownName ? L("aucun")
                : preview.Error is { } previewError ? L($"{shownName} (illisible : {previewError})")
                : preview.IsLoading ? L($"{shownName} (chargement...)")
                : $"{shownName} · {preview.Source}";
            Widgets.PropertyText(L("Aperçu"), shown, L("Véhicule du jeu affiché sur la place dans la vue 3D, pour juger la taille et le sens. Le jeu peut en choisir un autre."));

            Widgets.Property(L("Modèle"), L("Nom du modèle garé à cette place (ex. adder) ou son hash (0x...). Vide = au hasard : un modèle du groupe, sinon un véhicule de la zone."));
            if (CommitText("##cargen_model", HashText(spot.CarModel, names), L("au hasard"), out string model) && ParseHashText(model, names) is { } modelHash)
                Commit(Current() with { CarModel = modelHash }, Change(L("modèle")));

            Widgets.Property(L("Groupe"), L("Groupe de population (popgroups) dans lequel le jeu choisit le modèle, ou son hash (0x...). Vide = aucun."));
            if (CommitText("##cargen_group", HashText(spot.PopGroup, names), L("aucun"), out string group) && ParseHashText(group, names) is { } groupHash)
                Commit(Current() with { PopGroup = groupHash }, Change(L("groupe")));

            Widgets.Property(L("Couleurs"), L("Numéros des couleurs de carrosserie (body color remap) 1 à 4 ; -1 = au hasard."));
            {
                float width = Widgets.SplitWidth(4);
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    int value = i switch { 0 => current.Color1, 1 => current.Color2, 2 => current.Color3, _ => current.Color4 };
                    ImGui.SetNextItemWidth(width);
                    if (ImGui.DragInt($"##cargen_color{i}", ref value, 0.2f, -1, 255))
                    {
                        value = Math.Clamp(value, -1, 255);
                        Live(i switch
                        {
                            0 => current with { Color1 = value },
                            1 => current with { Color2 = value },
                            2 => current with { Color3 = value },
                            _ => current with { Color4 = value },
                        });
                    }
                    Track(current, Change(L("couleurs")));
                }
            }

            Widgets.Property(L("Livrée"), L("Numéro de la livrée (motifs, logos) ; -1 = au hasard."));
            {
                var current = Current();
                int value = current.Livery;
                if (ImGui.DragInt("##cargen_livery", ref value, 0.1f, -1, 127))
                    Live(current with { Livery = Math.Clamp(value, -1, 127) });
                Track(current, Change(L("livrée")));
            }

            if (!Simple)
                Widgets.Property(L("Drapeaux"), L("Réglages de la place (heures d'apparition, priorité...), en hexadécimal. Le sens de chaque bit n'est pas encore connu."));
            if (!Simple && CommitText("##cargen_flags", $"0x{spot.Flags:X}", "0x0", out string flagsText))
            {
                string hex = flagsText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? flagsText[2..] : flagsText;
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint flags))
                    Commit(Current() with { Flags = flags }, Change(L("drapeaux")));
                else
                    state.Notify(L("Drapeaux invalides : un nombre hexadécimal est attendu (ex. 0xE60)."), warning: true);
            }
            Widgets.EndProperties();
        }

        if (!Simple && Widgets.Section(L("Fichier"), "cargen_file") &&Widgets.BeginProperties("##cargen_file"))
        {
            Widgets.PropertyText(L("YMAP"), file, selection.Map.FilePath);
            Widgets.PropertyText(L("Dans ce YMAP"), L($"{selection.Map.KeptCarGenerators.Count()} place(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu peut garer un véhicule à cette place quand le joueur passe à proximité. Échap pour désélectionner ; ")
            + L("masquez le calque Véhicules garés pour sélectionner les objets."), wrap: true);
    }

    // Hours when a light is on, from its mask (bit n = on during hour n), as ranges: "de 20 h à 6 h".
    internal static string LightHours(uint mask)
    {
        mask &= 0xFFFFFF;
        if (mask == 0xFFFFFF)
            return L("toute la journée");
        if (mask == 0)
            return L("jamais");
        bool On(int hour) => (mask >> ((hour % 24 + 24) % 24) & 1) != 0;
        var ranges = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            if (!On(h) || On(h - 1))
                continue;
            int end = h;
            while (On(end + 1) && end + 1 < h + 24)
                end++;
            ranges.Add(L($"de {h} h à {(end + 1) % 24} h"));
        }
        return string.Join(", ", ranges);
    }

    private void DrawLodLight(LodLightSelection selection, DistantLodLight light)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Moon}  Lumière LOD {selection.Index}"));
        ImGui.PopFont();
        string distantFile = Path.GetFileName(selection.Distant.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {distantFile}");
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la lumière")))
            state.FocusOn(light.Position, 6f);
        ImGui.SameLine();
        string copied = state.FormatPosition(light.Position, null);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Retire la lumière et ses détails de leurs deux fichiers. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedLodLight();
            return;
        }

        var details = selection.Details;
        if (Widgets.Section(L("Lumière"), "lodlight_light") && Widgets.BeginProperties("##lodlight_light"))
        {
            var distant = selection.Distant;
            var lod = selection.Lod;
            int index = selection.Index;
            LodLightValues Current() => LodLightValues.Of(distant, lod, index);
            void Live(LodLightValues after) => after.ApplyTo(distant, lod, index);
            void Commit(LodLightValues after, string description)
            {
                var before = Current();
                if (after == before)
                    return;
                Live(after);
                state.History.Add(new LodLightEdit(distant, lod, index, before, after, description));
            }
            // Live change while a field is dragged; one undo step when it is released.
            void Track(LodLightValues before, string description)
            {
                if (ImGui.IsItemActivated())
                    _lightEditStart = (selection, before);
                if (ImGui.IsItemDeactivatedAfterEdit() && _lightEditStart is { } edit)
                {
                    CommitLightEdit(edit, description);
                    _lightEditStart = null;
                }
            }
            string Change(string what) => L($"Modifier la lumière LOD {index} ({what})");
            static DistantLodLight WithColor(DistantLodLight l, int r, int g, int b, int i) =>
                l with { Rgbi = DistantLodLight.Pack((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(i, 0, 255)) };
            Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

            if (details is { } type)
            {
                if (Simple)
                    Widgets.PropertyText(L("Type"), LightTypeName(type.Type));
                else
                {
                    Widgets.Property(L("Type"), L("Forme de la lumière : ponctuelle (tout autour), spot (un cône dans une direction) ou tube."));
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.BeginCombo("##lodlight_type", LightTypeName(type.Type)))
                    {
                        foreach (var t in (LodLightType[])[LodLightType.Point, LodLightType.Spot, LodLightType.Capsule])
                        {
                            if (ImGui.Selectable(LightTypeName(t), t == type.Type) && t != type.Type && Current().Details is { } now)
                                Commit(Current() with { Details = now.WithType(t) }, Change(L("type")));
                        }
                        ImGui.EndCombo();
                    }
                }
            }

            Widgets.Property(L("Couleur"), L("Couleur de la lumière (rouge, vert, bleu de 0 à 255). Cliquez le carré pour choisir dans une palette."));
            {
                var color = new Vector4(light.Red / 255f, light.Green / 255f, light.Blue / 255f, 1f);
                if (ImGui.ColorButton("##lodlight_color", color, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha,
                        new Vector2(ImGui.GetFrameHeight() * 1.6f, ImGui.GetFrameHeight())))
                {
                    _lightPickerStart = (selection, Current());
                    ImGui.OpenPopup("##lodlight_picker");
                }
                if (ImGui.BeginPopup("##lodlight_picker"))
                {
                    var rgb = new Vector3(color.X, color.Y, color.Z);
                    if (ImGui.ColorPicker3("##lodlight_picker_rgb", ref rgb, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSidePreview))
                    {
                        var current = Current();
                        Live(current with
                        {
                            Light = WithColor(current.Light, (int)MathF.Round(rgb.X * 255), (int)MathF.Round(rgb.Y * 255), (int)MathF.Round(rgb.Z * 255), current.Light.Intensity),
                        });
                    }
                    ImGui.EndPopup();
                }
                else if (_lightPickerStart is { } picked)
                {
                    // The palette closed: one undo step for the whole choice.
                    CommitLightEdit(picked, L($"Modifier la lumière LOD {picked.Selection.Index} (couleur)"));
                    _lightPickerStart = null;
                }
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
                float width = (ImGui.GetContentRegionAvail().X - spacing * 2) / 3;
                string[] channels = ["R %d", "G %d", "B %d"];
                for (int c = 0; c < 3; c++)
                {
                    if (c > 0) ImGui.SameLine(0, spacing);
                    var current = Current();
                    var l = current.Light;
                    int value = c == 0 ? l.Red : c == 1 ? l.Green : l.Blue;
                    ImGui.SetNextItemWidth(width);
                    if (ImGui.DragInt($"##lodlight_rgb{c}", ref value, 0.5f, 0, 255, channels[c]))
                        Live(current with { Light = WithColor(l, c == 0 ? value : l.Red, c == 1 ? value : l.Green, c == 2 ? value : l.Blue, l.Intensity) });
                    Track(current, Change(L("couleur")));
                }
            }

            Widgets.Property(L("Intensité"), L("Force de la lumière vue de loin, de 0 à 255."));
            {
                var current = Current();
                int value = current.Light.Intensity;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragInt("##lodlight_intensity", ref value, 0.5f, 0, 255, "%d / 255"))
                    Live(current with { Light = WithColor(current.Light, current.Light.Red, current.Light.Green, current.Light.Blue, value) });
                Track(current, Change(L("intensité")));
            }

            Widgets.Property(L("Position"), L("Position de la lumière, en mètres. Aussi avec les poignées (touche 2)."));
            {
                float width = Widgets.SplitWidth(3);
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    var position = current.Light.Position;
                    float value = position[i];
                    if (Widgets.AxisField($"##lodlight_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                    {
                        position[i] = value;
                        Live(current with { Light = current.Light with { Position = position } });
                    }
                    Track(current, Change(L("position")));
                }
            }

            if (details is { } d)
            {
                static LodLightValues WithDetails(LodLightValues v, Func<LodLight, LodLight> change) =>
                    v.Details is { } now ? v with { Details = change(now) } : v;

                Widgets.Property(L("Allumée"), L("Heures où le jeu allume la lumière : de la première à la seconde (0 à 23) ; les mêmes = toute la journée. ")
                    + L($"Actuellement : {LightHours(d.HourMask)}."));
                {
                    var (first, last) = FirstHourRange(d.HourMask);
                    float half = Widgets.SplitWidth(2);
                    for (int i = 0; i < 2; i++)
                    {
                        if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                        var current = Current();
                        int value = i == 0 ? first : last;
                        ImGui.SetNextItemWidth(half);
                        if (ImGui.DragInt($"##lodlight_hour{i}", ref value, 0.1f, 0, 23, i == 0 ? L("de %d h") : L("à %d h")))
                        {
                            value = Math.Clamp(value, 0, 23);
                            uint mask = LodLight.HourRange(i == 0 ? value : first, i == 0 ? last : value);
                            Live(WithDetails(current, x => x.WithHourMask(mask)));
                        }
                        Track(current, Change(L("heures")));
                    }
                }

                Widgets.Property(L("Portée"), L("Distance éclairée autour de la lumière, en mètres (cercle dans la vue 3D)."));
                {
                    var current = Current();
                    float value = d.Falloff;
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.DragFloat("##lodlight_falloff", ref value, 0.05f, 0.01f, 500f, "%.2f m"))
                        Live(WithDetails(current, x => x with { Falloff = Math.Clamp(value, 0.01f, 500f) }));
                    Track(current, Change(L("portée")));
                }

                if (!Simple)
                {
                    Widgets.Property(L("Exposant de la portée"), L("Vitesse à laquelle la lumière baisse avec la distance."));
                    {
                        var current = Current();
                        float value = d.FalloffExponent;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.DragFloat("##lodlight_exponent", ref value, 0.05f, 0f, 256f, "%.2f"))
                            Live(WithDetails(current, x => x with { FalloffExponent = Math.Clamp(value, 0f, 256f) }));
                        Track(current, Change(L("exposant")));
                    }

                    Widgets.Property(L("Direction"), L("Sens dans lequel éclaire un spot (ligne dans la vue 3D), en X, Y, Z."));
                    {
                        float width = Widgets.SplitWidth(3);
                        for (int i = 0; i < 3; i++)
                        {
                            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                            var current = Current();
                            var direction = current.Details?.Direction ?? d.Direction;
                            float value = direction[i];
                            if (Widgets.AxisField($"##lodlight_direction{i}", ref value, axes[i], width, 0.01f, "%.3f"))
                            {
                                direction[i] = value;
                                Live(WithDetails(current, x => x with { Direction = direction }));
                            }
                            Track(current, Change(L("direction")));
                        }
                    }

                    Widgets.Property(L("Cône"), L("Ouverture d'un spot (intérieur, extérieur), ou longueur d'un tube : valeurs brutes du fichier, de 0 à 255."));
                    {
                        float half = Widgets.SplitWidth(2);
                        for (int i = 0; i < 2; i++)
                        {
                            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                            var current = Current();
                            var now = current.Details ?? d;
                            int value = i == 0 ? now.ConeInnerAngle : now.ConeOuterAngleOrCapExt;
                            ImGui.SetNextItemWidth(half);
                            if (ImGui.DragInt($"##lodlight_cone{i}", ref value, 0.3f, 0, 255, i == 0 ? L("intérieur %d") : L("extérieur %d")))
                            {
                                byte b = (byte)Math.Clamp(value, 0, 255);
                                Live(WithDetails(current, x => i == 0 ? x with { ConeInnerAngle = b } : x with { ConeOuterAngleOrCapExt = b }));
                            }
                            Track(current, Change(L("cône")));
                        }
                    }

                    Widgets.Property(L("Halo"), L("Force du halo (corona) autour de la lumière, de 0 à 255."));
                    {
                        var current = Current();
                        int value = d.CoronaIntensity;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.DragInt("##lodlight_corona", ref value, 0.5f, 0, 255, "%d / 255"))
                            Live(WithDetails(current, x => x with { CoronaIntensity = (byte)Math.Clamp(value, 0, 255) }));
                        Track(current, Change(L("halo")));
                    }

                    Widgets.PropertyText(L("Hash"), $"0x{d.Hash:X8}", L("Identifiant de la lumière d'origine sur son objet."));
                    Widgets.PropertyText(L("Drapeaux"), $"0x{d.TimeAndStateFlags:X8}", L("Heures (bits 0 à 23), réglages (24, 25), type (26 à 28)."));
                }
            }
            else
                Widgets.PropertyText(L("Détails"), L("aucun"), L("Ce fichier de lumières lointaines n'a pas de fichier lodlights pour cette lumière."));
            Widgets.EndProperties();
        }
        if (!Simple && Widgets.Section(L("Fichiers"), "lodlight_files") && Widgets.BeginProperties("##lodlight_files"))
        {
            Widgets.PropertyText(L("Lumières lointaines"), distantFile, selection.Distant.FilePath);
            Widgets.PropertyText(L("Détails"), Path.GetFileName(selection.Lod?.FilePath ?? "") is { Length: > 0 } lodFile ? lodFile : L("aucun"), selection.Lod?.FilePath);
            Widgets.PropertyText(L("Catégorie"), selection.Distant.DistantCategory switch { 0 => L("petite"), 1 => L("moyenne"), 2 => L("grande"), var c => c.ToString() },
                L("Taille des lumières du fichier (small, medium, large dans son nom)."));
            Widgets.PropertyText(L("Lampadaires"), selection.Index < selection.Distant.DistantStreetLights ? L("oui") : L("non"),
                L("Les premières lumières de chaque fichier sont des lampadaires de rue."));
            Widgets.PropertyText(L("Dans ce fichier"), L($"{selection.Distant.DistantLights.Count} lumière(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu remplace par ces lumières les vraies lumières des objets quand on les voit de loin, la nuit. Échap pour désélectionner ; ")
            + L("masquez le calque Lumières LOD pour sélectionner les objets."), wrap: true);
    }

    // A light whose field (or colour palette) is being changed, and its values before.
    private (LodLightSelection Selection, LodLightValues Start)? _lightEditStart, _lightPickerStart;

    // One undo step from the values before to the light's current ones (none if unchanged).
    private void CommitLightEdit((LodLightSelection Selection, LodLightValues Start) edit, string description)
    {
        var (s, before) = edit;
        if (s.Light is null)
            return;
        var now = LodLightValues.Of(s.Distant, s.Lod, s.Index);
        if (now != before)
            state.History.Add(new LodLightEdit(s.Distant, s.Lod, s.Index, before, now, description));
    }

    private static string LightTypeName(LodLightType type) => type switch
    {
        LodLightType.Point => L("Ponctuelle (dans toutes les directions)"),
        LodLightType.Spot => L("Spot (dans une direction)"),
        LodLightType.Capsule => L("Tube"),
        var other => L($"Inconnu ({(int)other})"),
    };

    // The first hours a light is on, as start and end (end excluded): (0, 0) for the whole day or never.
    internal static (int Start, int End) FirstHourRange(uint mask)
    {
        mask &= 0xFFFFFF;
        if (mask is 0 or 0xFFFFFF)
            return (0, 0);
        bool On(int hour) => (mask >> ((hour % 24 + 24) % 24) & 1) != 0;
        for (int h = 0; h < 24; h++)
        {
            if (!On(h) || On(h - 1))
                continue;
            int end = h;
            while (On(end + 1))
                end++;
            return (h, (end + 1) % 24);
        }
        return (0, 0);
    }

    private TimecycleModifier? _zoneEditStart;
    private const float MinZoneSide = 0.5f;

    private void DrawTimecycle(TimecycleSelection selection, TimecycleModifier zone, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Sunset}  Zone de timecycle {selection.Index}"));
        ImGui.PopFont();
        string file = Path.GetFileName(selection.Map.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {file}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && selection.Map.FilePath is { } path)
            ImGui.SetTooltip(path);
        var size = zone.Size;
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la zone")))
            state.FocusOn(zone.Center, MathF.Max(MathF.Max(size.X, size.Y), MathF.Max(size.Z, 4f)));
        ImGui.SameLine();
        string copied = state.FormatPosition(zone.Center, null);
        if (Widgets.Button(L($"{Icons.Copy}  Copier le centre"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedTimecycleZone();
            return;
        }

        static string V(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}");
        var map = selection.Map;
        int index = selection.Index;
        TimecycleModifier Current() => map.TimecycleModifiers[index];
        void Live(TimecycleModifier after) => map.SetTimecycleModifier(index, after);
        void Commit(TimecycleModifier after, string description)
        {
            var before = Current();
            if (after == before)
                return;
            Live(after);
            state.History.Add(new TimecycleEdit(map, index, before, after, description));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(TimecycleModifier before, string description)
        {
            if (ImGui.IsItemActivated())
                _zoneEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _zoneEditStart is { } start)
            {
                if (Current() != start)
                    state.History.Add(new TimecycleEdit(map, index, start, Current(), description));
                _zoneEditStart = null;
            }
        }
        string Change(string what) => L($"Modifier la zone de timecycle {index} ({what})");
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Zone"), "timecycle_zone") && Widgets.BeginProperties("##timecycle_zone"))
        {
            Widgets.Property(L("Modificateur"), L("Réglage de lumière, de couleurs ou de brouillard appliqué dans la zone (timecycle_mods_*.xml du jeu). ")
                + L("Tapez son nom (ex. NoAmbientmult) ou son hash (0x...)."));
            if (CommitText("##timecycle_name", TimecycleText(zone.NameHash, names), L("aucun"), out string modifier)
                && ParseTimecycle(modifier, names) is { } modifierHash)
                Commit(Current() with { NameHash = modifierHash }, Change(L("modificateur")));

            Widgets.Property(L("Centre"), L("Centre de la boîte, en mètres. Aussi avec les poignées (touche 2)."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var center = current.Center;
                float value = center[i];
                if (Widgets.AxisField($"##timecycle_center{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    var delta = Vector3.Zero;
                    delta[i] = value - center[i];
                    Live(current with { BoundsMin = current.BoundsMin + delta, BoundsMax = current.BoundsMax + delta });
                }
                Track(current, Change(L("position")));
            }

            Widgets.Property(L("Taille"), L("Taille de la boîte en X, Y, Z (mètres), autour du centre. Aussi avec les poignées (touche 4)."));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var sides = current.Size;
                float value = sides[i];
                if (Widgets.AxisField($"##timecycle_size{i}", ref value, axes[i], width, 0.05f, "%.2f", MinZoneSide, 100000f))
                {
                    var center = current.Center;
                    sides[i] = MathF.Max(MinZoneSide, value);
                    Live(current with { BoundsMin = center - sides / 2, BoundsMax = center + sides / 2 });
                }
                Track(current, Change(L("taille")));
            }

            Widgets.Property(L("Intensité"), L("Force du réglage dans la boîte, de 0 à 100 %."));
            {
                var current = Current();
                float value = current.Percentage;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragFloat("##timecycle_percentage", ref value, 0.5f, 0f, 100f, "%.0f %%"))
                    Live(current with { Percentage = Math.Clamp(value, 0f, 100f) });
                Track(current, Change(L("intensité")));
            }

            Widgets.Property(L("Fondu"), L("Distance autour de la boîte sur laquelle le réglage apparaît peu à peu (cadre jaune pâle dans la vue 3D)."));
            {
                var current = Current();
                float value = current.Range;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragFloat("##timecycle_range", ref value, 0.1f, 0f, 1000f, "%.1f m"))
                    Live(current with { Range = Math.Clamp(value, 0f, 1000f) });
                Track(current, Change(L("fondu")));
            }

            Widgets.Property(L("Heures"), L("Heures du jour où la zone s'applique : de la première à la seconde (0 à 23)."));
            {
                float half = Widgets.SplitWidth(2);
                for (int i = 0; i < 2; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    int value = (int)(i == 0 ? current.StartHour : current.EndHour);
                    ImGui.SetNextItemWidth(half);
                    if (ImGui.DragInt($"##timecycle_hour{i}", ref value, 0.1f, 0, 23, i == 0 ? L("de %d h") : L("à %d h")))
                    {
                        uint hour = (uint)Math.Clamp(value, 0, 23);
                        Live(i == 0 ? current with { StartHour = hour } : current with { EndHour = hour });
                    }
                    Track(current, Change(L("heures")));
                }
            }
            if (!Simple)
            {
                Widgets.PropertyText(L("Coin min"), V(zone.BoundsMin), L("Coin bas, sud-ouest de la boîte, en mètres."));
                Widgets.PropertyText(L("Coin max"), V(zone.BoundsMax), L("Coin haut, nord-est de la boîte, en mètres."));
            }
            Widgets.EndProperties();
        }
        if (!Simple && Widgets.Section(L("Fichier"), "timecycle_file") && Widgets.BeginProperties("##timecycle_file"))
        {
            Widgets.PropertyText(L("YMAP"), file, selection.Map.FilePath);
            Widgets.PropertyText(L("Dans ce YMAP"), L($"{selection.Map.TimecycleModifiers.Count(z => !z.IsDeleted)} zone(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu change l'ambiance de l'image quand la caméra est dans cette zone. Enregistré dans son YMAP au Ctrl+S. Échap pour désélectionner ; ")
            + L("masquez le calque Zones de timecycle pour sélectionner les objets."), wrap: true);
    }

    // Room flags (CMloRoomDef), as named by the community tools.
    private static (uint Flag, string Text, string Tooltip)[] RoomFlags =>
    [
        (0x1, L("Figer les véhicules"), L("Les véhicules de la salle ne bougent plus (freeze vehicles).")),
        (0x2, L("Figer les piétons"), L("Les piétons de la salle ne bougent plus (freeze peds).")),
        (0x4, L("Sans lumière du soleil"), L("Le soleil n'éclaire pas la salle (no directional light).")),
        (0x8, L("Sans lumières extérieures"), L("Les lumières du dehors n'éclairent pas la salle (no exterior lights).")),
        (0x10, L("Forcer le figeage"), L("Force freeze.")),
        (0x20, L("Moins de véhicules"), L("Moins de véhicules générés autour (reduce cars).")),
        (0x40, L("Moins de piétons"), L("Moins de piétons générés autour (reduce peds).")),
        (0x80, L("Forcer la lumière du soleil"), L("Le soleil éclaire la salle même à l'intérieur (force directional light on).")),
        (0x100, L("Ne pas dessiner l'extérieur"), L("Le dehors n'est pas dessiné depuis cette salle, même par les fenêtres (don't render exterior).")),
        (0x200, L("Miroir visible"), L("Un miroir peut être visible depuis cette salle (mirror potentially visible).")),
    ];

    private InteriorRoom? _roomEditStart;
    private readonly Dictionary<string, string> _textBuffers = new();

    // What the audio occlusion section shows for a placed interior: built once, then kept until the interior, its
    // position, its rooms or the language change.
    private sealed record AudioOcclusionView(AudioOcclusionCatalog Catalog, MapEntity Interior, uint Hash, uint FileHash,
        int RoomVersion, Editor.Localization.Language Language, string? Path, string? Error, bool Found, bool OriginalLost, int Links, int Outside,
        int Doors, int Glass, int Paths, IReadOnlyList<(string Text, bool Ok)> Lines, bool Mismatch);

    private AudioOcclusionView? _audioOcclusion;

    private AudioOcclusionView AudioOcclusionOf(AudioOcclusionCatalog catalog, MapEntity interior, Archetype archetype,
        MapLibrary library, HashNames names)
    {
        uint hash = AudioOcclusionHash.Of(interior.ArchetypeHash, interior.Position);
        uint fileHash = AudioOcclusionHash.Of(interior.FileArchetypeHash, interior.FilePosition);
        if (_audioOcclusion is { } kept && kept.Catalog == catalog && kept.Interior == interior && kept.Hash == hash
            && kept.FileHash == fileHash && kept.RoomVersion == library.RoomVersion && kept.Language == Editor.Localization.Loc.Current)
            return kept;

        var (occlusion, error) = catalog.Get(hash);
        var rooms = archetype.InteriorRooms;
        string RoomName(int r) => r == 0 ? L("extérieur") : r < rooms.Count && rooms[r].Name.Length > 0 ? rooms[r].Name : $"{r}";
        var lines = new List<(string Text, bool Ok)>();
        int links = 0, outside = 0, doors = 0, glass = 0;
        foreach (var link in occlusion?.Links ?? [])
        {
            doors += link.Entities.Count(e => e.IsDoor);
            glass += link.Entities.Count(e => e.IsGlass);
            if (link.DestInteriorProxyHash != link.InteriorProxyHash)
                outside++;
            if (link.InteriorProxyHash != hash)
                continue;
            links++;

            // PortalIndex counts the portals touching the room, in the archetype's order.
            InteriorPortal? portal = null;
            if (link.RoomIndex < rooms.Count)
            {
                int rank = 0;
                foreach (var p in archetype.InteriorPortals)
                    if ((p.RoomFrom == link.RoomIndex || p.RoomTo == link.RoomIndex) && rank++ == link.PortalIndex)
                    {
                        portal = p;
                        break;
                    }
            }
            bool ok = portal is not null;
            string destination = L("un autre intérieur");
            if (link.DestInteriorProxyHash == hash)
            {
                destination = RoomName(link.DestRoomIndex);
                ok = portal is not null && (portal.RoomFrom == link.RoomIndex ? portal.RoomTo : portal.RoomFrom) == link.DestRoomIndex;
            }
            var text = new System.Text.StringBuilder($"{RoomName(link.RoomIndex)} → {destination}");
            foreach (var e in link.Entities)
                text.Append($" · {(e.IsDoor ? L("porte") : e.IsGlass ? L("vitre") : L("objet"))} {names.Describe(e.ModelHash)} ({e.MaxOcclusion * 100:0} %)");
            if (!ok)
                text.Append("  ?");
            lines.Add((text.ToString(), ok));
        }
        return _audioOcclusion = new AudioOcclusionView(catalog, interior, hash, fileHash, library.RoomVersion, Editor.Localization.Loc.Current,
            catalog.PathOf(hash), error, occlusion is not null, occlusion is null && fileHash != hash && catalog.PathOf(fileHash) is not null,
            links, outside, doors, glass, occlusion?.Paths.Count ?? 0, lines, lines.Any(l => !l.Ok));
    }

    private void DrawAudioOcclusion(MapEntity interior, Archetype archetype, MapLibrary library, HashNames names)
    {
        Widgets.Hint(L("Fichier qui dit au jeu comment le son passe d'une salle à l'autre (portails, portes, vitres). ")
            + L("Sans lui, les sons traversent les murs de l'intérieur."), wrap: true);
        if (state.World.AudioOcclusion is not { } catalog)
        {
            Widgets.Hint(L("Lecture des fichiers d'occlusion audio..."));
            return;
        }
        var view = AudioOcclusionOf(catalog, interior, archetype, library, names);
        if (Widgets.BeginProperties("##audio_occlusion"))
        {
            Widgets.PropertyText(L("Fichier attendu"), $"{view.Hash}.ymt",
                L("Nom calculé par le jeu à partir du type de l'intérieur et de sa position : il change si l'intérieur est déplacé."));
            if (view.Path is not null)
                FileRow(L("Trouvé dans"), view.Path);
            else
                Widgets.PropertyText(L("Trouvé dans"), L("aucun fichier"), null, UiTheme.Warning);
            if (view.Error is not null)
                Widgets.PropertyText(L("Erreur"), view.Error, view.Error, UiTheme.Error);
            if (view.Found)
            {
                Widgets.PropertyText(L("Passages du son"), $"{view.Links}",
                    L("Un passage par portail et par sens : de quelle salle vers quelle salle le son peut aller."));
                if (view.Outside > 0)
                    Widgets.PropertyText(L("Vers d'autres intérieurs"), $"{view.Outside}");
                Widgets.PropertyText(L("Portes / vitres"), $"{view.Doors} / {view.Glass}",
                    L("Objets des portails qui étouffent le son quand ils sont fermés."));
                Widgets.PropertyText(L("Chemins précalculés"), $"{view.Paths}");
            }
            Widgets.EndProperties();
        }

        if (view.OriginalLost)
            WarningText(L($"Intérieur déplacé : son occlusion audio d'origine ({view.FileHash}.ymt) ne s'applique plus, les sons traversent ses murs. ")
                + L("L'export FiveM en crée une nouvelle à cette position."));
        else if (!view.Found && view.Error is null)
            Widgets.Hint(L("Cet intérieur n'a pas d'occlusion audio à cet endroit : les sons traversent ses murs. ")
                + L("L'export FiveM en crée une pour les intérieurs placés par le projet."), wrap: true);
        if (view.Mismatch)
            WarningText(L("Le fichier ne correspond plus aux salles et portails de l'intérieur (modifiés depuis ?) : marqués « ? » ci-dessous. ")
                + L("S'ils ont été modifiés dans le projet, l'export FiveM refait le fichier."));
        if (view.Lines.Count > 0 && ImGui.TreeNode(L($"Détail des passages ({view.Lines.Count})###audio_links")))
        {
            ImGui.PushTextWrapPos(0);
            foreach (var (text, ok) in view.Lines)
                ImGui.TextColored(ok ? UiTheme.TextMuted : UiTheme.Warning, text);
            ImGui.PopTextWrapPos();
            ImGui.TreePop();
        }
    }

    private string AudioName(uint hash, AudioGameData data, HashNames names) =>
        hash == 0 ? L("aucun") : data.Name(hash) ?? names.Describe(hash);

    private void DrawInteriorAudio(MapEntity interior, Archetype archetype, HashNames names)
    {
        Widgets.Hint(L("Réglages du jeu pour le son dans l'intérieur et dans chaque salle (écho, son de fond, pluie...), ")
            + L("lus dans les fichiers .dat151.rel. Cliquez une salle pour voir les siens."), wrap: true);
        if (state.World.AudioGameData is not { } data)
        {
            Widgets.Hint(L("Lecture des données audio..."));
            return;
        }
        if (data.Interior(interior.ArchetypeHash) is not { } settings)
        {
            WarningText(L("Cet intérieur n'a pas de réglages audio : le jeu le traite comme l'extérieur (pas d'écho de pièce, pluie audible). L'export FiveM lui en crée (ceux de l'intérieur copié s'il y en a un, sinon des réglages neutres)."));
            return;
        }
        if (Widgets.BeginProperties("##interior_audio"))
        {
            FileRow(L("Défini dans"), settings.File);
            Widgets.PropertyText(L("Bruit de foule"), AudioName(settings.WallaSoundSet, data, names), L("Sons de foule (walla) joués dans l'intérieur."));
            Widgets.PropertyText(L("Réflexions"), AudioName(settings.Reflections, data, names));
            Widgets.PropertyText(L("Drapeaux"), $"0x{settings.Flags:X8}");
            Widgets.PropertyText(L("Salles audio"), $"{settings.Rooms.Count}");
            Widgets.EndProperties();
        }
        for (int r = 1; r < archetype.InteriorRooms.Count; r++)
        {
            var room = archetype.InteriorRooms[r];
            int index = r;
            if (Widgets.Link($"{(room.Name.Length > 0 ? room.Name : $"{r}")}##audio_room{r}", L("Sélectionner cette salle")))
            {
                state.SelectRoom(new RoomSelection(interior, index));
                state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
            }
            ImGui.SameLine();
            if (data.RoomOf(interior.ArchetypeHash, room.Name) is { } audio)
                ImGui.TextColored(UiTheme.TextMuted, L($"écho {audio.ReverbSmall:0.##} / {audio.ReverbMedium:0.##} / {audio.ReverbLarge:0.##}"));
            else
                ImGui.TextColored(UiTheme.Warning, L("sans réglage audio"));
        }
    }

    private void DrawRoomAudio(MapEntity interior, InteriorRoom room, HashNames names)
    {
        if (state.World.AudioGameData is not { } data)
        {
            Widgets.Hint(L("Lecture des données audio..."));
            return;
        }
        if (data.RoomOf(interior.ArchetypeHash, room.Name) is not { } audio)
        {
            WarningText(data.Interior(interior.ArchetypeHash) is null
                ? L("L'intérieur n'a pas de réglages audio : l'export FiveM lui en crée.")
                : L("Aucun réglage audio ne porte le nom de cette salle : le jeu lui donne des réglages par défaut. L'export FiveM lui en crée (ceux de la salle de même nom de l'intérieur copié, sinon des réglages neutres)."));
            return;
        }
        if (!Widgets.BeginProperties("##room_audio"))
            return;
        FileRow(L("Défini dans"), audio.File);
        Widgets.PropertyText(L("Nom audio"), AudioName(audio.NameHash, data, names));
        Widgets.PropertyText(L("Écho petit / moyen / grand"), $"{audio.ReverbSmall:0.##} / {audio.ReverbMedium:0.##} / {audio.ReverbLarge:0.##}",
            L("Réverbération de la pièce, de 0 à 1 : petite pièce, pièce moyenne, grand volume."));
        Widgets.PropertyText(L("Son de fond"), AudioName(audio.RoomTone, data, names), L("Bruit de fond de la salle (ventilation, bourdonnement...)."));
        Widgets.PropertyText(L("Zone d'ambiance"), AudioName(audio.AmbientZone, data, names));
        Widgets.PropertyText(L("Sons de l'extérieur"), $"{audio.ExteriorAudibility:0.##}", L("Part des sons de l'extérieur entendus dans la salle, de 0 à 1."));
        Widgets.PropertyText(L("Portails non marqués"), $"{audio.NonMarkedPortalOcclusion:0.##}",
            L("Étouffement du son par les portails sans réglage, de 0 (aucun) à 1 (bloqué)."));
        Widgets.PropertyText(L("Distance d'occlusion"), $"{audio.DistanceFromPortalForOcclusion:0.##} m / {audio.DistanceFromPortalFadeDistance:0.##} m",
            L("Distance au portail à partir de laquelle le son est étouffé, puis longueur du fondu."));
        Widgets.PropertyText(L("Amortissement"), $"{audio.RoomOcclusionDamping:0.##}");
        Widgets.PropertyText(L("Pluie"), audio.RainType switch { 0 => L("normale"), _ => $"{audio.RainType}" }, L("Type de bruit de pluie dans la salle."));
        Widgets.PropertyText(L("Type d'intérieur"), $"{audio.InteriorType}");
        Widgets.PropertyText(L("Acoustique des armes"), AudioName(audio.WeaponMetrics, data, names));
        Widgets.PropertyText(L("Bruit de foule"), AudioName(audio.WallaSoundSet, data, names));
        Widgets.PropertyText(L("Drapeaux"), $"0x{audio.Flags:X8}");
        Widgets.EndProperties();
    }

    private static void WarningText(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(UiTheme.Warning, $"{Icons.TriangleAlert} {text}");
        ImGui.PopTextWrapPos();
    }

    // The rooms of an interior, each one can be selected (and edited) in turn.
    private void DrawRoomList(MapEntity interior, Archetype archetype)
    {
        Widgets.Hint(L("Boîtes qui découpent l'intérieur : le jeu ne dessine une salle que si elle est visible par un portail. ")
            + L("Cliquez une salle pour la modifier."), wrap: true);
        for (int r = 1; r < archetype.InteriorRooms.Count; r++)
        {
            var room = archetype.InteriorRooms[r];
            int index = r;
            if (Widgets.Link(L($"{r} · {(room.Name.Length > 0 ? room.Name : L("(sans nom)"))}##room{r}"), L("Sélectionner cette salle")))
            {
                state.SelectRoom(new RoomSelection(interior, index));
                state.SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
            }
            ImGui.SameLine();
            int objects = archetype.InteriorEntities.Count(e => e.InteriorRoom == index && !e.IsDeleted);
            ImGui.TextColored(UiTheme.TextMuted, objects == 1 ? L("1 objet") : L($"{objects} objets"));
        }
        if (Widgets.Button(L($"{Icons.Box}  Ajouter une salle"), L("Boîte de 4 x 4 x 3 m devant la caméra, dans toutes les copies de l'intérieur.")))
            state.AddRoom(interior);
    }

    // The portal flattened onto the nearest wall of one of its rooms (limbo has none); null without a room.
    private static InteriorPortal? SnapToWall(Archetype archetype, InteriorPortal portal)
    {
        if (portal.Corners.Count == 0)
            return null;
        var center = portal.Corners.Aggregate(Vector3.Zero, (a, c) => a + c) / portal.Corners.Count;
        (int Axis, float Wall, float Distance)? best = null;
        foreach (int r in new[] { portal.RoomFrom, portal.RoomTo })
        {
            if (r <= 0 || r >= archetype.InteriorRooms.Count)
                continue;
            var room = archetype.InteriorRooms[r];
            for (int axis = 0; axis < 3; axis++)
                foreach (float wall in new[] { room.BoundsMin[axis], room.BoundsMax[axis] })
                {
                    float distance = MathF.Abs(center[axis] - wall);
                    if (best is null || distance < best.Value.Distance)
                        best = (axis, wall, distance);
                }
        }
        if (best is not { } b)
            return null;
        return portal with { Corners = [.. portal.Corners.Select(c => { c[b.Axis] = b.Wall; return c; })] };
    }

    private string? _intEditId;
    private int _intEditValue;

    // InputInt rejects EnterReturnsTrue: the +/- buttons apply at once, typed values when the field is released.
    private bool CommitInt(string id, ref int value, int step, int stepFast)
    {
        int v = value;
        bool changed = ImGui.InputInt(id, ref v, step, stepFast);
        bool active = ImGui.IsItemActive();
        if (changed && active)
        {
            _intEditId = id;
            _intEditValue = v;
            return false;
        }
        if (changed)
        {
            value = v;
            return true;
        }
        if (ImGui.IsItemDeactivatedAfterEdit() && _intEditId == id)
        {
            _intEditId = null;
            value = _intEditValue;
            return true;
        }
        return false;
    }

    // A text field whose value is applied when it is released (Enter or click elsewhere); true with the typed text then.
    private bool CommitText(string id, string current, string hint, out string committed)
    {
        if (!_textBuffers.TryGetValue(id, out var buffer))
            buffer = current;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint(id, hint, ref buffer, 96);
        if (ImGui.IsItemActive())
            _textBuffers[id] = buffer;
        else
            _textBuffers.Remove(id);
        committed = buffer.Trim();
        return ImGui.IsItemDeactivatedAfterEdit();
    }

    private void DrawRoom(RoomSelection selected, Archetype archetype, MapLibrary library)
    {
        var names = state.World.Names ?? new HashNames();
        var interior = selected.Interior;
        int index = selected.Index;
        var room = archetype.InteriorRooms[index];
        var center = Vector3.Transform((room.BoundsMin + room.BoundsMax) / 2, interior.Transform);

        void Change(InteriorRoom after, string description)
        {
            if (after != archetype.InteriorRooms[index])
                state.History.Add(RoomEdit.Apply(library, interior, index, after, description));
        }

        // Live change while a field is dragged; one undo step when it is released.
        void Track(InteriorRoom before, string description)
        {
            if (ImGui.IsItemActivated())
                _roomEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _roomEditStart is { } start)
            {
                var end = archetype.InteriorRooms[index];
                if (end != start)
                    state.History.Add(new RoomEdit(library, interior, index, start, end, description));
                _roomEditStart = null;
            }
        }

        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.House}  Salle {index} : {(room.Name.Length > 0 ? room.Name : L("(sans nom)"))}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {names.Describe(interior.ArchetypeHash)} · {Path.GetFileName(library.GetArchetypeFile(interior.ArchetypeHash) ?? "")}");
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la salle")))
            state.FocusOn(center, MathF.Max((room.BoundsMax - room.BoundsMin).Length() / 2, 4f));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.House}  Sélectionner l'intérieur")))
        {
            state.Select(interior);
            return;
        }

        if (index == 0)
        {
            Widgets.Hint(L("Salle 0 (limbo) : elle représente l'extérieur de l'intérieur, elle n'a pas de boîte à modifier."), wrap: true);
            return;
        }

        if (Widgets.Section(L("Salle"), "room") && Widgets.BeginProperties("##room"))
        {
            Widgets.Property(L("Nom"), L("Nom de la salle dans le .ytyp (lettres, chiffres, _ et -). Entrée pour valider."));
            if (CommitText($"##room_name{interior.ArchetypeHash}_{index}", room.Name, L("nom de la salle"), out var name) && name != room.Name)
            {
                string clean = new(name.Replace(' ', '_').Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
                if (clean.Length == 0)
                    state.Notify(L("Nom de salle vide ou sans lettre valide : non changé."), warning: true);
                else
                    Change(room with { Name = clean }, L($"Renommer la salle {index}"));
            }

            Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
            float width = Widgets.SplitWidth(3);
            Widgets.Property(L("Coin min"), L("Coin bas de la boîte de la salle, en mètres, dans le repère de l'intérieur (X, Y, Z)."));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = archetype.InteriorRooms[index];
                var min = current.BoundsMin;
                float value = min[i];
                if (Widgets.AxisField($"##room_min{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    min[i] = MathF.Min(value, current.BoundsMax[i] - 0.1f);
                    library.SetRoom(interior.ArchetypeHash, index, current with { BoundsMin = min });
                }
                Track(current, L($"Redimensionner la salle {index}"));
            }
            Widgets.Property(L("Coin max"), L("Coin haut de la boîte de la salle, en mètres, dans le repère de l'intérieur (X, Y, Z)."));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = archetype.InteriorRooms[index];
                var max = current.BoundsMax;
                float value = max[i];
                if (Widgets.AxisField($"##room_max{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    max[i] = MathF.Max(value, current.BoundsMin[i] + 0.1f);
                    library.SetRoom(interior.ArchetypeHash, index, current with { BoundsMax = max });
                }
                Track(current, L($"Redimensionner la salle {index}"));
            }
            room = archetype.InteriorRooms[index];
            var size = room.BoundsMax - room.BoundsMin;
            Widgets.PropertyText(L("Taille"), $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m");

            Widgets.Property(L("Timecycle"), L("Réglage de lumière et de couleurs de la salle (timecycle modifier), par son nom (ex. int_gasstation). ")
                + L("Vide = aucun. Entrée pour valider."));
            if (CommitText($"##room_tc{interior.ArchetypeHash}_{index}", TimecycleText(room.TimecycleHash, names), "aucun", out var timecycle)
                && ParseTimecycle(timecycle, names) is { } tcHash && tcHash != room.TimecycleHash)
                Change(room with { TimecycleHash = tcHash }, L($"Timecycle de la salle {index}"));
            Widgets.Property(L("Timecycle secondaire"), L("Deuxième réglage de lumière, mélangé au premier. Vide = aucun."));
            if (CommitText($"##room_tc2{interior.ArchetypeHash}_{index}", TimecycleText(room.SecondaryTimecycleHash, names), "aucun", out var secondary)
                && ParseTimecycle(secondary, names) is { } tc2Hash && tc2Hash != room.SecondaryTimecycleHash)
                Change(room with { SecondaryTimecycleHash = tc2Hash }, L($"Timecycle secondaire de la salle {index}"));

            Widgets.Property(L("Étage"), L("Numéro d'étage de la salle (floor id), 0 le plus souvent. Entrée pour valider."));
            {
                int floor = room.FloorId;
                ImGui.SetNextItemWidth(-1);
                if (CommitInt("##room_floor", ref floor, 1, 1))
                    Change(room with { FloorId = floor }, L($"Étage de la salle {index}"));
            }
            Widgets.Property(L("Profondeur de l'extérieur"), L("Nombre de portails à traverser au plus pour voir l'extérieur depuis cette salle ")
                + L("(exterior visibility depth) ; -1 = réglage du jeu. Entrée pour valider."));
            {
                int depth = room.ExteriorVisibilityDepth;
                ImGui.SetNextItemWidth(-1);
                if (CommitInt("##room_depth", ref depth, 1, 1))
                    Change(room with { ExteriorVisibilityDepth = Math.Max(-1, depth) }, L($"Profondeur de l'extérieur de la salle {index}"));
            }

            room = archetype.InteriorRooms[index];
            int portals = archetype.InteriorPortals.Count(p => p.RoomFrom == index || p.RoomTo == index);
            int objects = archetype.InteriorEntities.Count(e => e.InteriorRoom == index && !e.IsDeleted);
            Widgets.PropertyText(L("Contenu"), L($"{portals} portail(s), {objects} objet(s)"));
            Widgets.EndProperties();
        }

        if (!Simple && Widgets.Section(L("Réglages de la salle"), "room_flags"))
        {
            foreach (var (flag, text, tooltip) in RoomFlags)
            {
                bool on = (room.Flags & flag) != 0;
                if (ImGui.Checkbox($"{text}##room_flag{flag:X}", ref on))
                    Change(room with { Flags = on ? room.Flags | flag : room.Flags & ~flag }, L($"{(on ? L("Activer") : L("Désactiver"))} « {text} » (salle {index})"));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(tooltip);
            }
            uint other = room.Flags & ~RoomFlags.Aggregate(0u, (all, f) => all | f.Flag);
            if (other != 0)
                Widgets.Hint(L($"Autres drapeaux : 0x{other:X}"));
        }
        if (!Simple && index > 0 && Widgets.Section(L("Audio de la salle"), "room_audio", defaultOpen: false))
            DrawRoomAudio(interior, room, names);
        if (Widgets.Section(L("Portails de la salle"), "room_portals"))
            DrawPortalList(interior, archetype, index);
        int roomPortals = archetype.InteriorPortals.Count(p => p.RoomFrom == index || p.RoomTo == index);
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer la salle"), L("Supprime la salle") + (roomPortals > 0 ? L($" et ses {roomPortals} portail(s)") : "")
                + L(" ; ses objets vont dans la salle qui les entoure (sinon dans limbo). Suppr fait de même.")))
        {
            state.DeleteSelectedRoom();
            return;
        }
        Widgets.Hint(L("Change la salle dans toutes les copies de l'intérieur ; enregistré dans son .ytyp au Ctrl+S. ")
            + L("Échap pour désélectionner ; masquez le calque Salles pour sélectionner les objets."), wrap: true);
    }

    private static string TimecycleText(uint hash, HashNames names) =>
        hash == 0 ? "" : names.TryGet(hash) ?? $"0x{hash:X8}";

    // A typed timecycle name (or hash, 0x...): its hash, 0 for none; null when the text is not valid.
    private uint? ParseTimecycle(string text, HashNames names)
    {
        text = text.Trim().Trim('"', '\'');
        if (text.Length == 0)
            return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex))
            return hex;
        if (text.Any(char.IsWhiteSpace))
        {
            state.Notify(L("Nom de timecycle invalide (espaces)."), warning: true);
            return null;
        }
        names.Add(text);
        return Joaat.Hash(text);
    }

    // Portal flags (CMloPortalDef), as named by the community tools.
    private static (uint Flag, string Text, string Tooltip)[] PortalFlags =>
    [
        (0x1, L("Sens unique"), L("On ne voit à travers le portail que dans un sens (one way).")),
        (0x2, L("Relie un autre intérieur"), L("Le portail mène à un autre intérieur collé à celui-ci (link interior).")),
        (0x4, L("Miroir"), L("Le portail est un miroir : il montre la salle en reflet (mirror).")),
        (0x8, L("Ignorer le timecycle"), L("Le réglage de lumière de la salle n'est pas appliqué à travers le portail (ignore modifier).")),
        (0x10, L("Miroir haute qualité"), L("Miroir dessiné avec les effets coûteux (mirror using expensive shaders).")),
        (0x20, L("Basse qualité seulement"), L("Seulement pour les modèles de basse qualité (low LOD only).")),
        (0x40, L("Se ferme avec la porte"), L("Quand la porte attachée est fermée, ce qui est derrière n'est plus dessiné (allow closing).")),
        (0x80, L("Miroir : voit le soleil"), L("Le reflet est éclairé par le soleil (mirror can see directional).")),
        (0x100, L("Miroir : traverse les portails"), L("Le reflet montre aussi les autres salles (mirror using portal traversal).")),
        (0x200, L("Miroir au sol"), L("Miroir posé à plat, comme un sol brillant (mirror floor).")),
        (0x400, L("Miroir : voit l'extérieur"), L("Le reflet montre aussi le dehors (mirror can see exterior view).")),
        (0x800, L("Surface d'eau"), L("Le portail est une surface d'eau, comme une piscine (water surface).")),
        (0x1000, L("Eau jusqu'à l'horizon"), L("La surface d'eau s'étend jusqu'à l'horizon (water surface extend to horizon).")),
        (0x2000, L("Lumière qui passe"), L("La lumière d'une salle passe par le portail dans l'autre (use light bleed).")),
    ];

    private InteriorPortal? _portalEditStart;

    private static string RoomLabel(Archetype archetype, int room) =>
        room == 0 ? L("0 · extérieur (limbo)")
        : room < archetype.InteriorRooms.Count ? $"{room} · {(archetype.InteriorRooms[room].Name.Length > 0 ? archetype.InteriorRooms[room].Name : "(sans nom)")}"
        : L($"{room} · (salle absente)");

    // The portals of a room, each one can be selected in turn.
    private void DrawPortalList(MapEntity interior, Archetype archetype, int room)
    {
        bool any = false;
        for (int p = 0; p < archetype.InteriorPortals.Count; p++)
        {
            var portal = archetype.InteriorPortals[p];
            if (portal.RoomFrom != room && portal.RoomTo != room)
                continue;
            any = true;
            int other = portal.RoomFrom == room ? portal.RoomTo : portal.RoomFrom;
            if (Widgets.Link(L($"Portail {p} · vers {RoomLabel(archetype, other)}##portal{p}"), L("Sélectionner ce portail")))
            {
                state.SelectPortal(new RoomSelection(interior, p));
                state.SetLayerVisible(RoomLayer.LayerName(other == 0 ? RoomLayerPart.OutsidePortals : RoomLayerPart.InnerPortals), true);
            }
        }
        if (!any)
            Widgets.Hint(L("Aucun portail : la salle n'est visible depuis nulle part."));
        if (Widgets.Button(L($"{Icons.Box}  Ajouter un portail"), L("Ouverture de 1,2 x 2,2 m en bas du côté de la salle tourné vers la caméra, ")
                + L("vers l'extérieur. À placer ensuite sur la porte ou la fenêtre.")))
            state.AddPortal(interior, room);
    }

    private void DrawPortal(RoomSelection selected, Archetype archetype, MapLibrary library)
    {
        var names = state.World.Names ?? new HashNames();
        var interior = selected.Interior;
        int index = selected.Index;
        var portal = archetype.InteriorPortals[index];

        void Change(InteriorPortal after, string description)
        {
            if (!after.SameAs(archetype.InteriorPortals[index]))
                state.History.Add(PortalEdit.Apply(library, interior, index, after, description));
        }

        // Live change while a field is dragged; one undo step when it is released.
        void Track(InteriorPortal before, string description)
        {
            if (ImGui.IsItemActivated())
                _portalEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _portalEditStart is { } start)
            {
                var end = archetype.InteriorPortals[index];
                if (!end.SameAs(start))
                    state.History.Add(new PortalEdit(library, interior, index, start, end, description));
                _portalEditStart = null;
            }
        }

        var center = portal.Corners.Count > 0 ? portal.Corners.Aggregate(Vector3.Zero, (sum, c) => sum + c) / portal.Corners.Count : Vector3.Zero;
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.House}  Portail {index}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {names.Describe(interior.ArchetypeHash)} · {Path.GetFileName(library.GetArchetypeFile(interior.ArchetypeHash) ?? "")}");
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller au portail")))
        {
            float radius = portal.Corners.Count > 0 ? portal.Corners.Max(c => Vector3.Distance(c, center)) : 2f;
            state.FocusOn(Vector3.Transform(center, interior.Transform), MathF.Max(radius, 3f));
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.House}  Sélectionner l'intérieur")))
        {
            state.Select(interior);
            return;
        }

        if (Widgets.Section(L("Portail"), "portal") && Widgets.BeginProperties("##portal"))
        {
            // The two rooms it joins (room 0 = the outside).
            void RoomCombo(string label, string tooltip, string id, int current, Func<int, InteriorPortal> with, string description)
            {
                Widgets.Property(label, tooltip);
                ImGui.SetNextItemWidth(-1);
                if (!ImGui.BeginCombo(id, RoomLabel(archetype, current)))
                    return;
                for (int r = 0; r < archetype.InteriorRooms.Count; r++)
                {
                    if (ImGui.Selectable(RoomLabel(archetype, r), r == current) && r != current)
                        Change(with(r), description);
                }
                ImGui.EndCombo();
            }
            RoomCombo(L("De la salle"), L("Première salle reliée par le portail (0 = l'extérieur)."), "##portal_from", portal.RoomFrom,
                r => archetype.InteriorPortals[index] with { RoomFrom = r }, L($"Salle de départ du portail {index}"));
            RoomCombo(L("Vers la salle"), L("Deuxième salle reliée par le portail (0 = l'extérieur)."), "##portal_to", portal.RoomTo,
                r => archetype.InteriorPortals[index] with { RoomTo = r }, L($"Salle d'arrivée du portail {index}"));
            Widgets.Property("");
            if (Widgets.Button(L($"{Icons.RefreshCw}  Inverser les salles"), L("Échange « De la salle » et « Vers la salle ».")))
                Change(portal with { RoomFrom = portal.RoomTo, RoomTo = portal.RoomFrom }, L($"Inverser les salles du portail {index}"));
            Widgets.Property("");
            if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Recoller au mur"), L("Pose le portail sur le mur le plus proche d'une de ses salles, sans changer sa taille.")))
            {
                if (SnapToWall(archetype, portal) is { } snapped)
                    Change(snapped, L($"Recoller au mur le portail {index}"));
                else
                    state.Notify(L("Le portail ne touche aucune salle (seulement l'extérieur)."));
            }
            portal = archetype.InteriorPortals[index];
            if (portal.RoomFrom == portal.RoomTo)
                Widgets.Hint(L("Le portail relie une salle à elle-même : le jeu ne s'en sert pas."), wrap: true);

            Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
            float width = Widgets.SplitWidth(3);
            Widgets.Property(L("Centre"), L("Déplace tout le portail (ses coins ensemble), en mètres, dans le repère de l'intérieur."));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = archetype.InteriorPortals[index];
                var middle = current.Corners.Count > 0 ? current.Corners.Aggregate(Vector3.Zero, (sum, c) => sum + c) / current.Corners.Count : Vector3.Zero;
                float value = middle[i];
                if (Widgets.AxisField($"##portal_center{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    var delta = Vector3.Zero;
                    delta[i] = value - middle[i];
                    library.SetPortal(interior.ArchetypeHash, index, current with { Corners = [.. current.Corners.Select(c => c + delta)] });
                }
                Track(current, L($"Déplacer le portail {index}"));
            }
            for (int c = 0; c < portal.Corners.Count; c++)
            {
                Widgets.Property(L($"Coin {c + 1}"), L("Coin du portail, en mètres, dans le repère de l'intérieur (numéroté sur la vue 3D). ")
                    + L("Les coins font le tour de l'ouverture, dans l'ordre."));
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = archetype.InteriorPortals[index];
                    var corner = current.Corners[c];
                    float value = corner[i];
                    if (Widgets.AxisField($"##portal_corner{c}_{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                    {
                        corner[i] = value;
                        int which = c;
                        library.SetPortal(interior.ArchetypeHash, index, current with { Corners = [.. current.Corners.Select((p, k) => k == which ? corner : p)] });
                    }
                    Track(current, L($"Déplacer un coin du portail {index}"));
                }
            }
            portal = archetype.InteriorPortals[index];
            if (portal.Corners.Count == 4)
                Widgets.PropertyText(L("Taille"), $"{Vector3.Distance(portal.Corners[0], portal.Corners[1]):0.00} x {Vector3.Distance(portal.Corners[1], portal.Corners[2]):0.00} m");

            Widgets.Property(L("Opacité"), L("Opacité du portail (0 le plus souvent ; utilisé par les miroirs). Entrée pour valider."));
            {
                int opacity = portal.Opacity;
                ImGui.SetNextItemWidth(-1);
                if (CommitInt("##portal_opacity", ref opacity, 1, 10))
                    Change(portal with { Opacity = Math.Max(0, opacity) }, L($"Opacité du portail {index}"));
            }
            Widgets.Property(L("Priorité du miroir"), L("Ordre de dessin quand plusieurs miroirs sont visibles (0 le plus souvent). Entrée pour valider."));
            {
                int priority = portal.MirrorPriority;
                ImGui.SetNextItemWidth(-1);
                if (CommitInt("##portal_mirror", ref priority, 1, 1))
                    Change(portal with { MirrorPriority = Math.Max(0, priority) }, L($"Priorité du miroir du portail {index}"));
            }
            Widgets.Property(L("Occlusion audio"), L("Réglage du son à travers le portail (audio occlusion), par son nom ou son hash (0x...). ")
                + L("Vide = aucun. Entrée pour valider."));
            if (CommitText($"##portal_audio{interior.ArchetypeHash}_{index}", TimecycleText(portal.AudioOcclusionHash, names), "aucun", out var audio)
                && ParseTimecycle(audio, names) is { } audioHash && audioHash != portal.AudioOcclusionHash)
                Change(portal with { AudioOcclusionHash = audioHash }, L($"Occlusion audio du portail {index}"));

            int objects = archetype.InteriorEntities.Count(e => e.InteriorPortal == index && !e.IsDeleted);
            Widgets.PropertyText(L("Objets attachés"), objects == 0 ? L("aucun") : L($"{objects} (portes...)"));
            Widgets.EndProperties();
        }

        if (!Simple && Widgets.Section(L("Réglages du portail"), "portal_flags"))
        {
            foreach (var (flag, text, tooltip) in PortalFlags)
            {
                bool on = (portal.Flags & flag) != 0;
                if (ImGui.Checkbox($"{text}##portal_flag{flag:X}", ref on))
                    Change(portal with { Flags = on ? portal.Flags | flag : portal.Flags & ~flag }, L($"{(on ? L("Activer") : L("Désactiver"))} « {text} » (portail {index})"));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(tooltip);
            }
            uint other = portal.Flags & ~PortalFlags.Aggregate(0u, (all, f) => all | f.Flag);
            if (other != 0)
                Widgets.Hint(L($"Autres drapeaux : 0x{other:X}"));
        }
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer le portail"), L("Les objets attachés (portes...) restent, sans portail. Suppr fait de même.")))
        {
            state.DeleteSelectedPortal();
            return;
        }
        Widgets.Hint(L("Change le portail dans toutes les copies de l'intérieur ; enregistré dans son .ytyp au Ctrl+S. ")
            + L("Échap pour désélectionner ; masquez les calques des portails pour sélectionner les objets."), wrap: true);
    }

    private void DrawPathNode(PathNode node, PathNetwork network)
    {
        var names = state.World.Names ?? new HashNames();
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Route}  Nœud {node.Id.AreaId}:{node.Id.NodeId}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  nodes{node.Id.AreaId}.ynd");
        var p = node.Position;
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), state.FormatPosition(p)))
            ImGui.SetClipboardText(state.FormatPosition(p));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Supprime aussi ses liens ; les nœuds suivants du fichier sont renumérotés. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedPathNode();
            return;
        }
        if (Widgets.Button(L($"{Icons.Waypoints}  Ajouter un nœud relié"), L("Nouveau nœud à 10 m, relié à celui-ci (mêmes réglages), à placer ensuite avec les poignées.")))
        {
            state.AddPathNode(node);
            return;
        }
        ImGui.SameLine();
        bool linking = state.PathLinkSource == node;
        if (Widgets.Button(linking ? L($"{Icons.GitBranch}  Cliquez sur l'autre nœud...") : L($"{Icons.GitBranch}  Relier à un autre nœud"),
                L("Puis cliquez sur un autre nœud dans la vue 3D : ils sont reliés dans les deux sens. Échap pour annuler.")))
            state.PathLinkSource = linking ? null : node;

        if (Widgets.Section(L("Nœud"), "path_node") && Widgets.BeginProperties("##path_node"))
        {
            DrawPathNodePosition(node, network);
            Widgets.PropertyText(L("Catégorie"), PathNodeLayer.CategoryOf(node));
            if (node.StreetNameHash != 0)
                Widgets.PropertyText(L("Rue"), names.Describe(node.StreetNameHash));
            if (node.IsJunction)
                Widgets.PropertyText(L("Carrefour"), L("oui"), L("Les données de carrefour du jeu restent les mêmes."));
            DrawPathNodeValues(node, network);
            Widgets.EndProperties();
            Widgets.Hint(L("Enregistré dans le .ynd du projet au Ctrl+S, ajouté à l'export FiveM."), wrap: true);
        }

        var area = network.GetArea(node.Id.AreaId);
        var links = area?.LinksOf(node).ToList() ?? [];
        if (Widgets.Section(L($"Liens ({links.Count})"), "path_links") && links.Count > 0 && Widgets.BeginProperties("##path_links"))
        {
            foreach (var (link, i) in links.Select((l, i) => (l, i)))
            {
                var target = network.GetNode(link.Target);
                Widgets.Property(L($"Vers {link.Target.AreaId}:{link.Target.NodeId}"));
                if (target is not null && Widgets.IconButton(Icons.Trash, $"##delete_link{i}", L("Supprimer ce lien (dans les deux sens). Annulable avec Ctrl+Z.")))
                {
                    var edit = PathLinkDeleteEdit.Apply(network, node, target, L($"Supprimer le lien {node.Id.AreaId}:{node.Id.NodeId} - {target.Id.AreaId}:{target.Id.NodeId}"));
                    if (edit is not null)
                        state.History.Add(edit);
                    break;
                }
                if (target is not null)
                    ImGui.SameLine();
                string lanes = L($"{link.LanesForward} voie(s) aller, {link.LanesBackward} retour, {network.LinkLength(node, link)} m");
                if (link.IsShortcut) lanes += L(", raccourci");
                if (link.IsNarrowRoad) lanes += L(", étroite");
                if (link.DontUseForNavigation) lanes += L(", hors navigation");
                if (target is not null && Widgets.Link($"{lanes}##link{i}", L("Sélectionner ce nœud")))
                    state.SelectedPathNode = target;
                else if (target is null)
                    ImGui.TextColored(UiTheme.TextFaint, L($"{lanes} (zone non chargée)"));
                if (target is not null && DrawLinkLanes(node, target, link, i, network))
                    break;
            }
            Widgets.EndProperties();
        }
    }

    private static readonly string[] LaneCounts = ["0", "1", "2", "3", "4", "5", "6", "7"];

    // Lanes of a link, towards its target and back; the link back gets them swapped. True when they changed (the
    // links of the node were rebuilt).
    private bool DrawLinkLanes(PathNode node, PathNode target, PathLink link, int index, PathNetwork network)
    {
        Widgets.Property(L("   Voies aller / retour"), L("Nombre de voies de ce nœud vers l'autre (aller) et de l'autre vers celui-ci (retour), 0 à 7.\nLe lien dans l'autre sens est réglé pareil (inversé). Annulable avec Ctrl+Z."));
        int forward = link.LanesForward, backward = link.LanesBackward;
        float width = Widgets.SplitWidth(2);
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.Combo($"##lanes_forward{index}", ref forward, LaneCounts, LaneCounts.Length);
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.Combo($"##lanes_backward{index}", ref backward, LaneCounts, LaneCounts.Length);
        if (!changed)
            return false;
        var edit = PathLinkLanesEdit.Apply(network, node, target, forward, backward,
            L($"Voies du lien {node.Id.AreaId}:{node.Id.NodeId} - {target.Id.AreaId}:{target.Id.NodeId}"));
        if (edit is null)
            return false;
        state.History.Add(edit);
        return true;
    }

    private int? _densityDraft;

    // Speed, density, special type and flags of a node; one undo step per change (the density when the slider is
    // released, the network being rebuilt on every change).
    private void DrawPathNodeValues(PathNode node, PathNetwork network)
    {
        string change = L($"Modifier le nœud {node.Id.AreaId}:{node.Id.NodeId}");
        var values = node.Values;
        void Commit(PathNodeValues after)
        {
            if (after == values)
                return;
            network.SetValues(node, after);
            state.History.Add(new PathNodeValuesEdit(network, node, values, after, change));
        }

        Widgets.Property(L("Vitesse"), L("Vitesse des véhicules sur cette portion de route."));
        string[] speeds = [L("lente"), L("normale"), L("rapide"), L("très rapide")];
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##node_speed", speeds[values.Speed]))
        {
            for (int i = 0; i < speeds.Length; i++)
                if (ImGui.Selectable(speeds[i], i == values.Speed))
                    Commit(values.WithSpeed(i));
            ImGui.EndCombo();
        }

        Widgets.Property(L("Densité de trafic"), L("Quantité de véhicules créés ici : 0 = aucun, 15 = le plus."));
        int density = _densityDraft ?? values.Density;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##node_density", ref density, 0, 15, "%d / 15"))
            _densityDraft = Math.Clamp(density, 0, 15);
        if (ImGui.IsItemDeactivated())
        {
            if (_densityDraft is { } chosen)
                Commit(values.WithDensity(chosen));
            _densityDraft = null;
        }

        Widgets.Property(L("Type spécial"), L("Rôle particulier du nœud : place de parking, passage piéton, arrêt au feu..."));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##node_special", SpecialLabel(values.Special), ImGuiComboFlags.HeightLarge))
        {
            foreach (var type in Enum.GetValues<PathNodeSpecialType>())
                if (ImGui.Selectable(SpecialLabel(type), type == values.Special))
                    Commit(values.WithSpecial(type));
            ImGui.EndCombo();
        }

        foreach (var (flag, label, tooltip) in NodeFlags)
        {
            Widgets.Property(label, tooltip);
            bool on = values.Has(flag);
            if (ImGui.Checkbox($"##node_flag{(int)flag}", ref on))
                Commit(values.With(flag, on));
        }

        if (Simple)
            return;
        Widgets.Property(L("Octets bruts"), L("Flags 0 à 4 et 3 bits bas de l'octet de liens, tels que stockés dans le fichier .ynd, en hexadécimal."));
        string raw = $"{values.Flags0:X2} {values.Flags1:X2} {values.Flags2:X2} {values.Flags3:X2} {values.Flags4:X2} {values.LinkCountExtra:X2}";
        if (CommitText("##node_raw", raw, "", out string typed) && typed != raw)
        {
            var parts = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[6];
            bool valid = parts.Length == 6;
            for (int i = 0; valid && i < 6; i++)
                valid = byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]);
            if (valid && bytes[5] < 8)
                Commit(new PathNodeValues(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]));
            else
                state.Notify(L("Octets invalides : 6 nombres hexadécimaux attendus (ex. 02 00 40 00 08 02), le dernier de 00 à 07."), warning: true);
        }
    }

    private static (PathNodeFlag Flag, string Label, string Tooltip)[] NodeFlags =>
    [
        (PathNodeFlag.Highway, L("Autoroute"), L("Route marquée autoroute (freeway) par le jeu.")),
        (PathNodeFlag.Tunnel, L("Tunnel"), L("Portion de route dans un tunnel.")),
        (PathNodeFlag.OffRoad, L("Hors-piste"), L("Chemin de terre ou hors route.")),
        (PathNodeFlag.SlipRoad, L("Bretelle"), L("Voie d'entrée ou de sortie (slip lane).")),
        (PathNodeFlag.NoBigVehicles, L("Pas de gros véhicules"), L("Camions et bus évitent ce nœud.")),
        (PathNodeFlag.NoGps, L("Ignoré par le GPS"), L("Le GPS ne fait pas passer d'itinéraire par ce nœud.")),
        (PathNodeFlag.SwitchedOff, L("Sans trafic par défaut"), L("Route coupée : le jeu n'y crée pas de trafic, sauf si un script la rouvre.")),
    ];

    private static string SpecialLabel(PathNodeSpecialType type) => type switch
    {
        PathNodeSpecialType.None => L("aucun"),
        PathNodeSpecialType.ParkingSpace => L("place de parking"),
        PathNodeSpecialType.PedCrossing => L("passage piéton"),
        PathNodeSpecialType.PedAssistedMovement => L("déplacement piéton guidé"),
        PathNodeSpecialType.TrafficLightStop => L("arrêt au feu"),
        PathNodeSpecialType.StopSign => "stop",
        PathNodeSpecialType.Caution => L("prudence"),
        PathNodeSpecialType.PedCrossingNoWait => L("passage piéton sans attente"),
        PathNodeSpecialType.EmergencyVehiclesOnly => L("véhicules d'urgence seulement"),
        PathNodeSpecialType.OffRoadJunction => L("carrefour hors-piste"),
        _ => $"{(int)type}",
    };
}
