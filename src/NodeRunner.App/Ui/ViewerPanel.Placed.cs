using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.App.Ui;

// "Where on the map": the copies of the shown object placed on the loaded map, nearest to the camera first.
internal sealed partial class ViewerPanel
{
    private const int MaxPlacedRows = 200;

    private uint _placedHash;
    private MapLibrary? _placedLibrary;
    private int _placedVersion = -1;
    private Vector3 _placedSortedFrom;
    private List<MapEntity> _placedCopies = [];
    private bool _closeForMap;
    private bool _docked;

    private List<MapEntity> PlacedCopies(ViewerItem item)
    {
        if (item.Kind != ViewerKind.Object || Library is not { } library)
            return [];
        var camera = state.Camera.Position;
        if (_placedHash != item.ArchetypeHash || _placedLibrary != library || _placedVersion != library.StructureVersion)
        {
            _placedHash = item.ArchetypeHash;
            _placedLibrary = library;
            _placedVersion = library.StructureVersion;
            _placedCopies = [];
            foreach (var map in library.Maps)
                foreach (var entity in map.Entities)
                {
                    if (entity.IsDeleted)
                        continue;
                    if (entity.ArchetypeHash == item.ArchetypeHash)
                        _placedCopies.Add(entity);
                    foreach (var child in entity.InteriorEntities)
                        if (!child.IsDeleted && child.ArchetypeHash == item.ArchetypeHash)
                            _placedCopies.Add(child);
                }
            Sort(camera);
        }
        else if (Vector3.DistanceSquared(camera, _placedSortedFrom) > 50f * 50f)
            Sort(camera);
        return _placedCopies;

        void Sort(Vector3 from)
        {
            _placedSortedFrom = from;
            _placedCopies.Sort((a, b) => Vector3.DistanceSquared(a.Position, from).CompareTo(Vector3.DistanceSquared(b.Position, from)));
        }
    }

    private void DrawGoToMapButton(ViewerItem item)
    {
        var copies = PlacedCopies(item);
        if (Widgets.Button(L($"{Icons.Crosshair}  Sur la carte ({Widgets.Count(copies.Count)})"), copies.Count > 0
                ? L($"Aller à l'exemplaire le plus proche de la caméra ({Widgets.Count(copies.Count)} sur la carte chargée).\nLa liste complète est dans l'onglet « Type d'objet ».")
                : L("Ce type n'est placé nulle part sur la carte"), enabled: copies.Count > 0))
            GoToCopy(copies[0]);
    }

    private float GoToMapButtonWidth(ViewerItem item) =>
        item.Kind == ViewerKind.Object && Library is not null
            ? ImGui.CalcTextSize(L($"{Icons.Crosshair}  Sur la carte ({Widgets.Count(PlacedCopies(item).Count)})")).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X
            : 0f;

    private void DrawPlacedSection(ViewerItem item)
    {
        var copies = PlacedCopies(item);
        if (!Widgets.Section(L($"Sur la carte ({Widgets.Count(copies.Count)})"), "viewer_placed"))
            return;
        if (copies.Count == 0)
        {
            Widgets.Hint(L("Ce type n'est placé nulle part sur la carte"), wrap: true);
            return;
        }
        Widgets.Hint(L("Le plus proche de la caméra d'abord. Clic : y aller ; clic droit : copier la position."), wrap: true);
        var names = Names;
        var camera = state.Camera.Position;
        for (int i = 0; i < copies.Count && i < MaxPlacedRows; i++)
        {
            var entity = copies[i];
            ImGui.PushID(i);
            float distance = Vector3.Distance(entity.Position, camera);
            string where = distance < 1000f ? $"{distance:0} m" : $"{distance / 1000f:0.0} km";
            string place = MapName(entity, names);
            if (entity.Interior is { } interior)
                place = L($"{place}, dans {names?.Describe(interior.ArchetypeHash) ?? $"0x{interior.ArchetypeHash:X8}"}");
            if (ImGui.Selectable($"{Icons.MapPinned}  {where}##copy"))
                GoToCopy(entity);
            ImGui.SameLine();
            ImGui.TextDisabled(place);
            if (ImGui.BeginPopupContextItem("##copy_menu"))
            {
                string position = state.FormatPosition(entity.Position, FiveMFormat.Heading(entity));
                if (ImGui.MenuItem(L("Copier la position"), position))
                    CopyText(position);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        if (copies.Count > MaxPlacedRows)
            ImGui.TextDisabled(L($"… et {Widgets.Count(copies.Count - MaxPlacedRows)} autres plus loin"));
    }

    private static string MapName(MapEntity entity, HashNames? names)
    {
        var map = entity.Map ?? entity.Interior?.Map;
        if (map is null)
            return "?";
        return map.FilePath is { } path ? Path.GetFileName(path) : names?.Describe(map.NameHash) ?? $"0x{map.NameHash:X8}";
    }

    // Selects the copy in the 3D view; a floating viewer, which hides the map, closes (it reopens on the same model).
    private void GoToCopy(MapEntity entity)
    {
        state.Marker = null;
        if (state.IsOtherGame)
            state.Gta4Selection = entity;
        else
            state.Select(entity);
        state.FocusOn(entity);
        if (!_docked)
            _closeForMap = true;
    }
}
