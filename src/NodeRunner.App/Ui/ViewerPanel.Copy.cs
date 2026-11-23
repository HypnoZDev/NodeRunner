using System.Globalization;
using ImGuiNET;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// "Copier" menu of the viewer (GTA V): the model's name, its hash, or Lua code spawning it in FiveM.
internal sealed partial class ViewerPanel
{
    private void DrawCopyMenu(ViewerItem item)
    {
        if (!ImGui.BeginPopup("##viewer_copy"))
            return;
        uint hash = item.Kind == ViewerKind.Vehicle ? item.Location.ModelHash : item.ArchetypeHash;
        string hex = $"0x{hash:X8}";
        string number = unchecked((int)hash).ToString(CultureInfo.InvariantCulture);
        Copy(L("Nom"), item.Name);
        Copy(L("Hash (hexadécimal)"), hex);
        Copy(L("Hash (nombre, comme GetHashKey)"), number);
        ImGui.Separator();
        string what = item.Kind == ViewerKind.Vehicle ? L("Code Lua : faire apparaître ce véhicule") : L("Code Lua : faire apparaître cet objet");
        if (ImGui.MenuItem(what))
            CopyText(SpawnLua(item, hex));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(SpawnLua(item, hex));
        ImGui.EndPopup();
    }

    private void Copy(string label, string value)
    {
        if (ImGui.MenuItem(label, value))
            CopyText(value);
    }

    private void CopyText(string text)
    {
        ImGui.SetClipboardText(text);
        state.Notify(L("Copié dans le presse-papiers."));
    }

    // Client script spawning the model 4 m in front of the player. A name read as a hash has no backtick form.
    private static string SpawnLua(ViewerItem item, string hex)
    {
        string model = item.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex : $"`{item.Name}`";
        string create = item.Kind == ViewerKind.Vehicle
            ? "    local entity = CreateVehicle(model, coords.x, coords.y, coords.z, GetEntityHeading(ped), true, false)\n"
            : "    local entity = CreateObject(model, coords.x, coords.y, coords.z, true, false, false)\n"
              + "    PlaceObjectOnGroundProperly(entity)\n";
        return "CreateThread(function()\n"
            + $"    local model = {model}\n"
            + "    RequestModel(model)\n"
            + "    while not HasModelLoaded(model) do Wait(0) end\n"
            + "    local ped = PlayerPedId()\n"
            + "    local coords = GetOffsetFromEntityInWorldCoords(ped, 0.0, 4.0, 0.0)\n"
            + create
            + "    SetModelAsNoLongerNeeded(model)\n"
            + "end)\n";
    }
}
