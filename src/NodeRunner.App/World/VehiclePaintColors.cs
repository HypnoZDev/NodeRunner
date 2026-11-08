using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// Colours given to the renderer for a vehicle (by VehiclePaintSlot) from palette numbers in carvariations order.
internal static class VehiclePaintColors
{
    // Order of the numbers of a combination, and of the script functions.
    public static readonly VehiclePaintSlot[] Slots =
    [
        VehiclePaintSlot.Primary, VehiclePaintSlot.Secondary, VehiclePaintSlot.Pearlescent, VehiclePaintSlot.Wheel,
        VehiclePaintSlot.InteriorTrim, VehiclePaintSlot.Dashboard,
    ];

    public static readonly int[] Default = [0, 0, 0, 156, 0, 0];

    public static Vector3[] From(VehiclePaints paints, IReadOnlyList<int> indices)
    {
        var colors = Enumerable.Repeat(Vector3.One, 8).ToArray();
        for (int i = 0; i < Slots.Length; i++)
        {
            int index = i < indices.Count && indices[i] >= 0 ? indices[i] : Default[i];
            colors[(int)Slots[i]] = paints.Color(index) is { } color ? Display(color) : Vector3.One;
        }
        return colors;
    }

    // Colour drawn for a palette entry: chrome and colour-shifting paints have no plain colour in the file.
    public static Vector3 Display(VehicleColor color) => color.Finish switch
    {
        VehicleFinish.Chrome => new Vector3(0.78f, 0.8f, 0.82f),
        VehicleFinish.Chameleon => new Vector3(0.45f, 0.35f, 0.55f),
        _ => color.Color,
    };
}
