using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
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
            if (Widgets.Button(L($"{Icons.RefreshCw}  Inverser les salles"), L("Échange « De la salle » et « Vers la salle » (le portail est retourné avec).")))
                Change(InteriorCreation.Turned(portal with { RoomFrom = portal.RoomTo, RoomTo = portal.RoomFrom }), L($"Inverser les salles du portail {index}"));
            // Side it faces: the game's files have it facing out of the first room.
            if (InteriorCreation.FacesAsTheGame(portal, archetype.InteriorRooms) is { } asTheGame)
            {
                Widgets.Property(L("Sens"), L("Les coins d'un portail du jeu tournent de façon à ce qu'il regarde hors de « De la salle ». ")
                    + L("À l'envers, l'intérieur peut clignoter ou disparaître en jeu quand on passe le portail."));
                if (asTheGame)
                    ImGui.TextColored(UiTheme.TextMuted, L("comme le jeu"));
                else
                {
                    ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert}  à l'envers"));
                    ImGui.SameLine();
                    if (Widgets.Button(L("Retourner"), L("Remet les coins dans l'ordre du jeu (même ouverture).")))
                        Change(InteriorCreation.Turned(portal), L($"Retourner le portail {index}"));
                }
            }
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
}
