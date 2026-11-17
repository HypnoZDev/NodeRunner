using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Models;

// The 3D shape of an object (from a .ydr, a .ydd entry or a .yft), at its highest level of detail.
public sealed class ModelData
{
    public required uint NameHash { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public required IReadOnlyList<ModelMesh> Meshes { get; init; }

    public IReadOnlyDictionary<uint, TextureData> EmbeddedTextures { get; init; } = new Dictionary<uint, TextureData>();

    // The bones the meshes with BoneIndices follow (peds: the skeleton of their .yft), null when none do.
    public SkeletonData? Skeleton { get; init; }

    public int TriangleCount => Meshes.Sum(m => m.Indices.Length / 3);
}

// A part of a model drawn with one material: triangles with positions, normals and texture coordinates.
public sealed class ModelMesh
{
    public required Vector3[] Positions { get; init; }

    public required Vector3[] Normals { get; init; }

    public required Vector2[] TexCoords { get; init; }

    // First vertex colour (RGBA, 0 to 1), same length as Positions; white if the file has none.
    public required Vector4[] Colors { get; init; }

    public required ushort[] Indices { get; init; }

    // Meshes that bend with a skeleton (ModelData.Skeleton): for each vertex, up to 4 bones (16 bits each, the first
    // in the lowest bits: the story characters have over 300 bones) and how much each one moves it (adding up to 1).
    // Positions are those of the default pose.
    public ulong[]? BoneIndices { get; init; }

    public Vector4[]? BoneWeights { get; init; }

    public string? DiffuseTextureName { get => _diffuseTextureName; init => _diffuseTextureName = value; }
    private string? _diffuseTextureName;

    // A copy drawn with another colour texture (a ped's clothes in another colour).
    public ModelMesh WithDiffuseTexture(string? name)
    {
        var copy = (ModelMesh)MemberwiseClone();
        copy._diffuseTextureName = name;
        return copy;
    }

    public uint DiffuseTextureHash => DiffuseTextureName is null ? 0 : Joaat.Hash(DiffuseTextureName);

    public MeshBlendMode BlendMode { get; init; }

    public MeshEmission Emission { get; init; }

    // The colour comes from Colors, not from a texture (shaders cpv_only: e.g. the dark heart of bushes).
    public bool VertexColored { get; init; }

    // Colors hold light worked out in advance (GTA IV: grey, about 0.5 in the open, 0 in nooks and under objects).
    public bool BakedLighting { get; init; }

    // GTA III, Vice City, San Andreas: Colors are the whole light of the surface (pre-lit), to which the renderer adds
    // the ambient light of the hour; NightColors (San Andreas) the same light at night, blended in by the hour.
    public bool ClassicPrelit { get; init; }

    public Vector4[]? NightColors { get; init; }

    // Shadow or grime laid over a surface by the brightness of its texture: white darkens fully, black leaves the
    // surface as it is (GTA IV "gta_decal_dirt", e.g. shadows painted on interior floors). Drawn translucent.
    public bool DarkeningDecal { get; init; }

    public TerrainBlend? Terrain { get; init; }

    // Hash of the shader's name (e.g. "vehicle_paint1").
    public uint ShaderHash { get; init; }

    // Drawn with a ped shader (skin, clothes, hair of a character): lit as the game lights characters.
    public bool IsCharacter => CharacterShaders.Contains(ShaderHash);

    private static readonly HashSet<uint> CharacterShaders = new[]
    {
        "ped", "ped_alpha", "ped_cloth", "ped_cloth_enveff", "ped_decal", "ped_decal_decoration", "ped_decal_expensive",
        "ped_decal_medals", "ped_decal_nodiff", "ped_default", "ped_default_cloth", "ped_default_cutout", "ped_default_enveff",
        "ped_default_mp", "ped_default_palette", "ped_emissive", "ped_enveff", "ped_fur", "ped_hair_cutout_alpha",
        "ped_hair_spiked", "ped_nopeddamagedecals", "ped_palette", "ped_wrinkle", "ped_wrinkle_cloth",
        "ped_wrinkle_cloth_enveff", "ped_wrinkle_cs", "ped_wrinkle_enveff",
    }.Select(Joaat.Hash).ToHashSet();

    // Every texture of the shader, by parameter name hash (DiffuseSampler, BumpSampler...).
    public IReadOnlyDictionary<uint, string> ShaderTextures { get; init; } = EmptyTextures;

    // The shader's matDiffuseColor value (vehicles: which paint colour the part takes), null if it has none.
    public Vector4? DiffuseColor { get; init; }

    private static readonly IReadOnlyDictionary<uint, string> EmptyTextures = new Dictionary<uint, string>();

    // Second colour texture multiplied over the first (grass_fur: DiffuseHfSampler, the patch's large-scale colour),
    // read with TintTexCoords.
    public string? TintTextureName { get; init; }

    public Vector2[]? TintTexCoords { get; init; }

    public IReadOnlyList<string?> TextureNames => Terrain is not null ? [DiffuseTextureName, .. Terrain.TextureNames]
        : TintTextureName is not null ? [DiffuseTextureName, TintTextureName]
        : IsCharacter ? [DiffuseTextureName, NormalTextureName, SpecularTextureName]
        : [DiffuseTextureName];

    // Shine set by the shader (specularIntensityMult, specularFalloffMult): how bright and how tight the highlights of
    // the specular map are.
    public float SpecularIntensity { get; init; } = 1f;
    public float SpecularFalloff { get; init; } = 100f;

    // Characters: the relief (normal map) and shine (specular map) textures of the shader.
    public string? NormalTextureName => ShaderTextures.GetValueOrDefault(BumpSampler);
    public string? SpecularTextureName => ShaderTextures.GetValueOrDefault(SpecSampler);

    private static readonly uint BumpSampler = Joaat.Hash("BumpSampler");
    private static readonly uint SpecSampler = Joaat.Hash("SpecSampler");
}

// Ground made of four textures (grass, dirt, rock...) blended per pixel.
public sealed class TerrainBlend
{
    public required string?[] LayerNames { get; init; }

    public string? MaskName { get; init; }

    public TerrainBlendMode Mode { get; init; }

    public required Vector4[] Weights { get; init; }

    public required Vector2[] MaskTexCoords { get; init; }

    public IReadOnlyList<string?> TextureNames => [.. LayerNames, MaskName];
}

// Where the weights of a TerrainBlend come from.
public enum TerrainBlendMode
{
    Mixed,

    VertexOnly,

    MaskOnly,

    // GTA IV: each layer laid over the previous ones by its own weight (Weights x, y, z for layers 1 to 3).
    Layered,
}

// Whether a mesh gives off its own light (decided by its shader in the game files).
public enum MeshEmission
{
    None,

    Always,

    Night,
}

// How a mesh is combined with what is behind it (decided by its shader in the game files).
public enum MeshBlendMode
{
    Opaque,

    Cutout,

    // Semi-transparent, drawn after the solid meshes: dirt and grime decals laid on walls and ground, glass,
    // alpha-blended details.
    Translucent,

    Water,
}

// Reads 3D models from the game files.
public interface IModelReader
{
    // Reads the models of a .ydr or .yft file (one model, keyed by the file name hash) or of a .ydd drawable
    // dictionary (every model, keyed by name hash).
    IReadOnlyDictionary<uint, ModelData> ReadModels(string path);

    // Same, with the skeleton of another file for the parts attached to a bone (a ped's .ydd takes the skeleton of its
    // .yft); a .ydr given its own path keeps its bones, to be animated.
    IReadOnlyDictionary<uint, ModelData> ReadModels(string path, string? skeletonPath) => ReadModels(path);
}
