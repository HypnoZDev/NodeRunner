using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;

namespace NodeRunner.Data.Native.Resources;

// Writes XML laid out like the exports of CodeWalker (one space per level, values in a "value" attribute), which
// Sollumz and the other tools read.
internal sealed class XmlText
{
    private readonly StringBuilder _text = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
    private int _depth;

    public override string ToString() => _text.ToString();

    private StringBuilder Line() => _text.Append(' ', _depth);

    public void Open(string name, string? attributes = null)
    {
        Line().Append('<').Append(name);
        if (attributes is not null)
            _text.Append(' ').Append(attributes);
        _text.Append(">\r\n");
        _depth++;
    }

    public void Close(string name)
    {
        _depth--;
        Line().Append("</").Append(name).Append(">\r\n");
    }

    public void Empty(string name, string? attributes = null)
    {
        Line().Append('<').Append(name);
        if (attributes is not null)
            _text.Append(' ').Append(attributes);
        _text.Append(" />\r\n");
    }

    public void Value(string name, string value) => Empty(name, $"value=\"{Escape(value)}\"");
    public void Value(string name, long value) => Value(name, value.ToString(CultureInfo.InvariantCulture));
    public void Value(string name, float value) => Value(name, Number(value));
    public void Value(string name, bool value) => Value(name, value ? "true" : "false");

    // Text content, or an empty element when there is none.
    public void Text(string name, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Empty(name);
            return;
        }
        Line().Append('<').Append(name).Append('>').Append(Escape(text)).Append("</").Append(name).Append(">\r\n");
    }

    public void Vector(string name, Vector3 v) => Empty(name, $"x=\"{Number(v.X)}\" y=\"{Number(v.Y)}\" z=\"{Number(v.Z)}\"");
    public void Vector(string name, Vector4 v) =>
        Empty(name, $"x=\"{Number(v.X)}\" y=\"{Number(v.Y)}\" z=\"{Number(v.Z)}\" w=\"{Number(v.W)}\"");
    public void Vector(string name, Vector2 v) => Empty(name, $"x=\"{Number(v.X)}\" y=\"{Number(v.Y)}\"");

    // Raw lines inside an element (lists of numbers), each indented one level deeper.
    public void Lines(string name, string? attributes, IEnumerable<string> lines)
    {
        Open(name, attributes);
        foreach (string line in lines)
            Line().Append(line).Append("\r\n");
        Close(name);
    }

    public static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}
