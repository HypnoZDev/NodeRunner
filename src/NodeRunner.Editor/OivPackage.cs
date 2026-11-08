using System.Xml.Linq;

namespace NodeRunner.Editor;

// An OpenIV package (.oiv: a zip with assembly.xml and a "content" folder, or the same thing unzipped). Each <add> copies
// content\<source> into a game archive under the target's name; a FiveM "stream" folder replaces the game's file of
// that name the same way, so only the file names are kept.
public sealed class OivPackage
{
    public const string AssemblyFileName = "assembly.xml";
    public const string ContentFolderName = "content";

    public sealed record Entry(string Name, string Source);

    public string? Name { get; private init; }

    public string? Author { get; private init; }

    public string? Version { get; private init; }

    public List<Entry> Entries { get; } = [];

    // Commands the import cannot turn into streamed files (whole .rpf archives, xml / text edits, deletions).
    public List<string> Ignored { get; } = [];

    public static bool IsPackageFolder(string folder) => File.Exists(Path.Combine(folder, AssemblyFileName));

    public static OivPackage Parse(string xml, Func<string, bool> isStreamed)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException(L($"{AssemblyFileName} illisible : {ex.Message}"), ex);
        }
        var root = document.Root ?? throw new InvalidDataException(L($"{AssemblyFileName} est vide."));
        var metadata = root.Element("metadata");
        var version = metadata?.Element("version");
        var package = new OivPackage
        {
            Name = Text(metadata?.Element("name")),
            Author = Text(metadata?.Element("author")?.Element("displayName")),
            Version = version is null ? null : string.Join(".", new[] { Text(version.Element("major")), Text(version.Element("minor")) }.OfType<string>()),
        };
        if (root.Element("content") is { } content)
            package.Read(content, isStreamed);
        return package;
    }

    private void Read(XElement container, Func<string, bool> isStreamed)
    {
        foreach (var element in container.Elements())
        {
            string kind = element.Name.LocalName;
            if (kind == "archive")
            {
                Read(element, isStreamed);
                continue;
            }
            string target = element.Value.Trim();
            string name = Path.GetFileName(target.Replace('/', '\\').TrimEnd('\\'));
            if (kind == "add" && element.Attribute("source")?.Value is { Length: > 0 } source && name.Length > 0)
            {
                if (isStreamed(name))
                    Entries.Add(new Entry(name, source.Replace('/', '\\').TrimStart('\\')));
                else
                    Ignored.Add(name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) ? L($"archive {name} (pack DLC)") : name);
            }
            else
            {
                string path = element.Attribute("path")?.Value is { Length: > 0 } p ? Path.GetFileName(p.TrimEnd('\\', '/')) : name;
                Ignored.Add(kind switch
                {
                    "xml" or "text" => L($"modification de {path}"),
                    "delete" => L($"suppression de {name}"),
                    _ => $"<{kind}> {path}",
                });
            }
        }
    }

    private static string? Text(XElement? element) => element?.Value.Trim() is { Length: > 0 } text ? text : null;

    // Zip entry paths compared with '/' and without a leading slash (OpenIV writes either slash).
    public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
