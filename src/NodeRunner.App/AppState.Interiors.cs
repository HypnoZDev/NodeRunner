using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // Whether name can be given to a new interior: valid, and no object type (of the game or of the project) has it.
    public bool IsFreeInteriorName(string name) =>
        InteriorCreation.IsValidName(name) && World.Maps is { } library && library.GetArchetype(Data.Hashing.Joaat.Hash(name)) is null
        && !TypeFileNames().Contains(name + ".ytyp");

    private (GameContent Content, HashSet<string> Names)? _typeFileNames;

    // File names of every .ytyp loaded (game and project): a new interior's file must not replace one.
    private HashSet<string> TypeFileNames()
    {
        if (World.Content is not { } content)
            return [];
        if (_typeFileNames is not { } cached || cached.Content != content)
            _typeFileNames = cached = (content, content.EnumerateFiles(".ytyp", withInactiveFallback: true).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        return cached.Names;
    }

    public string SuggestInteriorName() =>
        InteriorCreation.SuggestName(Project?.ResourceName ?? "mlo", IsFreeInteriorName);

    // A new interior on the ground in front of the camera, its door facing it, in the project's map; selected, with
    // its rooms and portals shown.
    public void CreateInterior(string name)
    {
        Notice = null;
        if (World.Maps is not { } library || !IsFreeInteriorName(name) || ProjectMap(library) is not { } map)
            return;
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitY;
        var point = Camera.Position + forward * 10f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 2f;
        var edit = InteriorCreateEdit.Apply(library, map, name, point with { Z = ground }, InteriorCreation.FacingAway(forward), L($"Créer l'intérieur {name}"));
        History.Add(edit);
        World.Names?.Add(name);
        Select(edit.Interior);
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals), true);
        Notify(L($"Intérieur « {name} » créé devant la caméra : une salle de 6 x 6 m avec une porte vers l'extérieur. ")
            + L("Placez des objets dedans, ajustez la salle avec ses poignées, puis Ctrl+S."));
    }

    // Interior to duplicate under a new name, asked from Properties or the Edit menu (MainUi opens its window).
    public MapEntity? DuplicateInteriorRequest { get; set; }

    public string SuggestDuplicateName(uint sourceHash) =>
        InteriorCreation.SuggestName(World.Names?.TryGet(sourceHash) ?? "mlo", IsFreeInteriorName, "_copy");

    // A copy of interior's type under a new name (its own .ytyp in the project, with the same objects, entity sets,
    // rooms and portals), placed on the ground in front of the camera, its lowest room on the ground; selected.
    public void DuplicateInterior(MapEntity interior, string name)
    {
        Notice = null;
        if (World.Maps is not { } library || !IsFreeInteriorName(name) || ProjectMap(library) is not { } map
            || library.GetArchetype(interior.ArchetypeHash) is not { Kind: ArchetypeKind.Interior } source)
            return;
        var rooms = source.InteriorRooms.Skip(1).ToList();
        float reach = rooms.Count == 0 ? 0f
            : rooms.Max(r => MathF.Max(new Vector2(r.BoundsMin.X, r.BoundsMin.Y).Length(), new Vector2(r.BoundsMax.X, r.BoundsMax.Y).Length()));
        float floor = rooms.Count == 0 ? 0f : rooms.Min(r => r.BoundsMin.Z);
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitY;
        var point = Camera.Position + forward * (10f + Math.Min(reach, 200f));
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 2f;
        var edit = InteriorDuplicateEdit.Apply(library, map, source.NameHash, name, point with { Z = ground - floor }, InteriorCreation.FacingAway(forward),
            L($"Dupliquer l'intérieur sous le nom {name}"));
        History.Add(edit);
        World.Names?.Add(name);
        Select(edit.Interior);
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
        Notify(L($"Intérieur « {name} » créé devant la caméra, copie de {World.Names?.Describe(source.NameHash) ?? ""} : ")
            + L("ses objets, salles et portails sont maintenant à lui, les modifier ne change pas l'original. Ctrl+S l'enregistre dans son propre .ytyp."));
    }

    // A room of 4 x 4 x 3 m in front of the camera, in the interior's space; selected.
    public void AddRoom(MapEntity interior)
    {
        if (World.Maps is not { } library || library.GetArchetype(interior.ArchetypeHash) is not { } archetype
            || !Matrix4x4.Invert(interior.Transform, out var toInterior))
            return;
        var center = Vector3.Transform(Camera.Position + Camera.Forward * 6f, toInterior);
        string name = $"room{archetype.InteriorRooms.Count}";
        var room = new InteriorRoom(center - new Vector3(2f, 2f, 1f), center + new Vector3(2f, 2f, 2f)) { Name = name };
        var edit = InteriorLayoutEdit.Apply(library, interior, () => library.AddRoom(interior.ArchetypeHash, room),
            L($"Ajouter la salle {name}"), isPortal: false, added: true);
        History.Add(edit);
        SelectRoom(new RoomSelection(interior, edit.Index));
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
        Notify(L($"Salle {edit.Index} « {name} » ajoutée devant la caméra : ajustez ses coins dans Propriétés, puis ajoutez-lui un portail."));
    }

    public void DeleteSelectedRoom()
    {
        if (SelectedRoom is not { Index: > 0 } selected || World.Maps is not { } library
            || library.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype || selected.Index >= archetype.InteriorRooms.Count)
            return;
        var room = archetype.InteriorRooms[selected.Index];
        int portals = archetype.InteriorPortals.Count(p => p.RoomFrom == selected.Index || p.RoomTo == selected.Index);
        string name = room.Name.Length > 0 ? room.Name : $"{selected.Index}";
        History.Add(InteriorLayoutEdit.Apply(library, selected.Interior, () =>
        {
            library.RemoveRoom(selected.Interior.ArchetypeHash, selected.Index);
            return selected.Index;
        }, L($"Supprimer la salle {name}"), isPortal: false, added: false));
        Select(selected.Interior);
        NotifyUndoable(L($"Salle « {name} » supprimée{(portals > 0 ? L($" avec ses {portals} portail(s)") : "")} ; ses objets vont dans la salle qui les entoure ")
            + L("(sinon dans limbo). Ctrl+Z pour annuler."));
    }

    // A portal of 1.2 x 2.2 m (at most) at the bottom of the side of the room facing the camera, to the outside; selected.
    public void AddPortal(MapEntity interior, int roomIndex)
    {
        if (World.Maps is not { } library || library.GetArchetype(interior.ArchetypeHash) is not { } archetype
            || roomIndex < 1 || roomIndex >= archetype.InteriorRooms.Count || !Matrix4x4.Invert(interior.Transform, out var toInterior))
            return;
        var room = archetype.InteriorRooms[roomIndex];
        var min = room.BoundsMin;
        var max = room.BoundsMax;
        var center = (min + max) / 2;
        var half = Vector3.Max((max - min) / 2, new Vector3(0.05f));
        var toCamera = Vector3.Transform(Camera.Position, toInterior) - center;
        bool alongX = MathF.Abs(toCamera.X) / half.X >= MathF.Abs(toCamera.Y) / half.Y;
        float width = MathF.Min(1.2f, 2 * (alongX ? half.Y : half.X) - 0.2f) / 2;
        float bottom = min.Z;
        float top = MathF.Min(min.Z + 2.2f, max.Z - 0.05f);
        Vector3[] corners = alongX
            ? [new(toCamera.X >= 0 ? max.X : min.X, center.Y - width, top), new(toCamera.X >= 0 ? max.X : min.X, center.Y + width, top),
               new(toCamera.X >= 0 ? max.X : min.X, center.Y + width, bottom), new(toCamera.X >= 0 ? max.X : min.X, center.Y - width, bottom)]
            : [new(center.X - width, toCamera.Y >= 0 ? max.Y : min.Y, top), new(center.X + width, toCamera.Y >= 0 ? max.Y : min.Y, top),
               new(center.X + width, toCamera.Y >= 0 ? max.Y : min.Y, bottom), new(center.X - width, toCamera.Y >= 0 ? max.Y : min.Y, bottom)];
        var portal = InteriorCreation.FacingAsTheGame(new InteriorPortal(roomIndex, 0, 0) { Corners = corners }, archetype.InteriorRooms);
        var edit = InteriorLayoutEdit.Apply(library, interior, () => library.AddPortal(interior.ArchetypeHash, portal),
            L($"Ajouter un portail à la salle {roomIndex}"), isPortal: true, added: true);
        History.Add(edit);
        SelectPortal(new RoomSelection(interior, edit.Index));
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals), true);
        Notify(L($"Portail {edit.Index} ajouté sur le côté de la salle face à la caméra, vers l'extérieur : placez ses coins sur l'ouverture ")
            + L("et choisissez les salles qu'il relie."));
    }

    public void DeleteSelectedPortal()
    {
        if (SelectedPortal is not { } selected || World.Maps is not { } library
            || library.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype || selected.Index >= archetype.InteriorPortals.Count)
            return;
        History.Add(InteriorLayoutEdit.Apply(library, selected.Interior, () =>
        {
            library.RemovePortal(selected.Interior.ArchetypeHash, selected.Index);
            return selected.Index;
        }, L($"Supprimer le portail {selected.Index}"), isPortal: true, added: false));
        Select(selected.Interior);
        NotifyUndoable(L($"Portail {selected.Index} supprimé. Ctrl+Z pour annuler."));
    }
}
