using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// Properties panel (docked on the right) of the selected object: name and quick actions, then a label / value grid in
// sections: placement (editable, each change can be undone), files, level of detail (parent and children can be
// selected in turn), entity sets of an interior (shown or hidden live), render settings.
internal sealed class PropertiesPanel(AppState state, Action<uint> showModel, CollisionLayer collisionLayer)
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
    private static readonly (uint Flag, string Text)[] KnownFlags =
    [
        (0x8, "LOD dans la carte parente"),
        (0x80000, "projette des ombres fixes"),
        (0x100000, "projette des ombres mobiles"),
        (0x800000, "ombres seulement"),
        (0x1000000, "absent des reflets"),
        (0x2000000, "reflets seulement"),
        (0x8000000, "reflets de l'eau seulement"),
        (0x20000000, "miroirs seulement"),
    ];

    public void Draw(ref bool visible)
    {
        if (!ImGui.Begin(Title, ref visible))
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

        if (state.Selection is { } entity && state.World.Maps is { } library)
        {
            var names = state.World.Names ?? new HashNames();
            if (state.SelectedEntities.Count is var count and > 1)
            {
                Widgets.Hint($"{count} objets sélectionnés : propriétés du dernier cliqué. Les poignées 3D et Suppr agissent sur tout le groupe. "
                    + "Ctrl+clic pour en ajouter ou en retirer.", wrap: true);
                ImGui.Dummy(new Vector2(0, 2));
            }
            DrawHeader(entity, names);
            if (Widgets.Section("Transformation", "transform"))
                DrawPlacement(entity, library, names);
            if (Widgets.Section("Fichiers", "files"))
                DrawFiles(entity, library, names);
            if (Widgets.Section("Niveau de détail", "lod"))
                DrawLevelOfDetail(entity, names);
            if ((entity.IsInterior ? entity : entity.Interior) is { EntitySets.Count: > 0 } interior
                && Widgets.Section($"Entity sets ({interior.EntitySets.Count})", "entity_sets"))
                DrawEntitySets(interior, library, names);
            if (Widgets.Section("Réglages", "flags", defaultOpen: false))
                DrawFlags(entity, library);
        }
        else if (state.SelectedPathNode is { } node && state.World.PathNetwork is { } network)
            DrawPathNode(node, network);
        else if (state.SelectedOccluder is { } occluder)
            DrawOccluder(occluder);
        else
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(UiTheme.TextMuted, "Aucun objet sélectionné");
            Widgets.Hint("Clic gauche sur un objet de la vue 3D, ou dans la liste Scène, pour voir et modifier ses propriétés.", wrap: true);
        }
        ImGui.End();
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
        if (Widgets.Button($"{Icons.Crosshair}  Aller à l'objet", "Placer la caméra devant l'objet (double-clic dans la liste Scène)", buttonWidth))
            state.FocusOn(entity);
        ImGui.SameLine();
        string position = state.FormatPosition(entity.Position);
        string withHeading = state.FormatPosition(entity.Position, FiveMFormat.Heading(entity));
        if (Widgets.Button($"{Icons.Copy}  Copier la position", $"Copier {position}\nClic droit : avec le cap ({withHeading})\n"
            + $"Format : {PositionText.FormatName(state.CopyFormat)} (à changer dans le menu ...)", buttonWidth))
            ImGui.SetClipboardText(position);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(withHeading);
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Ellipsis, "more_copies", "Autres copies, désélectionner"))
            ImGui.OpenPopup("more_copies_menu");
        if (entity.CanEdit)
        {
            if (Widgets.Button($"{Icons.ArrowDownToLine}  Poser au sol", "Monter ou descendre l'objet jusqu'à la collision en dessous (touche Fin)",
                ImGui.GetContentRegionAvail().X))
                state.PutSelectionOnGround();
        }
        if (ImGui.BeginPopup("more_copies_menu"))
        {
            if (ImGui.MenuItem($"{Icons.Copy}  Copier position et cap : {withHeading}"))
                ImGui.SetClipboardText(withHeading);
            if (state.SelectedEntities.Count is var selected and > 1
                && ImGui.MenuItem($"{Icons.Copy}  Copier les {selected} objets sélectionnés (liste : nom, position, cap)"))
                ImGui.SetClipboardText(state.FormatSelection());
            if (ImGui.BeginMenu($"{Icons.Copy}  Format de copie : {PositionText.FormatName(state.CopyFormat)}"))
            {
                foreach (var format in Enum.GetValues<CopyFormat>())
                {
                    string example = PositionText.Position(new Vector3(215.76f, -810.12f, 30.73f), null, format);
                    if (ImGui.MenuItem($"{PositionText.FormatName(format)}  ·  {example}", null, state.CopyFormat == format))
                        state.CopyFormat = format;
                }
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem($"{Icons.Copy}  Copier le nom : {name}"))
                ImGui.SetClipboardText(name);
            if (entity.Map?.FilePath is { } path && ImGui.MenuItem($"{Icons.Copy}  Copier le nom du YMAP : {Path.GetFileNameWithoutExtension(path)}"))
                ImGui.SetClipboardText(Path.GetFileNameWithoutExtension(path));
            ImGui.Separator();
            if (ImGui.MenuItem($"{Icons.Box}  Voir le modèle dans la visionneuse"))
                showModel(entity.ArchetypeHash);
            if (ImGui.MenuItem($"{Icons.X}  Désélectionner", "Échap"))
                state.Select(null);
            ImGui.Separator();
            int group = state.DeletableSelection.Count;
            if (ImGui.MenuItem(group > 1 ? $"{Icons.Trash}  Supprimer les {group} objets sélectionnés..." : $"{Icons.Trash}  Supprimer...", "Suppr", false, group > 0))
                state.RequestDelete();
            ImGui.EndPopup();
        }
        ImGui.Dummy(new Vector2(0, 2));
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
            Widgets.PropertyText("Position", $"X {p.X:0.00}   Y {p.Y:0.00}   Z {p.Z:0.00}");
            Widgets.PropertyText("Rotation", $"X {r.X:0.0}°   Y {r.Y:0.0}°   Z {r.Z:0.0}°");
            Widgets.PropertyText("Échelle", $"{entity.Scale.X:0.###} (horiz.)   {entity.Scale.Z:0.###} (vert.)");
        }
        Widgets.PropertyText("Distance d'affichage", $"{library.DrawDistance(entity):0} m");
        Widgets.EndProperties();

        if (entity.Interior is not null)
            Widgets.Hint("Objet défini par l'intérieur (.ytyp) : le déplacer le déplace dans toutes les copies de cet intérieur ; Ctrl+S écrit le .ytyp modifié.", wrap: true);
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

        Widgets.Property("Position", "Mètres. X = est, Y = nord, Z = haut.");
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
            TrackEdit(entity, before, $"Déplacer {name}");
        }

        Widgets.Property("Rotation (°)", "Degrés autour des axes X, Y et Z.");
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
            TrackEdit(entity, before, $"Tourner {name}");
        }

        // Interiors cannot be scaled in the game. It stores one horizontal scale (X and Y) and one vertical scale (Z).
        if (!entity.IsInterior)
        {
            Widgets.Property("Échelle", "Horizontale (X et Y), puis verticale (Z).");
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
                TrackEdit(entity, before, $"Redimensionner {name}");
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
            state.History.Add(new PlacementEdit(entity, _editStart, Placement.Of(entity), description));
    }

    private void DrawFiles(MapEntity entity, MapLibrary library, HashNames names)
    {
        if (!Widgets.BeginProperties("##files"))
            return;

        var archetype = library.GetArchetype(entity.ArchetypeHash);
        Widgets.PropertyText("Type (archétype)", names.Describe(entity.ArchetypeHash));
        if (archetype is not null)
        {
            FileRow("Défini dans", library.GetArchetypeFile(entity.ArchetypeHash));
            if (archetype.Kind == ArchetypeKind.Interior)
                Widgets.PropertyText("Intérieur (MLO)", $"{entity.InteriorEntities.Count} objets, {archetype.InteriorRooms.Count} salles");
            else
                FileRow("Modèle 3D", state.World.Models?.Locate(archetype)?.Path);
            if (archetype.TextureDictionaryHash != 0)
                Widgets.PropertyText("Textures", $"{names.Describe(archetype.TextureDictionaryHash)}.ytd");
            var size = archetype.BoundsMax - archetype.BoundsMin;
            Widgets.PropertyText("Taille", $"{size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} m");
        }
        FileRow("Placé par", entity.Map?.FilePath);

        if (entity.Interior is { } interior)
        {
            Widgets.Property("Dans l'intérieur");
            if (Widgets.Link($"{names.Describe(interior.ArchetypeHash)}##interior", "Sélectionner l'intérieur"))
                state.Select(interior);
            if (entity.InteriorRoom >= 0)
                Widgets.PropertyText("Salle", entity.InteriorRoom == 0 ? "0 (limbo, vue de l'extérieur)" : $"{entity.InteriorRoom}");
            if (entity.InteriorPortal >= 0)
                Widgets.PropertyText("Attaché au portail", $"{entity.InteriorPortal}");
            if (entity.EntitySet is { } set)
                Widgets.PropertyText("Entity set", names.Describe(set.NameHash), set.IsActive ? null : "Entity set masqué");
        }
        Widgets.EndProperties();

        if (archetype is null)
            ImGui.TextColored(UiTheme.Warning, "Type introuvable dans les fichiers .ytyp.");
    }

    // The optional groups of objects of an interior: a box per set to show or hide it (not saved: in the game,
    // scripts activate them), the script lines that activate the shown sets, and a field to name a set.
    private void DrawEntitySets(MapEntity interior, MapLibrary library, HashNames names)
    {
        var sets = interior.EntitySets;
        Widgets.Hint("Variantes de mobilier activées par les scripts du jeu (styles d'appartement, améliorations...). "
            + "Masquées par défaut, comme en jeu. Non enregistré dans les YMAP.", wrap: true);
        ImGui.Dummy(new Vector2(0, 2));

        float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
        if (Widgets.Button($"{Icons.Eye}  Tout afficher", "Afficher tous les entity sets de cet intérieur", width))
            SetAll(library, sets, true);
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.EyeOff}  Tout masquer", "Masquer tous les entity sets de cet intérieur", width))
            SetAll(library, sets, false);
        ImGui.SameLine();
        var shown = sets.Where(s => s.IsActive).ToList();
        string format = state.CopyFormat == CopyFormat.CSharp ? "C#" : "Lua";
        if (Widgets.Button($"{Icons.Copy}  Copier le script", shown.Count == 0
                ? "Cochez d'abord les entity sets à activer."
                : $"Copier les lignes {format} qui activent les {shown.Count} entity sets affichés (GetInteriorAtCoords, "
                    + "ActivateInteriorEntitySet, RefreshInterior).\nFormat : celui de « Copier la position » (menu ...).",
                width, enabled: shown.Count > 0))
        {
            var items = shown.Select(s => new EntitySetItem(s.NameHash, names.TryGet(s.NameHash))).ToList();
            ImGui.SetClipboardText(EntitySetScript.Activate(names.Describe(interior.ArchetypeHash), PointInside(interior, library),
                items, state.CopyFormat));
            int unknown = items.Count(i => i.Name is null);
            state.Notify(unknown == 0
                ? $"Script copié ({items.Count} entity sets)."
                : $"Script copié : {unknown} entity sets sur {items.Count} sans nom connu (lignes en commentaire).", warning: unknown > 0);
        }

        // Naming: the files only keep hashes; a typed name whose hash is one of these sets becomes its name.
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##set_name", "Nommer un entity set (taper son nom, Entrée)", ref _setName, 96,
                ImGuiInputTextFlags.EnterReturnsTrue))
            NameEntitySet(interior, sets, library, names);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip("Le jeu ne stocke que le hash des noms. Tapez le nom d'un entity set (ex. tiré d'un script) : "
                + "s'il correspond, il est retenu jusqu'au rechargement de la carte.\nPour les garder : un nom par ligne dans data/strings.txt.");
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
            ImGui.SameLine();
            int count = set.Entities.Count(e => !e.IsDeleted);
            ImGui.TextColored(UiTheme.TextMuted, count == 1 ? "1 objet" : $"{count} objets");
        }
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
            _setNameResult = (true, $"Nommé : {name.ToLowerInvariant()} (0x{hash:X8}).");
            _setName = "";
        }
        else if (library.FindEntitySetInteriors(hash) is { Count: > 0 } others)
        {
            // Not a set of this interior, but of another one: the name is kept all the same.
            names.Add(name);
            string where = string.Join(", ", others.Take(3).Select(names.Describe)) + (others.Count > 3 ? "..." : "");
            _setNameResult = (true, $"Nom retenu, mais c'est un entity set d'un autre intérieur : {where}.");
            _setName = "";
        }
        else
            _setNameResult = (false, $"Aucun entity set ne s'appelle « {name} » (hash 0x{hash:X8}). Vérifiez l'orthographe.");
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

    private void DrawLevelOfDetail(MapEntity entity, HashNames names)
    {
        if (!Widgets.BeginProperties("##lod"))
            return;

        Widgets.PropertyText("Niveau", LevelName(entity.LodLevel));
        if (entity.ChildLodDistance > 0f)
            Widgets.PropertyText("Remplacé par ses enfants", $"à moins de {entity.ChildLodDistance:0} m");

        Widgets.Property("Parent");
        if (entity.Parent is { } parent)
        {
            if (Widgets.Link($"{names.Describe(parent.ArchetypeHash)}##parent", $"Sélectionner le parent : {LevelName(parent.LodLevel)}"))
                state.Select(parent);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("aucun");
        }

        var children = entity.Children.Where(c => !c.IsDeleted).ToList();
        Widgets.Property("Enfants");
        if (children.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("aucun");
        }
        else if (ImGui.TreeNode($"{children.Count}###children"))
        {
            for (int i = 0; i < children.Count && i < MaxChildrenListed; i++)
            {
                if (Widgets.Link($"{names.Describe(children[i].ArchetypeHash)}##child{i}", "Sélectionner cet enfant"))
                    state.Select(children[i]);
            }
            if (children.Count > MaxChildrenListed)
                Widgets.Hint($"... et {children.Count - MaxChildrenListed} autres");
            ImGui.TreePop();
        }
        Widgets.EndProperties();
    }

    private static void DrawFlags(MapEntity entity, MapLibrary library)
    {
        if (!Widgets.BeginProperties("##flags"))
            return;

        Widgets.Property("De l'objet");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"0x{entity.Flags:X8}");
        var known = KnownFlags.Where(f => (entity.Flags & f.Flag) != 0).Select(f => f.Text).ToList();
        if (known.Count > 0)
            ImGui.TextWrapped(string.Join(", ", known));

        if (library.GetArchetype(entity.ArchetypeHash) is { } archetype)
        {
            Widgets.Property("Du type");
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"0x{archetype.Flags:X8}");
            if (archetype.IsShadowOrReflectionProxy)
                ImGui.TextWrapped("doublure pour les ombres et reflets (jamais affichée)");
        }
        Widgets.EndProperties();
    }

    private static void FileRow(string label, string? path) =>
        Widgets.PropertyText(label, path is null ? "?" : Path.GetFileName(path), path);

    private static string LevelName(LodLevel level) => level switch
    {
        LodLevel.Hd => "détail (HD)",
        LodLevel.OrphanHd => "détail sans LOD",
        LodLevel.Lod => "LOD (vue moyenne)",
        _ => $"{level.ToString().ToUpperInvariant()} (vue lointaine)",
    };

    private Vector3 _nodeEditStart;

    private void DrawPathNodePosition(PathNode node, PathNetwork network)
    {
        Widgets.Property("Position", "Mètres. X = est, Y = nord, Z = haut. Aussi avec les poignées (outil Déplacer, touche 2).");
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
                state.History.Add(new PathNodeMoveEdit(network, node, _nodeEditStart, node.Position, $"Déplacer le nœud {node.Id.AreaId}:{node.Id.NodeId}"));
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
                    state.History.Add(new OccluderBoxEdit(file, start, end, $"Modifier la boîte d'occlusion {occluder.Index}"));
                _boxEditStart = null;
            }
        }

        Widgets.Property("Centre", "Mètres. X = est, Y = nord, Z = haut. Arrondi à 25 cm (précision du format du jeu). Aussi avec les poignées (touche 2).");
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

        Widgets.Property("Taille", "Longueur (X), largeur (Y) et hauteur (Z) de la boîte, en mètres, dans son propre sens. Aussi avec les poignées (touche 4).");
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

        Widgets.Property("Rotation Z", "Degrés. Une boîte d'occlusion ne tourne qu'autour de l'axe vertical. Aussi avec les poignées (touche 3).");
        {
            var box = file.Boxes[occluder.Index];
            float degrees = (box.Angle * 180f / MathF.PI + 360f) % 360f;
            if (Widgets.AxisField("##occl_angle", ref degrees, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                OccluderMaps.SetBox(file, box with { Angle = degrees * MathF.PI / 180f });
            Track(box);
        }

        if (file.IsBoxChanged(occluder.Index))
            Widgets.PropertyText("État", "modifiée (pas encore enregistrée)", "L'enregistrement des boîtes d'occlusion arrive à l'étape suivante.", UiTheme.Warning);
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
        if (Widgets.Button($"{Icons.Crosshair}  Aller à l'occluder"))
            state.FocusOn(center, MathF.Max(size.Length() / 2, 5f));
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.Copy}  Copier la position", state.FormatPosition(center)))
            ImGui.SetClipboardText(state.FormatPosition(center));
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.Trash}  Supprimer", "Suppr. Annulable avec Ctrl+Z."))
        {
            state.DeleteSelectedOccluder();
            return;
        }

        if (Widgets.Section(isBox ? "Boîte" : "Maillage", "occluder") && Widgets.BeginProperties("##occluder"))
        {
            if (occluder.Box is not null)
                DrawOccluderBoxFields(occluder);
            else if (occluder.Model is { } model)
            {
                Widgets.PropertyText("Triangles", Widgets.Count(model.TriangleCount));
                Widgets.PropertyText("Sommets", Widgets.Count(model.Vertices.Count), "255 au plus par maillage.");
                Widgets.PropertyText("Emprise", $"{size.X:0} x {size.Y:0} x {size.Z:0} m");
                Widgets.PropertyText("Centre", $"{center.X:0.#}, {center.Y:0.#}, {center.Z:0.#}");
            }
            Widgets.PropertyText("Fichier", occluder.File.FileName, occluder.File.GamePath);
            var occluders = occluder.File.Occluders;
            Widgets.PropertyText("Dans ce fichier", $"{occluders.Boxes.Count} boîte(s), {occluders.Models.Count} maillage(s)");
            Widgets.EndProperties();
        }
        Widgets.Hint("Le jeu ne dessine pas ce qui est caché derrière un occluder. Échap pour désélectionner ; "
            + "masquez le calque Occlusion pour sélectionner les bâtiments.", wrap: true);
    }

    private void DrawPathNode(PathNode node, PathNetwork network)
    {
        var names = state.World.Names ?? new HashNames();
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted($"{Icons.Route}  Nœud {node.Id.AreaId}:{node.Id.NodeId}");
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  nodes{node.Id.AreaId}.ynd");
        var p = node.Position;
        if (Widgets.Button($"{Icons.Copy}  Copier la position", state.FormatPosition(p)))
            ImGui.SetClipboardText(state.FormatPosition(p));

        if (Widgets.Section("Nœud", "path_node") && Widgets.BeginProperties("##path_node"))
        {
            DrawPathNodePosition(node, network);
            Widgets.PropertyText("Catégorie", PathNodeLayer.CategoryOf(node));
            if (node.StreetNameHash != 0)
                Widgets.PropertyText("Rue", names.Describe(node.StreetNameHash));
            Widgets.PropertyText("Vitesse", node.SpeedCategory switch { 0 => "lente", 1 => "normale", 2 => "rapide", _ => "très rapide" });
            Widgets.PropertyText("Densité de trafic", $"{node.Density} / 15");
            if (node.Special != PathNodeSpecialType.None)
                Widgets.PropertyText("Type spécial", SpecialLabel(node.Special));
            var flags = new List<string>();
            if (node.IsJunction) flags.Add("carrefour");
            if (node.IsHighway) flags.Add("autoroute");
            if (node.IsTunnel) flags.Add("tunnel");
            if (node.IsOffRoad) flags.Add("hors-piste");
            if (node.IsSlipRoad) flags.Add("bretelle");
            if (node.NoBigVehicles) flags.Add("pas de gros véhicules");
            if (node.NoGps) flags.Add("ignoré par le GPS");
            if (node.IsSwitchedOff) flags.Add("sans trafic par défaut");
            Widgets.PropertyText("Particularités", flags.Count == 0 ? "aucune" : string.Join(", ", flags));
            Widgets.PropertyText("Octets bruts", $"{node.Flags0:X2} {node.Flags1:X2} {node.Flags2:X2} {node.Flags3:X2} {node.Flags4:X2} {node.LinkCountExtra:X2}",
                "Flags 0 à 4 et octet de liens, tels que stockés dans le fichier .ynd");
            Widgets.EndProperties();
        }

        var area = network.GetArea(node.Id.AreaId);
        var links = area?.LinksOf(node).ToList() ?? [];
        if (Widgets.Section($"Liens ({links.Count})", "path_links") && links.Count > 0 && Widgets.BeginProperties("##path_links"))
        {
            foreach (var (link, i) in links.Select((l, i) => (l, i)))
            {
                var target = network.GetNode(link.Target);
                Widgets.Property($"Vers {link.Target.AreaId}:{link.Target.NodeId}");
                string lanes = $"{link.LanesForward} voie(s) aller, {link.LanesBackward} retour, {network.LinkLength(node, link)} m";
                if (link.IsShortcut) lanes += ", raccourci";
                if (link.IsNarrowRoad) lanes += ", étroite";
                if (link.DontUseForNavigation) lanes += ", hors navigation";
                if (target is not null && Widgets.Link($"{lanes}##link{i}", "Sélectionner ce nœud"))
                    state.SelectedPathNode = target;
                else if (target is null)
                    ImGui.TextColored(UiTheme.TextFaint, $"{lanes} (zone non chargée)");
            }
            Widgets.EndProperties();
        }
    }

    private static string SpecialLabel(PathNodeSpecialType type) => type switch
    {
        PathNodeSpecialType.ParkingSpace => "place de parking",
        PathNodeSpecialType.PedCrossing => "passage piéton",
        PathNodeSpecialType.PedAssistedMovement => "déplacement piéton guidé",
        PathNodeSpecialType.TrafficLightStop => "arrêt au feu",
        PathNodeSpecialType.StopSign => "stop",
        PathNodeSpecialType.Caution => "prudence",
        PathNodeSpecialType.PedCrossingNoWait => "passage piéton sans attente",
        PathNodeSpecialType.EmergencyVehiclesOnly => "véhicules d'urgence seulement",
        PathNodeSpecialType.OffRoadJunction => "carrefour hors-piste",
        _ => $"{(int)type}",
    };
}
