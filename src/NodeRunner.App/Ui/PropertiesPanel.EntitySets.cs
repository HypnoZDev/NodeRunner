using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
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
}
