using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.Data.Native.Models;

public static partial class YdrXml
{
    // A .ydr of the game as XML, its embedded textures as .dds in the folder named after it. Original release only.
    public static XmlConversion Export(string fileName, ReadOnlySpan<byte> data, ResourceHeader header, ShaderNameCatalog names)
    {
        RequireOriginal(header);
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        var output = new DrawableXml.Output(names, YtdXml.XmlBaseName(fileName));
        var xml = new XmlText();
        xml.Open("Drawable");
        DrawableXml.WriteDrawable(xml, res, 0, output, withName: true);
        DrawableXml.WriteBoundsAndLights(xml, res, 0);
        xml.Close("Drawable");
        return new XmlConversion(fileName + ".xml", xml.ToString(), output.Files);
    }

    internal static void RequireOriginal(ResourceHeader header)
    {
        if (header.Version is DrawableReader.EnhancedDrawableVersion or DrawableReader.EnhancedFragmentVersion)
            throw new InvalidDataException("Les modèles de GTA V Enhanced ne peuvent pas encore être exportés en XML.");
    }
}
