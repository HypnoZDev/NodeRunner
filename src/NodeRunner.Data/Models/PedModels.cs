using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Models;

// A ped (or animal) put together from the parts of its drawable dictionary (.ydd): one drawable per component, named
// like head_000_r or uppr_001_u, as the game dresses a ped when none is chosen.
public static class PedModels
{
    // Components in the game's order.
    private static readonly string[] Components = ["head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib"];

    // Components whose drawable changes from one ped to the next; the others keep their first one (some of them only
    // go with certain clothes).
    private static readonly HashSet<string> Varied = ["head", "hair", "uppr", "lowr"];

    private const int MaxDrawables = 64;

    // The parts of the ped: variant picks among the drawables of the varied components (the same variant gives the same
    // ped). Null when the dictionary holds no named component.
    public static ModelData? Compose(IReadOnlyDictionary<uint, ModelData> parts, uint pedHash, int variant) =>
        Compose(parts.ContainsKey, parts.GetValueOrDefault, pedHash, variant);

    // Same with parts read on demand (has: whether a part is there, by name hash; get: reads it); without variant, the
    // first drawable of each component, and only those are read.
    public static ModelData? Compose(Func<uint, bool> has, Func<uint, ModelData?> get, uint pedHash, int? variant)
    {
        var chosen = new List<ModelData>();
        string? skin = null;
        foreach (string component in Components)
        {
            // _u: any skin colour; _r and _m: textures per skin colour.
            var drawables = new List<(uint Hash, bool PerRace)>();
            for (int n = 0; n < MaxDrawables; n++)
            {
                if (has(Joaat.Hash($"{component}_{n:000}_u")))
                    drawables.Add((Joaat.Hash($"{component}_{n:000}_u"), false));
                else if (has(Joaat.Hash($"{component}_{n:000}_r")))
                    drawables.Add((Joaat.Hash($"{component}_{n:000}_r"), true));
                else if (has(Joaat.Hash($"{component}_{n:000}_m")))
                    drawables.Add((Joaat.Hash($"{component}_{n:000}_m"), true));
            }
            if (drawables.Count == 0)
                continue;
            if (variant is null || !Varied.Contains(component))
            {
                drawables = [drawables[0]];
            }
            else if (skin is not null && drawables.Where(d => d.PerRace && get(d.Hash) is { } m && Skin(m) == skin).ToList() is { Count: > 0 } sameSkin)
            {
                // Parts made for one skin colour match the head's.
                drawables = [.. sameSkin, .. drawables.Where(d => !d.PerRace)];
            }
            var pick = drawables[variant is { } seed ? (int)(Mix((uint)seed, Joaat.Hash(component)) % (uint)drawables.Count) : 0];
            if (get(pick.Hash) is not { } model)
                continue;
            chosen.Add(model);
            if (component == "head")
                skin = Skin(model);
        }
        if (chosen.Count == 0)
            return null;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in chosen.SelectMany(m => m.Meshes))
        {
            foreach (var p in mesh.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        var embedded = new Dictionary<uint, TextureData>();
        foreach (var part in chosen)
        {
            foreach (var (hash, texture) in part.EmbeddedTextures)
                embedded.TryAdd(hash, texture);
        }
        return new ModelData
        {
            NameHash = pedHash,
            BoundsMin = min.X <= max.X ? min : Vector3.Zero,
            BoundsMax = min.X <= max.X ? max : Vector3.Zero,
            Meshes = chosen.SelectMany(m => m.Meshes).ToList(),
            EmbeddedTextures = embedded,
        };
    }

    // Skin colour of a part, from the end of its texture name (head_diff_001_a_bla: "bla"), null for "uni".
    private static string? Skin(ModelData model)
    {
        string? name = model.Meshes.Select(m => m.DiffuseTextureName).FirstOrDefault(n => n is not null);
        if (name is null || name.LastIndexOf('_') is var at && at < 0)
            return null;
        string skin = name[(at + 1)..];
        return skin == "uni" ? null : skin;
    }

    private static uint Mix(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ b;
        h ^= h >> 15;
        h *= 0x85EBCA77u;
        h ^= h >> 13;
        return h;
    }
}
