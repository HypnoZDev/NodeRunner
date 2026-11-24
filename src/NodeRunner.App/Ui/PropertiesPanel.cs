using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
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

    // Drawn instead of the map's properties while the model workshop is shown (its tool's settings).
    public Action? WorkshopContent { get; set; }
    public Action? PedContent { get; set; }
    public Action? RecordingContent { get; set; }

    public void Draw(ref bool visible)
    {
        StopZoneSoundIfDeselected();
        if (!ImGui.Begin(L(Title), ref visible))
        {
            ImGui.End();
            return;
        }
        if (state.WorkshopShown && WorkshopContent is { } workshop)
        {
            workshop();
            ImGui.End();
            return;
        }
        if (state.PedShown && PedContent is { } ped)
        {
            ped();
            ImGui.End();
            return;
        }
        if (state.RecordingOpen && state.Recordings.Selected is not null && RecordingContent is { } recording)
        {
            recording();
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
        if (state.IsClassic)
        {
            DrawClassic();
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
            if (state.OpeningTool is { } openingTool && openingTool.Building != entity)
                state.OpeningTool = null;
            if (!entity.IsInterior && entity.Interior is null && !Doors.IsDoor(names.TryGet(entity.ArchetypeHash))
                && Widgets.Section(L("Intérieur derrière une porte"), "building_opening", defaultOpen: state.OpeningTool is not null))
                DrawBuildingOpening(entity, library, names);
            if (!Simple && Widgets.Section(L("Morceaux du modèle"), "model_parts"))
                DrawModelParts(entity, library);
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
                if (Widgets.Section(L("Santé de l'intérieur"), "interior_health", defaultOpen: false))
                    DrawInteriorHealth(placed, interiorType, library, names);
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
        else if (state.SelectedAudio is { } audio)
            DrawAudioPlacement(audio, state.World.Names ?? new HashNames());
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
            ImGui.Dummy(new Vector2(0, 4));
            float width = ImGui.GetContentRegionAvail().X;
            if (Search is { } search && Widgets.Button(L($"{Icons.Search}  Rechercher un objet ou un lieu"), "Ctrl+K", width))
                search();
            if (state.Features.Editing && Widgets.Button(L($"{Icons.LibraryBig}  Poser un prop de la bibliothèque"),
                    L("Glissez un prop de la Bibliothèque dans la vue 3D."), width))
                ShowLibrary?.Invoke();
        }
        ImGui.End();
    }

    private bool Simple => state.Settings.SimpleProperties;

    // Shortcuts offered when nothing is selected.
    public Action? Search { get; set; }
    public Action? ShowLibrary { get; set; }

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

    // Changing the object's model (removing a logo, moving a piece, a texture...) is done in the model workshop.
    private void DrawModelParts(MapEntity entity, MapLibrary library)
    {
        float width = ImGui.GetContentRegionAvail().X;
        if (Widgets.Button(L($"{Icons.Box}  Ouvrir dans l'atelier de modèle"),
                L("Le modèle seul, en grand, dans un onglet Atelier à côté de la carte (aussi : double-clic sur l'objet) : ")
                + L("retirer, déplacer ou ajouter des morceaux, changer ses textures."), width))
            state.WorkshopRequest = entity;
        if (library.GetArchetype(entity.ArchetypeHash) is { } type && state.World.Models?.Locate(type) is { } location
            && ModelPartTool.ChangedFile(state, location.Path) is not null
            && Widgets.Button(L($"{Icons.Undo}  Remettre le modèle d'origine"),
                L("Annule tous les changements de ce modèle (le fichier d'avant est remis)."), width))
            state.Notify(ModelPartTool.Restore(state, location.Path));
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
            {
                RoomChoice(entity, interior, mlo);
                PortalChoice(entity, mlo);
            }
            else
            {
                if (entity.InteriorRoom >= 0)
                    Widgets.PropertyText(L("Salle"), entity.InteriorRoom == 0 ? L("0 (limbo, vue de l'extérieur)") : $"{entity.InteriorRoom}");
                if (entity.InteriorPortal >= 0)
                    Widgets.PropertyText(L("Attaché au portail"), $"{entity.InteriorPortal}");
            }
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

    private static void WarningText(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(UiTheme.Warning, $"{Icons.TriangleAlert} {text}");
        ImGui.PopTextWrapPos();
    }

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

}
