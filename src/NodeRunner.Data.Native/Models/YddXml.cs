using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.Data.Native.Models;

// Drawable dictionaries (.ydd) of the game as the XML read by Sollumz and CodeWalker: one <Item> per drawable.
public static class YddXml
{
    // modelNames: the names of models known elsewhere (the dictionary only keeps their hashes).
    public static XmlConversion Export(string fileName, ReadOnlySpan<byte> data, ResourceHeader header, ShaderNameCatalog names,
        Func<uint, string?>? modelNames = null)
    {
        YdrXml.RequireOriginal(header);
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        var output = new DrawableXml.Output(names, YtdXml.XmlBaseName(fileName));
        var xml = new XmlText();
        xml.Open("DrawableDictionary");
        int count = res.UInt16(0x38);
        if (count > 0 && res.UInt64(0x30) is not 0 and var pointer)
        {
            int list = res.ResolveArray(pointer, count, 8, "liste des modèles");
            int hashes = res.UInt16(0x28) >= count && res.UInt64(0x20) is not 0 and var h ? res.ResolveArray(h, count, 4, "noms des modèles") : -1;
            for (int i = 0; i < count; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                    continue;
                int d = res.ResolveArray(res.UInt64(list + i * 8), 1, 0xD0, "modèle");
                xml.Open("Item");
                uint hash = hashes < 0 ? 0 : res.UInt32(hashes + i * 4);
                xml.Text("Name", hash == 0 ? null : modelNames?.Invoke(hash) ?? $"hash_{hash:X8}");
                DrawableXml.WriteDrawable(xml, res, d, output, withName: false);
                DrawableXml.WriteBoundsAndLights(xml, res, d);
                xml.Close("Item");
            }
        }
        xml.Close("DrawableDictionary");
        return new XmlConversion(fileName + ".xml", xml.ToString(), output.Files);
    }
}
