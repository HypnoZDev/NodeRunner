using System.Globalization;
using System.Numerics;
using System.Security;

namespace NodeRunner.Editor;

// A changed model saved as a new object type beside the original one: its own model file, and a .ytyp holding a copy
// of the original's type under the new name (same textures, collision, flags and draw distance; box of the model as it
// is now). The game's object keeps its model everywhere it is placed.
public static class PropCopy
{
    public static bool IsValidName(string name) => InteriorCreation.IsValidName(name);

    public static string SuggestName(string source, Func<string, bool> isFree) =>
        InteriorCreation.SuggestName(source, isFree, "_copy");

    // Type names written in the .ytyp: a name, "hash_XXXXXXXX" when only its hash is known, empty for none.
    public static string TypeName(uint hash, string? name) => hash == 0 ? "" : name ?? $"hash_{hash:X8}";

    // fragment: the model is a .yft (an object that breaks), else a .ydr.
    public static string TypesXml(string name, bool fragment, string textureDictionary, string physicsDictionary, uint flags,
        float lodDistance, Vector3 boxMin, Vector3 boxMax)
    {
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        static string V(string tag, Vector3 v) => $"<{tag} x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\" />";
        static string T(string text) => SecurityElement.Escape(text);
        var center = (boxMin + boxMax) * 0.5f;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <CMapTypes>
             <extensions />
             <archetypes>
              <Item type="CBaseArchetypeDef">
               <lodDist value="{F(lodDistance)}" />
               <flags value="{flags.ToString(CultureInfo.InvariantCulture)}" />
               <specialAttribute value="0" />
               {V("bbMin", boxMin)}
               {V("bbMax", boxMax)}
               {V("bsCentre", center)}
               <bsRadius value="{F(Vector3.Distance(boxMin, boxMax) * 0.5f)}" />
               <hdTextureDist value="{F(Math.Min(lodDistance, 60f))}" />
               <name>{T(name)}</name>
               <textureDictionary>{T(textureDictionary)}</textureDictionary>
               <clipDictionary />
               <drawableDictionary />
               <physicsDictionary>{T(physicsDictionary)}</physicsDictionary>
               <assetType>{(fragment ? "ASSET_TYPE_FRAGMENT" : "ASSET_TYPE_DRAWABLE")}</assetType>
               <assetName>{T(name)}</assetName>
               <extensions />
              </Item>
             </archetypes>
             <name>{T(name)}</name>
             <dependencies />
             <compositeEntityTypes />
            </CMapTypes>
            """;
    }
}
