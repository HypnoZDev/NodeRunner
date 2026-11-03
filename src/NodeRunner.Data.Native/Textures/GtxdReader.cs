using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// reader for the texture dictionary relationships (root element CMapParentTxds): gtxd.ymt in the base game is binary
// (RBF), gtxd.meta files are XML.
public sealed class GtxdReader : ITextureParentReader
{
    public IReadOnlyList<TextureParentLink> ReadParents(byte[] data)
    {
        var links = new List<TextureParentLink>();
        if (RbfDocument.IsRbf(data))
        {
            var root = RbfDocument.Parse(data);
            foreach (var item in root.Child("txdRelationships")?.Children ?? [])
                Add(links, item.Child("child")?.Text, item.Child("parent")?.Text);
            return links;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
        }
        foreach (var item in document.Root?.Element("txdRelationships")?.Elements() ?? [])
            Add(links, item.Element("child")?.Value, item.Element("parent")?.Value);
        return links;
    }

    private static void Add(List<TextureParentLink> links, string? child, string? parent)
    {
        child = child?.Trim();
        parent = parent?.Trim();
        if (!string.IsNullOrEmpty(child) && !string.IsNullOrEmpty(parent))
            links.Add(new TextureParentLink(child, parent));
    }
}
