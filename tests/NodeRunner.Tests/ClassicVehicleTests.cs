using NodeRunner.Data.Native.Classic;

namespace NodeRunner.Tests;

// Vehicles of the ".ide" "cars" sections: the wheel model and size of GTA III / Vice City cars.
public class ClassicVehicleTests
{
    [Fact]
    public void Cars_GiveTheirWheels()
    {
        var defs = new ClassicDefinitions();
        int bad = defs.Read("""
            cars
            101, infernus, infernus, car, INFERNUS, INFERNS, executive, 10, 6, 0, 160, 0.7
            141, infernus2, infernus2, car, INFERNUS, INFERNS, null, executive, 7, 6, 0, 237, 0.74
            411, infernus3, infernus3, car, INFERNUS, INFERNU, null, executive, 5, 0, 0, -1, 0.7, 0.7, 0
            200, boat, boat, boat, BOAT, BOAT, ignore, 10, 0, 0
            end
            """);

        Assert.Equal(0, bad);
        Assert.Equal((160, 0.7f), (defs.FindVehicle("infernus")!.WheelModel, defs.FindVehicle("infernus")!.WheelScale));
        Assert.Equal((237, 0.74f), (defs.FindVehicle("INFERNUS2")!.WheelModel, defs.FindVehicle("infernus2")!.WheelScale));
        Assert.Equal(-1, defs.FindVehicle("infernus3")!.WheelModel);
        Assert.Equal(-1, defs.FindVehicle("boat")!.WheelModel);
    }
}
