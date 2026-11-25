using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Automatic LODs: a lighter model for each big object of the project, drawn in its place from afar. The LOD starts
// from the object's own model as XML (Sollumz form): same shaders, textures and vertex format, fewer triangles
// (ModelSimplifier), no collision, lights or lower levels of detail.
public static class LodModel
{
    // Objects smaller than this (diagonal of the box, in metres) keep no LOD, as in the game (benches, bins...).
    public const float MinSize = 10f;

    public const float Ratio = 0.25f;

    public static bool Wanted(Archetype? type) => type is { Kind: not ArchetypeKind.Interior, IsShadowOrReflectionProxy: false }
        && type.AssetType is ArchetypeAssetType.Drawable or ArchetypeAssetType.Fragment
        && Vector3.Distance(type.BoundsMin, type.BoundsMax) >= MinSize;

    // Name of the LOD type made for an object type (name, or only its hash).
    public static string Name(string? name, uint hash) =>
        name is { Length: > 0 and <= 50 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? name.ToLowerInvariant() + "_nrlod" : $"nrlod_{hash:x8}";

    // Up to where the LOD is drawn: well beyond its object's own distance.
    public static float LodDistance(float objectDistance) => Math.Clamp(objectDistance * 4f, 1000f, 3000f);

    // The model's XML (a <Drawable>, or a <Fragment> whose <Drawable> is taken) turned into its LOD's, named name.
    public static string DrawableXml(string modelXml, string name, float ratio = Ratio, float maxError = 0.02f, float smallPart = 0.03f)
    {
        var root = XDocument.Parse(modelXml).Root ?? throw new InvalidDataException("XML vide.");
        var source = root.Name.LocalName == "Drawable" ? root
            : root.Element("Drawable") ?? throw new InvalidDataException("Balise <Drawable> introuvable.");
        var d = new XElement(source) { Name = "Drawable" };
        SetValue(d, "Name", name);
        foreach (var removed in new[] { "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow", "Bounds", "Lights", "Joints" })
            d.Element(removed)?.Remove();
        foreach (var level in new[] { "High", "Med", "Low", "Vlow" })
        {
            SetAttribute(d, "LodDist" + level, "9998");
            if (level != "High")
                SetAttribute(d, "Flags" + level, "0");
        }

        var high = d.Element("DrawableModelsHigh") ?? throw new InvalidDataException("Le modèle n'a pas de géométrie.");
        var original = new XElement(high);
        float size = Vector3.Distance(Vec3(d.Element("BoundingBoxMin")), Vec3(d.Element("BoundingBoxMax")));
        Simplify(high, size, ratio, maxError, smallPart);
        // Only small parts: the model keeps them all, simplified.
        if (!high.Descendants("Geometries").Elements("Item").Any())
        {
            high.ReplaceWith(original);
            Simplify(original, size, ratio, maxError, 0f);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), d).Declaration + "\n" + d;
    }

    private static void Simplify(XElement models, float size, float ratio, float maxError, float smallPart)
    {
        double limit = Math.Pow(Math.Max(size, 0.01f) * maxError, 2);
        foreach (var model in models.Elements("Item").ToList())
        {
            foreach (var geometry in model.Element("Geometries")?.Elements("Item").ToList() ?? [])
            {
                if (!SimplifyGeometry(geometry, size * smallPart, ratio, limit))
                    geometry.Remove();
            }
            if (model.Element("Geometries")?.Elements("Item").Any() != true)
                model.Remove();
        }
    }

    // False when the geometry is too small to be kept.
    private static bool SimplifyGeometry(XElement geometry, float minSize, float ratio, double limit)
    {
        var buffer = geometry.Element("VertexBuffer");
        var data = buffer?.Element("Data");
        var indexData = geometry.Element("IndexBuffer")?.Element("Data");
        if (buffer is null || data is null || indexData is null)
            return true;
        // Positions come first in each row; a geometry without them is kept as it is.
        if (buffer.Element("Layout")?.Elements().FirstOrDefault()?.Name.LocalName != "Position")
            return true;
        var rows = Rows(data.Value);
        var rows2 = buffer.Element("Data2") is { } second ? Rows(second.Value) : null;
        var positions = rows.Select(r => ParsePosition(r)).ToArray();
        var indices = indexData.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (indices.Length < 3 || indices.Any(i => i < 0 || i >= positions.Length))
            return indices.Length >= 3;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int i in indices)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        if (Vector3.Distance(min, max) < minSize)
            return false;

