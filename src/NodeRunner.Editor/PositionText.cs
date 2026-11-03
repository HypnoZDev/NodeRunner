using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

/// <summary>Language of the positions copied for scripts.</summary>
public enum CopyFormat
{
    /// <summary>FiveM Lua scripts: vector3(x, y, z), vector4(x, y, z, heading).</summary>
    Lua,
    /// <summary>JSON: {"x": ..., "y": ..., "z": ...} (config files, web tools).</summary>
    Json,
    /// <summary>C# scripts (FiveM C#, ScriptHookVDotNet): new Vector3(x, y, z).</summary>
    CSharp,
}

/// <summary>One object for a copied list: its type name, position and heading (degrees, 0 to 360).</summary>
public readonly record struct PositionItem(string Name, Vector3 Position, float Heading);

/// <summary>Positions written as scripts expect them, in the chosen <see cref="CopyFormat"/>; 2 decimals.</summary>
public static class PositionText
{
    public static string FormatName(CopyFormat format) => format switch
    {
        CopyFormat.Json => "JSON",
        CopyFormat.CSharp => "C#",
        _ => "Lua",
    };

    /// <summary>A position, with the heading if given (vector4 in Lua).</summary>
    public static string Position(Vector3 p, float? heading, CopyFormat format) => format switch
    {
        CopyFormat.Json => heading is { } h
            ? Invariant($"{{\"x\": {p.X:0.00}, \"y\": {p.Y:0.00}, \"z\": {p.Z:0.00}, \"heading\": {h:0.00}}}")
            : Invariant($"{{\"x\": {p.X:0.00}, \"y\": {p.Y:0.00}, \"z\": {p.Z:0.00}}}"),
        CopyFormat.CSharp => heading is { } h
            ? Invariant($"new Vector4({p.X:0.00}f, {p.Y:0.00}f, {p.Z:0.00}f, {h:0.00}f)")
            : Invariant($"new Vector3({p.X:0.00}f, {p.Y:0.00}f, {p.Z:0.00}f)"),
        _ => heading is { } h
            ? Invariant($"vector4({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}, {h:0.00})")
            : Invariant($"vector3({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})"),
    };

    /// <summary>
    /// Several objects, each with its type name and its position and heading: a Lua table, a JSON array or a C# array,
    /// ready to paste in a script.
    /// </summary>
    public static string List(IEnumerable<PositionItem> items, CopyFormat format)
    {
        var lines = items.Select(i => format switch
        {
            CopyFormat.Json => Invariant(
                $"  {{\"model\": \"{i.Name}\", \"x\": {i.Position.X:0.00}, \"y\": {i.Position.Y:0.00}, \"z\": {i.Position.Z:0.00}, \"heading\": {i.Heading:0.00}}}"),
            CopyFormat.CSharp => $"    {Position(i.Position, i.Heading, format)}, // {i.Name}",
            _ => $"    {{ model = '{i.Name}', coords = {Position(i.Position, i.Heading, format)} }},",
        }).ToList();

        var text = new StringBuilder();
        switch (format)
        {
            case CopyFormat.Json:
                text.Append("[\n").AppendJoin(",\n", lines).Append("\n]");
                break;
            case CopyFormat.CSharp:
                text.Append("new[]\n{\n").AppendJoin("\n", lines).Append("\n}");
                break;
            default:
                text.Append("{\n").AppendJoin("\n", lines).Append("\n}");
                break;
        }
        return text.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
