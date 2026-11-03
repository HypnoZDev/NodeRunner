using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

/// <summary>An entity set to activate: its name when known (the game files only store its hash).</summary>
public readonly record struct EntitySetItem(uint Hash, string? Name);

/// <summary>
/// Script lines that show entity sets of an interior in FiveM: find the interior at a point inside it, activate each
/// set, then refresh the interior. The natives take the set's name: a set whose name is unknown is written as a
/// commented line (the user can name it in data/strings.txt).
/// </summary>
public static class EntitySetScript
{
    /// <param name="interiorName">Name of the interior type, written as a comment.</param>
    /// <param name="inside">A point inside the interior (GetInteriorAtCoords).</param>
    /// <param name="format">C# for FiveM C# scripts, Lua otherwise (JSON has no script form).</param>
    public static string Activate(string interiorName, Vector3 inside, IReadOnlyList<EntitySetItem> sets, CopyFormat format)
    {
        bool cs = format == CopyFormat.CSharp;
        string comment = cs ? "//" : "--";
        var text = new StringBuilder();
        text.Append(Invariant($"{comment} {interiorName}\n"));
        text.Append(cs
            ? Invariant($"int interior = API.GetInteriorAtCoords({inside.X:0.00}f, {inside.Y:0.00}f, {inside.Z:0.00}f);\n")
            : Invariant($"local interior = GetInteriorAtCoords({inside.X:0.00}, {inside.Y:0.00}, {inside.Z:0.00})\n"));
        foreach (var set in sets)
        {
            if (set.Name is { } name)
                text.Append(cs ? $"API.ActivateInteriorEntitySet(interior, \"{name}\");\n" : $"ActivateInteriorEntitySet(interior, '{name}')\n");
            else
                text.Append($"{comment} nom inconnu (0x{set.Hash:X8}) : à ajouter dans data/strings.txt\n");
        }
        text.Append(cs ? "API.RefreshInterior(interior);" : "RefreshInterior(interior)");
        return text.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