        int target = Math.Max(4, (int)MathF.Round(indices.Length / 3 * ratio));
        var (kept, newIndices, moved) = ModelSimplifier.Simplify(positions, indices, target, limit);
        if (newIndices.Length < 3)
            return false;
        string Row(string[] all, int v) => moved[v] == positions[v] ? all[v] : ReplacePosition(all[v], moved[v]);
        data.Value = "\n" + string.Join("\n", kept.Select(v => Row(rows, v))) + "\n";
        if (rows2 is not null && rows2.Length == rows.Length)
            buffer.Element("Data2")!.Value = "\n" + string.Join("\n", kept.Select(v => Row(rows2, v))) + "\n";
        else
            buffer.Element("Data2")?.Remove();
        indexData.Value = "\n" + string.Join("\n", newIndices.Chunk(24).Select(row => string.Join(" ", row))) + "\n";
        return true;
    }

    private static string[] Rows(string text) =>
        text.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToArray();

    private static Vector3 ParsePosition(string row)
    {
        var values = row.Split("   ")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length < 3)
            throw new InvalidDataException("Sommet illisible dans le modèle.");
        return new Vector3(Number(values[0]), Number(values[1]), Number(values[2]));
    }

    private static string ReplacePosition(string row, Vector3 p)
    {
        int end = row.IndexOf("   ", StringComparison.Ordinal);
        string position = $"{F(p.X)} {F(p.Y)} {F(p.Z)}";
        return end < 0 ? position : position + row[end..];
    }

    private static float Number(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);

    private static Vector3 Vec3(XElement? e) => e is null ? Vector3.Zero
        : new Vector3(Attr(e, "x"), Attr(e, "y"), Attr(e, "z"));

    private static float Attr(XElement e, string name) =>
        (string?)e.Attribute(name) is { } text && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    private static void SetValue(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } e)
            e.Value = value;
        else
            parent.AddFirst(new XElement(name, value));
    }

    private static void SetAttribute(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } e)
            e.SetAttributeValue("value", value);
    }

    // One LOD type made for an object type.
    public sealed record LodType(string Name, string TextureDictionary, float LodDistance, Vector3 BoxMin, Vector3 BoxMax);

    // The .ytyp (XML form) declaring the LOD types, named name.
    public static string TypesXml(string name, IEnumerable<LodType> types)
    {
        static string V(string tag, Vector3 v) => $"<{tag} x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" />";
        static string T(string text) => SecurityElement.Escape(text);
        var items = new StringBuilder();
        foreach (var t in types)
        {
            var center = (t.BoxMin + t.BoxMax) * 0.5f;
            items.Append($"""
                  <Item type="CBaseArchetypeDef">
                   <lodDist value="{F(t.LodDistance)}" />
                   <flags value="0" />
                   <specialAttribute value="0" />
                   {V("bbMin", t.BoxMin)}
                   {V("bbMax", t.BoxMax)}
                   {V("bsCentre", center)}
                   <bsRadius value="{F(Vector3.Distance(t.BoxMin, t.BoxMax) * 0.5f)}" />
                   <hdTextureDist value="5" />
                   <name>{T(t.Name)}</name>
                   <textureDictionary>{T(t.TextureDictionary)}</textureDictionary>
                   <clipDictionary />
                   <drawableDictionary />
                   <physicsDictionary />
                   <assetType>ASSET_TYPE_DRAWABLE</assetType>
                   <assetName>{T(t.Name)}</assetName>
                   <extensions />
                  </Item>

                """);
        }
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <CMapTypes>
             <extensions />
             <archetypes>
            {items} </archetypes>
             <name>{T(name)}</name>
             <dependencies />
             <compositeEntityTypes />
            </CMapTypes>
            """;
    }
}
