using System.Numerics;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class EntitySetScriptTests
{
    private static readonly EntitySetItem[] Sets = [new(0x594E7B83, "v_michael_d_items"), new(0x7B4AAF31, null)];

    [Fact]
    public void Writes_lua_lines_and_comments_unknown_names()
    {
        string text = EntitySetScript.Activate("v_michael", new Vector3(-802.311f, 175.056f, 72.845f), Sets, CopyFormat.Lua);

        Assert.Equal("-- v_michael\n"
            + "local interior = GetInteriorAtCoords(-802.31, 175.06, 72.85)\n"
            + "ActivateInteriorEntitySet(interior, 'v_michael_d_items')\n"
            + "-- nom inconnu (0x7B4AAF31) : à ajouter dans data/strings.txt\n"
            + "RefreshInterior(interior)", text);
    }

    [Fact]
    public void Writes_csharp_lines()
    {
        string text = EntitySetScript.Activate("v_michael", Vector3.Zero, Sets[..1], CopyFormat.CSharp);

        Assert.Equal("// v_michael\n"
            + "int interior = API.GetInteriorAtCoords(0.00f, 0.00f, 0.00f);\n"
            + "API.ActivateInteriorEntitySet(interior, \"v_michael_d_items\");\n"
            + "API.RefreshInterior(interior);", text);
    }

    [Fact]
    public void Json_falls_back_to_lua() =>
        Assert.StartsWith("-- v_michael\nlocal interior", EntitySetScript.Activate("v_michael", Vector3.Zero, Sets, CopyFormat.Json));
}
