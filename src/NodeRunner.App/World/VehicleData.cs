using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;

namespace NodeRunner.App.World;

// What the game says of its vehicles besides their models: palette, planned colours, sheets.
internal sealed record VehicleData(VehiclePaints Paints, VehicleInfoCatalog Info)
{
    public static VehicleData Load(GameContent content)
    {
        var paints = VehiclePaints.Empty;
        var info = VehicleInfoCatalog.Empty;
        try
        {
            paints = VehiclePaints.Load(content, new VehiclePaintReader());
            foreach (var error in paints.Errors)
                Diagnostics.ErrorLog.Add(L("Couleurs des véhicules"), error);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Diagnostics.ErrorLog.Add(L("Couleurs des véhicules"), ex);
        }
        try
        {
            info = VehicleInfoCatalog.Load(content);
            foreach (var error in info.Errors)
                Diagnostics.ErrorLog.Add(L("Fiches des véhicules"), error);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Diagnostics.ErrorLog.Add(L("Fiches des véhicules"), ex);
        }
        return new VehicleData(paints, info);
    }
}
