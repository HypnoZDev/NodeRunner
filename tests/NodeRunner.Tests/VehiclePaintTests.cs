using System.Numerics;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;

namespace NodeRunner.Tests;

public class VehiclePaintTests
{
    [Theory]
    [InlineData(2f, 1f, 1f, VehiclePaintSlot.Primary)]
    [InlineData(2f, 2f, 2f, VehiclePaintSlot.Secondary)]
    [InlineData(2f, 4f, 4f, VehiclePaintSlot.Wheel)]
    [InlineData(2f, 5f, 5f, VehiclePaintSlot.Unpainted)]
    [InlineData(2f, 7f, 7f, VehiclePaintSlot.Dashboard)]
    [InlineData(1f, 1f, 1f, VehiclePaintSlot.None)]
    [InlineData(2f, 1f, 2f, VehiclePaintSlot.None)]
    [InlineData(2f, 9f, 9f, VehiclePaintSlot.None)]
    public void PaintSlotComesFromMatDiffuseColor(float x, float y, float z, VehiclePaintSlot expected) =>
        Assert.Equal(expected, VehiclePaints.SlotOf(new Vector4(x, y, z, 0f)));

    [Fact]
    public void NoDiffuseColorMeansNoSlot() => Assert.Equal(VehiclePaintSlot.None, VehiclePaints.SlotOf(null));

    [Fact]
    public void ReadsThePaletteOfAnXmlCarcols()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CVehicleModelInfoVarGlobal>
              <Colors>
                <Item>
                  <color value="0xFF0A1428" />
                  <colorName> 0 Metallic Black		</colorName>
                </Item>
                <Item>
                  <color value="0x00000000" />
                  <colorName> 161 Anodized Red</colorName>
                  <rampTextureName>vehicle_paint_ramp_anod001</rampTextureName>
                </Item>
                <Item>
                  <color value="0xFF101010" />
                  <colorName>12 Matte Black</colorName>
                </Item>
              </Colors>
            </CVehicleModelInfoVarGlobal>
            """;
        var colors = new VehiclePaintReader().ReadColors(Encoding.UTF8.GetBytes(xml));
        Assert.Equal(3, colors.Count);
        Assert.Equal("Metallic Black", colors[0].Name);
        Assert.Equal(VehicleFinish.Metallic, colors[0].Finish);
        Assert.Equal(new Vector3(0x0A, 0x14, 0x28) / 255f, colors[0].Color);
        Assert.Equal("Anodized Red", colors[1].Name);
        Assert.Equal(VehicleFinish.Chameleon, colors[1].Finish);
        Assert.Equal("vehicle_paint_ramp_anod001", colors[1].RampTexture);
        Assert.Equal(VehicleFinish.Matte, colors[2].Finish);
    }

    [Fact]
    public void ReadsTheColourCombinationsOfAnXmlCarvariations()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CVehicleModelInfoVariation>
              <variationData>
                <Item>
                  <modelName>tornado6</modelName>
                  <colors>
                    <Item>
                      <indices content="char_array">
                        22
                        0
                        4
                        22
                      </indices>
                    </Item>
                    <Item>
                      <indices content="char_array">25 0 0 25 3 7</indices>
                    </Item>
                  </colors>
                  <kits>
                    <Item>196_tornado6_modkit</Item>
                  </kits>
                  <plateProbabilities>
                    <Probabilities>
                      <Item>
                        <Name>Blue Plate</Name>
                        <Value value="40" />
                      </Item>
                    </Probabilities>
                  </plateProbabilities>
                  <lightSettings value="104" />
                  <sirenSettings value="0" />
                </Item>
              </variationData>
            </CVehicleModelInfoVariation>
            """;
        var vehicles = new VehiclePaintReader().ReadVariations(Encoding.UTF8.GetBytes(xml));
        var (hash, variation) = Assert.Single(vehicles);
        Assert.Equal(Joaat.Hash("tornado6"), hash);
        Assert.Equal(2, variation.ColorSets.Count);
        Assert.Equal([22, 0, 4, 22], variation.ColorSets[0].Indices);
        Assert.Equal(-1, variation.ColorSets[0][4]);
        Assert.Equal(7, variation.ColorSets[1][5]);
        Assert.Equal(["196_tornado6_modkit"], variation.Kits);
        Assert.Equal(("Blue Plate", 40), Assert.Single(variation.Plates));
        Assert.Equal(104, variation.LightSettings);
    }

    [Fact]
    public void AFileWithoutColoursGivesAnEmptyPalette()
    {
        var colors = new VehiclePaintReader().ReadColors(Encoding.UTF8.GetBytes("<CVehicleModelInfoVarGlobal><Kits /></CVehicleModelInfoVarGlobal>"));
        Assert.Empty(colors);
    }
}
