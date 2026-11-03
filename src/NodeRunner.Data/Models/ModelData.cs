using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Models;

/// <summary>
/// The 3D shape of an object (from a .ydr, a .ydd entry or a .yft), at its highest level of detail.
/// Positions are in model space: an entity's transform places it in the world.
/// </summary>
public sealed class ModelData
{
    public required uint NameHash { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public required IReadOnlyList<ModelMesh> Meshes { get; init; }

    /// <summary>Textures stored in the model file itself, keyed by name hash (searched before the .ytd files).</summary>
    public IReadOnlyDictionary<uint, TextureData> EmbeddedTextures { get; init; } = new Dictionary<uint, TextureData>();

    public int TriangleCount => Meshes.Sum(m => m.Indices.Length / 3);
}

/// <summary>A part of a model drawn with one material: triangles with positions, normals and texture coordinates.</summary>
public sealed class ModelMesh
{
    public required Vector3[] Positions { get; init; }

    /// <summary>Same length as <see cref="Positions"/>; zero vectors if the file has no normals.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>Same length as <see cref="Positions"/>; zero if the file has no texture coordinates.</summary>
    public required Vector2[] TexCoords { get; init; }

    /// <summary>
    /// First vertex colour (RGBA, 0 to 1), same length as <see cref="Positions"/>; white if the file has none.
    /// Its alpha fades translucent meshes (decals) in and out; its colour is not an albedo.
    /// </summary>
    public required Vector4[] Colors { get; init; }

    /// <summary>Three indices per triangle.</summary>
    public required ushort[] Indices { get; init; }

    /// <summary>Name of the main (diffuse) texture, lower case, or null.</summary>
    public string? DiffuseTextureName { get; init; }

    /// <summary>Hash of <see cref="DiffuseTextureName"/>, 0 if none.</summary>
    public uint DiffuseTextureHash => DiffuseTextureName is null ? 0 : Joaat.Hash(DiffuseTextureName);

    public MeshBlendMode BlendMode { get; init; }

    public MeshEmission Emission { get; init; }

    /// <summary>
    /// The colour comes from <see cref="Colors"/>, not from a texture (shaders cpv_only: e.g. the dark heart of bushes).
    /// </summary>
    public bool VertexColored { get; init; }

    /// <summary>Blending of the ground layers (terrain shaders only), null for other meshes.</summary>
    public TerrainBlend? Terrain { get; init; }

    /// <summary>
    /// Every texture the mesh uses: <see cref="DiffuseTextureName"/> first, then for terrain the layers 1 to 3
    /// and the blend mask (<see cref="TerrainBlend.TextureNames"/>). Entries may be null.
    /// </summary>
    public IReadOnlyList<string?> TextureNames => Terrain is null ? [DiffuseTextureName] : [DiffuseTextureName, .. Terrain.TextureNames];
}

/// <summary>
/// Ground made of four textures (grass, dirt, rock...) blended per pixel. The diffuse texture of the mesh is
/// layer 0. The weights come from the second vertex colour (blue: layer 0 to 1 and 2 to 3, green: layers 0-1 to 2-3),
/// a blend mask texture, or both (the first vertex colour's alpha chooses between them).
/// </summary>
public sealed class TerrainBlend
{
    /// <summary>Layers 1 to 3 (three entries, null when missing).</summary>
    public required string?[] LayerNames { get; init; }

    /// <summary>The blend mask texture (lookupSampler), or null.</summary>
    public string? MaskName { get; init; }

    public TerrainBlendMode Mode { get; init; }

    /// <summary>Second vertex colour (the blend weights), same length as the positions.</summary>
    public required Vector4[] Weights { get; init; }

    /// <summary>Texture coordinates of the mask, same length as the positions.</summary>
    public required Vector2[] MaskTexCoords { get; init; }

    /// <summary>Layers 1 to 3, then the mask.</summary>
    public IReadOnlyList<string?> TextureNames => [.. LayerNames, MaskName];
}

/// <summary>Where the weights of a <see cref="TerrainBlend"/> come from.</summary>
public enum TerrainBlendMode
{
    /// <summary>Mask where the first vertex colour's alpha is 0, vertex colour where it is 1.</summary>
    Mixed,

    /// <summary>Vertex colour only (distant ground).</summary>
    VertexOnly,

    /// <summary>Mask only (shaders terrain_cb_w_4lyr_cm*).</summary>
    MaskOnly,
}

/// <summary>Whether a mesh gives off its own light (decided by its shader in the game files).</summary>
public enum MeshEmission
{
    None,

    /// <summary>Always lit: signs, screens, lamp bulbs (shaders emissive, emissivestrong).</summary>
    Always,

    /// <summary>Lit at night only: windows of buildings, street lamps (shaders emissivenight).</summary>
    Night,
}

/// <summary>How a mesh is combined with what is behind it (decided by its shader in the game files).</summary>
public enum MeshBlendMode
{
    /// <summary>
    /// Solid. The alpha channel of the texture is not transparency here (some shaders use it as a tint mask).
    /// </summary>
    Opaque,

    /// <summary>Solid, but mostly transparent pixels of the texture are cut out (foliage, fences, grilles).</summary>
    Cutout,

    /// <summary>
    /// Semi-transparent, drawn after the solid meshes: dirt and grime decals laid on walls and ground,
    /// glass, alpha-blended details.
    /// </summary>
    Translucent,

    /// <summary>Water surface (pools, fountains, streams): drawn with the water's look, not its texture.</summary>
    Water,
}

/// <summary>Reads 3D models from the game files.</summary>
public interface IModelReader
{
    /// <summary>
    /// Reads the models of a .ydr or .yft file (one model, keyed by the file name hash)
    /// or of a .ydd drawable dictionary (every model, keyed by name hash).
    /// Returns an empty dictionary if the file does not exist.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is damaged or in an unsupported format.</exception>
    IReadOnlyDictionary<uint, ModelData> ReadModels(string path);
}
