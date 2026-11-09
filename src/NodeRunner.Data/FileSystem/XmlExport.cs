namespace NodeRunner.Data.FileSystem;

// A game file exported as XML by Sollumz or CodeWalker ("name.ytd.xml"). ReadFile reads a file next to it by relative
// path ("name/texture.dds"), or gives null when there is none.
public sealed record XmlExport(string FileName, string Text, Func<string, byte[]?> ReadFile)
{
    // "name.ytd.xml" -> "name": also the folder holding the textures exported with it.
    public string BaseName
    {
        get
        {
            string name = Path.GetFileName(FileName);
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            return Path.GetFileNameWithoutExtension(name);
        }
    }

    public static XmlExport FromText(string fileName, string text) => new(fileName, text, _ => null);
}
