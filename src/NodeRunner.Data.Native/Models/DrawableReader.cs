using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Models;

// Reads the game's models: .ydr (one drawable), .ydd (a dictionary of drawables) and .yft (fragments: vehicles and
// breakable objects, whose wheels are separate drawables).
public sealed class DrawableReader : IModelReader
{
    private readonly IGameFileSystem _files;

    public DrawableReader(IGameFileSystem files)
    {
        _files = files;
    }

    public IReadOnlyDictionary<uint, ModelData> ReadModels(string path)
    {
        if (_files.ReadFile(path) is not { } data)
            return new Dictionary<uint, ModelData>();
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        try
        {
            return Read(data, header, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    public static Dictionary<uint, ModelData> Read(ReadOnlySpan<byte> data, ResourceHeader header, string name, string extension)
    {
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        uint fileHash = Joaat.Hash(name);
        var models = new Dictionary<uint, ModelData>();
        switch (extension.ToLowerInvariant())
        {
            case ".ydr":
                if (Convert(res, fileHash, 0) is { } drawable)
                    models[fileHash] = drawable;
                break;

            case ".yft":
                if (Convert(res, fileHash, Block(res, res.UInt64(FragDrawablePointer), DrawableLength)) is { } body)
                    models[fileHash] = AddWheels(res, body);
                break;

            case ".ydd":
                // Dictionary: name hashes and drawables side by side (a repeated hash keeps the last drawable).
                int hashCount = res.UInt16(0x28);
                int drawableCount = res.UInt16(0x38);
                int hashes = res.ResolveArray(res.UInt64(0x20), hashCount, 4, "noms des modèles");
                int pointers = res.ResolveArray(res.UInt64(0x30), drawableCount, 8, "modèles");
                var byHash = new Dictionary<uint, int>();
                for (int i = 0; i < hashCount && i < drawableCount; i++)
                    byHash[res.UInt32(hashes + i * 4)] = Block(res, res.UInt64(pointers + i * 8), DrawableLength);
                foreach (var (hash, offset) in byHash)
                {
                    if (Convert(res, hash, offset) is { } model)
                        models[hash] = model;
                }
                break;

            default:
                throw new InvalidDataException($"Type de modèle non géré : {name}{extension}");
        }
        return models;
    }

    private static int Block(ResourceData res, ulong pointer, int length) =>
        pointer == 0 ? -1 : res.ResolveArray(pointer, 1, length, "bloc");

    // Drawable (rmcDrawable), at least 0xA8 bytes.
    private const int DrawableLength = 0xA8;
    private const int ShaderGroupPointer = 0x10;
    private const int BoundsMinOffset = 0x30;
    private const int BoundsMaxOffset = 0x40;
    private const int ModelsHigh = 0x50; // then medium, low and very low, 8 bytes apart
    private const int ModelsPointer = 0xA0;

    // Shader group: its own textures, then the shaders.
    private const int EmbeddedTexturesPointer = 0x08;
    private const int ShadersPointer = 0x10;
    private const int ShadersCount = 0x18;

    // Model: geometries and the shader of each.
    private const int GeometriesPointer = 0x08;
    private const int GeometriesCount = 0x10;
    private const int ShaderMappingPointer = 0x20;

    private static ModelData? Convert(ResourceData res, uint hash, int d)
    {
        if (d < 0 || Models(res, d) is not { } models)
            return null;

        var shaders = Shaders(res, d);
        var meshes = new List<ModelMesh>();
        foreach (int model in models)
        {
            if (model < 0)
                continue;
            int count = res.UInt16(model + GeometriesCount);
            int geometries = res.ResolveArray(res.UInt64(model + GeometriesPointer), count, 8, "géométries");
            ulong mappingPointer = res.UInt64(model + ShaderMappingPointer);
            int mapping = mappingPointer == 0 ? -1 : res.ResolveArray(mappingPointer, count, 2, "shaders des géométries");
            for (int i = 0; i < count; i++)
            {
                int geometry = Block(res, res.UInt64(geometries + i * 8), GeometryLength);
                if (geometry < 0)
                    continue;
                int shaderId = mapping >= 0 ? res.UInt16(mapping + i * 2) : 0;
                if (ConvertGeometry(res, geometry, shaderId < shaders.Count ? shaders[shaderId] : null, shaders) is { } mesh)
                    meshes.Add(mesh);
            }
        }

        int group = Block(res, res.UInt64(d + ShaderGroupPointer), 0x40);
        int dictionary = group < 0 ? -1 : Block(res, res.UInt64(group + EmbeddedTexturesPointer), 0x40);
        return new ModelData
        {
            NameHash = hash,
            BoundsMin = Vec3(res, d + BoundsMinOffset),
            BoundsMax = Vec3(res, d + BoundsMaxOffset),
            Meshes = meshes,
            EmbeddedTextures = dictionary >= 0 ? YtdReader.ReadDictionary(res, dictionary) : new Dictionary<uint, TextureData>(),
        };
    }

    // The models of the first level of detail present (high, medium, low, very low): a list header (pointer, count,
    // capacity) whose every slot is read.
    private static List<int>? Models(ResourceData res, int d)
    {
        // The level lists are stored together; without that block there is nothing to draw.
        if (res.UInt64(d + ModelsPointer) == 0 && res.UInt64(d + ModelsHigh) == 0)
            return null;
        for (int level = 0; level < 4; level++)
        {
            if (ModelList(res, res.UInt64(d + ModelsHigh + level * 8)) is { } list)
                return list;
        }
        return null;
    }

    private static List<int>? ModelList(ResourceData res, ulong headerPointer)
    {
        if (headerPointer == 0)
            return null;
        int header = res.ResolveArray(headerPointer, 1, 16, "niveau de détail");
        ulong pointer = res.UInt64(header);
        int capacity = res.UInt16(header + 10);
        if (pointer == 0 || capacity == 0)
            return null;
        int slots = res.ResolveArray(pointer, capacity, 8, "modèles du niveau de détail");
        var models = new List<int>(capacity);
        for (int i = 0; i < capacity; i++)
            models.Add(Block(res, res.UInt64(slots + i * 8), 0x30));
        return models;
    }

    private static int AllModelsCount(ResourceData res, int d)
    {
        if (res.UInt64(d + ModelsPointer) == 0 && res.UInt64(d + ModelsHigh) == 0)
            return 0;
        int count = 0;
        for (int level = 0; level < 4; level++)
            count += ModelList(res, res.UInt64(d + ModelsHigh + level * 8))?.Count ?? 0;
        // An extra list when the block does not start with the high level's list.
        ulong block = res.UInt64(d + ModelsPointer);
        if (block != 0 && block != res.UInt64(d + ModelsHigh))
            count += ModelList(res, block)?.Count ?? 0;
        return count;
    }

    // ---- Shaders ----

    // What the meshes need from a shader.
    private sealed record Shader(uint Name, uint FileName, byte RenderBucket, Dictionary<uint, string> Textures);

    private static List<Shader?> Shaders(ResourceData res, int d)
    {
        var shaders = new List<Shader?>();
        int group = Block(res, res.UInt64(d + ShaderGroupPointer), 0x40);
        if (group < 0)
            return shaders;
        int count = res.UInt16(group + ShadersCount);
        if (count == 0 || res.UInt64(group + ShadersPointer) == 0)
            return shaders;
        int pointers = res.ResolveArray(res.UInt64(group + ShadersPointer), count, 8, "shaders");
        for (int i = 0; i < count; i++)
        {
            int s = Block(res, res.UInt64(pointers + i * 8), 0x30);
            shaders.Add(s < 0 ? null : ReadShader(res, s));
        }
        return shaders;
    }

    // Shader (grmShaderFx): 0x00 parameters, 0x08 name hash, 0x10 parameter count, 0x11 render bucket, 0x18 hash of
    // its preset file (e.g. "trees_normal_spec.sps"). Parameters: 16 bytes each (type, pointer: type 0 = texture, N =
    // N vectors), then the vectors, then the name hash of each parameter.
    private static Shader ReadShader(ResourceData res, int s)
    {
        var textures = new Dictionary<uint, string>();
        int count = res.Byte(s + 0x10);
        ulong parametersPointer = res.UInt64(s);
        if (count > 0 && parametersPointer != 0)
        {
            int parameters = res.ResolveArray(parametersPointer, count, 16, "paramètres de shader");
            int vectors = 0;
            for (int i = 0; i < count; i++)
            {
                int type = res.Byte(parameters + i * 16);
                vectors += type == 0 ? 0 : 16 * type;
            }
            int hashes = res.ResolveArray(parametersPointer + (ulong)(count * 16 + vectors), count, 4, "noms des paramètres");
            for (int i = 0; i < count; i++)
            {
                int p = parameters + i * 16;
                ulong texture = res.UInt64(p + 8);
                if (res.Byte(p) != 0 || texture == 0)
                    continue;
                // Texture reference: its name pointer is at 0x28, as in a full texture.
                if (res.Text(res.UInt64(res.ResolveArray(texture, 1, 0x30, "texture") + 0x28), "nom de texture") is { } name)
                    textures.TryAdd(res.UInt32(hashes + i * 4), name.ToLowerInvariant());
            }
        }
        return new Shader(res.UInt32(s + 0x08), res.UInt32(s + 0x18), res.Byte(s + 0x11), textures);
    }

    // Shader parameters holding the colour texture, by priority. Terrain shaders blend several layers: the
    // first one is the colour texture, the others are in TerrainBlend.
    private static readonly uint[] ColorSamplers = [Joaat.Hash("DiffuseSampler"), Joaat.Hash("TextureSampler_layer0")];

    private static readonly uint[] TerrainLayers =
        [Joaat.Hash("TextureSampler_layer1"), Joaat.Hash("TextureSampler_layer2"), Joaat.Hash("TextureSampler_layer3")];

    private static readonly uint TerrainMask = Joaat.Hash("lookupSampler");

    // Terrain shaders taking their weights from the vertex colour only, or from the mask only.
    private static readonly uint TerrainVertexOnlyShader = Joaat.Hash("terrain_cb_w_4lyr_lod");

    private static readonly HashSet<uint> TerrainMaskOnlyShaders = new[]
    {
        "terrain_cb_w_4lyr_cm", "terrain_cb_w_4lyr_cm_tnt", "terrain_cb_w_4lyr_cm_pxm", "terrain_cb_w_4lyr_cm_pxm_tnt",
    }.Select(Joaat.Hash).ToHashSet();

    // Mirror surfaces show a live reflection in the game, not their placeholder texture: not drawn for now.
    // Simple shapes inside trees only cast the foliage's shadow in the game (white balls if drawn).
    private static readonly HashSet<uint> HiddenShaders = new[]
    {
        "mirror_default", "mirror_decal", "mirror_crack", "trees_shadow_proxy",
    }.Select(Joaat.Hash).ToHashSet();

    // Shaders without texture, coloured by the vertex colours.
    private static readonly uint VertexColorShader = Joaat.Hash("cpv_only");

    // Presets of the tree and bush shaders (hash of the file name).
    private static readonly HashSet<uint> TreePresets = new[]
    {
        "trees", "trees_camera_aligned", "trees_camera_facing", "trees_lod", "trees_lod2", "trees_lod2d", "trees_lod_tnt",
        "trees_normal", "trees_normal_diffspec", "trees_normal_diffspec_tnt", "trees_normal_spec",
        "trees_normal_spec_camera_aligned", "trees_normal_spec_camera_aligned_tnt", "trees_normal_spec_camera_facing",
        "trees_normal_spec_camera_facing_tnt", "trees_normal_spec_tnt", "trees_normal_spec_wind", "trees_shadow_proxy",
        "trees_tnt",
    }.Select(n => Joaat.Hash(n + ".sps")).ToHashSet();

    private static readonly HashSet<uint> AlwaysEmissiveShaders = new[]
    {
        "emissive", "emissive_alpha", "emissive_tnt", "emissive_alpha_tnt", "emissive_clip", "emissive_speclum",
        "emissive_additive_alpha", "emissive_additive_uv_alpha", "emissivestrong", "emissivestrong_alpha",
    }.Select(Joaat.Hash).ToHashSet();

    private static readonly HashSet<uint> NightEmissiveShaders = new[]
    {
        "emissivenight", "emissivenight_alpha", "emissivenight_geomnightonly",
    }.Select(Joaat.Hash).ToHashSet();

    // ---- Geometry ----

    // Geometry: 0x18 vertex buffer, 0x38 index buffer.
    private const int GeometryLength = 0x98;

    // Vertex components (bit index in the vertex declaration flags).
    private const int PositionComponent = 0;
    private const int NormalComponent = 3;
    private const int ColorComponent = 4;
    private const int SecondColorComponent = 5;
    private const int TexCoordComponent = 6;
    private const int SecondTexCoordComponent = 7;

    private static ModelMesh? ConvertGeometry(ResourceData res, int g, Shader? shader, List<Shader?> shaders)
    {
        // Vertex buffer: 0x08 stride, 0x10 data, 0x18 count, 0x20 second copy of the data, 0x30 declaration.
        int buffer = Block(res, res.UInt64(g + 0x18), 0x40);
        if (buffer < 0)
            return null;
        ulong dataPointer = res.UInt64(buffer + 0x10);
        if (dataPointer == 0)
            dataPointer = res.UInt64(buffer + 0x20);
        if (dataPointer == 0)
            return null;
        int declarationOffset = Block(res, res.UInt64(buffer + 0x30), 16)
            is >= 0 and var found ? found : throw new InvalidDataException("Format des sommets absent.");
        var declaration = new VertexDeclaration(res.UInt32(declarationOffset), res.UInt16(declarationOffset + 4),
            res.UInt64(declarationOffset + 8));
        int count = (int)res.UInt32(buffer + 0x18);
        int bufferStride = res.UInt16(buffer + 0x08);
        ReadOnlySpan<byte> bytes = count * bufferStride == 0 ? [] : res.Bytes(res.ResolveArray(dataPointer, (long)count * bufferStride, 1, "sommets"), count * bufferStride);

        // Index buffer: 0x08 count, 0x10 indices (16 bits).
        int indexBuffer = Block(res, res.UInt64(g + 0x38), 0x60);
        if (indexBuffer < 0)
            return null;
        int indexCount = (int)res.UInt32(indexBuffer + 0x08);
        ulong indicesPointer = res.UInt64(indexBuffer + 0x10);
        if (indexCount == 0 || indicesPointer == 0)
            return null;
        ReadOnlySpan<byte> indexBytes = res.Bytes(res.ResolveArray(indicesPointer, indexCount, 2, "indices"), indexCount * 2);

        if (shader is not null && HiddenShaders.Contains(shader.Name))
            return null;
        // In trees and bushes, the untextured heart (cpv_only) keeps other data than a colour in its vertex colours
        // (drawn with them, it is a bright yellow ball): not drawn.
        if (shader?.Name == VertexColorShader && shaders.Any(s => s is not null && TreePresets.Contains(s.FileName)))
            return null;

        int stride = declaration.Stride;
        if (count == 0 || stride == 0 || (long)count * stride > bytes.Length)
            return null;

        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var texCoords = new Vector2[count];
        var colors = new Vector4[count];
        bool hasColors = declaration.Has(ColorComponent);
        for (int v = 0; v < count; v++)
        {
            int start = v * stride;
            positions[v] = Xyz(declaration.Read(bytes, start, PositionComponent));
            normals[v] = Xyz(declaration.Read(bytes, start, NormalComponent));
            var uv = declaration.Read(bytes, start, TexCoordComponent);
            texCoords[v] = new Vector2(uv.X, uv.Y);
            colors[v] = hasColors ? declaration.Read(bytes, start, ColorComponent) : Vector4.One;
        }

        // Ignore incomplete triangles and indices pointing outside the vertices.
        int safeCount = indexCount - indexCount % 3;
        var indices = new ushort[safeCount];
        for (int i = 0; i < safeCount; i++)
        {
            ushort index = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes[(i * 2)..]);
            indices[i] = index < count ? index : (ushort)0;
        }

        return new ModelMesh
        {
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Colors = colors,
            Indices = indices,
            DiffuseTextureName = shader is null ? null : ColorSamplers.Select(s => shader.Textures.GetValueOrDefault(s)).FirstOrDefault(n => n is not null),
            BlendMode = shader?.RenderBucket switch
            {
                // Render bucket chosen by the game: alpha (1), decal (2) and displacement alpha (7) are translucent,
                // cutout (3) is cut out, water (6: water_river, water_poolenv, water_fountain...) is water.
                1 or 2 or 7 => MeshBlendMode.Translucent,
                3 => MeshBlendMode.Cutout,
                6 => MeshBlendMode.Water,
                _ => MeshBlendMode.Opaque,
            },
            Emission = shader is null ? MeshEmission.None
                : NightEmissiveShaders.Contains(shader.Name) ? MeshEmission.Night
                : AlwaysEmissiveShaders.Contains(shader.Name) ? MeshEmission.Always
                : MeshEmission.None,
            Terrain = Terrain(shader, declaration, bytes, count),
            VertexColored = hasColors && shader?.Name == VertexColorShader,
        };
    }

    // The ground layers of a terrain shader (several TextureSampler_layerN), with their weights read from the
    // vertices; null for other shaders.
    private static TerrainBlend? Terrain(Shader? shader, VertexDeclaration declaration, ReadOnlySpan<byte> bytes, int count)
    {
        if (shader is null)
            return null;
        var layers = TerrainLayers.Select(h => shader.Textures.GetValueOrDefault(h)).ToArray();
        if (layers.All(l => l is null))
            return null;

        var mask = shader.Textures.GetValueOrDefault(TerrainMask);
        var mode = shader.Name == TerrainVertexOnlyShader || mask is null ? TerrainBlendMode.VertexOnly
            : TerrainMaskOnlyShaders.Contains(shader.Name) ? TerrainBlendMode.MaskOnly
            : TerrainBlendMode.Mixed;

        bool hasWeights = declaration.Has(SecondColorComponent);
        bool hasMaskCoords = declaration.Has(SecondTexCoordComponent);
        var weights = new Vector4[count];
        var maskCoords = new Vector2[count];
        for (int v = 0; v < count; v++)
        {
            int start = v * declaration.Stride;
            weights[v] = hasWeights ? declaration.Read(bytes, start, SecondColorComponent) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            if (hasMaskCoords)
            {
                var uv = declaration.Read(bytes, start, SecondTexCoordComponent);
                maskCoords[v] = new Vector2(uv.X, uv.Y);
            }
            else
            {
                maskCoords[v] = new Vector2(0.5f, 0.5f);
            }
        }

        return new TerrainBlend { LayerNames = layers, MaskName = mask, Mode = mode, Weights = weights, MaskTexCoords = maskCoords };
    }

    // Vertex layout: one flag bit per component present, 4 bits of storage type per component; components follow one
    // another in bit order.
    private readonly record struct VertexDeclaration(uint Flags, int Stride, ulong Types)
    {
        public bool Has(int component) => ((Flags >> component) & 1) != 0;

        private int Type(int component) => (int)((Types >> (component * 4)) & 0xF);

        // Bytes of each storage type: half2, float, half4, (unused), float2, float3, float4, ubyte4, colour, rgba8 snorm.
        private static int Size(int type) => type switch
        {
            1 => 4, 2 => 4, 3 => 8, 5 => 8, 6 => 12, 7 => 16, 8 => 4, 9 => 4, 10 => 4,
            _ => 0,
        };

        private int Offset(int component)
        {
            int offset = 0;
            for (int k = 0; k < component; k++)
            {
                if (Has(k))
                    offset += Size(Type(k));
            }
            return offset;
        }

        public Vector4 Read(ReadOnlySpan<byte> bytes, int vertexStart, int component)
        {
            if (!Has(component))
                return Vector4.Zero;
            int o = vertexStart + Offset(component);
            return Type(component) switch
            {
                2 => new Vector4(F(bytes, o), 0, 0, 0),
                5 => new Vector4(F(bytes, o), F(bytes, o + 4), 0, 0),
                6 => new Vector4(F(bytes, o), F(bytes, o + 4), F(bytes, o + 8), 0),
                7 => new Vector4(F(bytes, o), F(bytes, o + 4), F(bytes, o + 8), F(bytes, o + 12)),
                1 => new Vector4(H(bytes, o), H(bytes, o + 2), 0, 0),
                3 => new Vector4(H(bytes, o), H(bytes, o + 2), H(bytes, o + 4), H(bytes, o + 6)),
                9 => new Vector4(B(bytes, o), B(bytes, o + 1), B(bytes, o + 2), B(bytes, o + 3)),
                _ => Vector4.Zero,
            };
        }

        private static float F(ReadOnlySpan<byte> b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b[at..]) : 0f;
        private static float H(ReadOnlySpan<byte> b, int at) => at + 2 <= b.Length ? (float)BinaryPrimitives.ReadHalfLittleEndian(b[at..]) : 0f;
        private static float B(ReadOnlySpan<byte> b, int at) => at < b.Length ? b[at] / 255f : 0f;
    }

    // ---- Fragments (vehicles) ----

    // Fragment: 0x30 main drawable, 0xF0 physics levels (0x10: the first level).
    private const int FragDrawablePointer = 0x30;
    private const int FragPhysicsPointer = 0xF0;

    // Bone tags of the wheels: left / right, front, middle (6-wheelers and more) and rear.
    private static readonly HashSet<ushort> FrontWheelBones = [27922, 26418];
    private static readonly HashSet<ushort> RearWheelBones = [29921, 29922, 29923, 27902, 5857, 5858, 5859, 26398];
    private static readonly HashSet<ushort> RightWheelBones = [26418, 5857, 5858, 5859, 26398];

    // Adds the wheels of a vehicle to its body.
    private static ModelData AddWheels(ResourceData res, ModelData body)
    {
        int group = Block(res, res.UInt64(FragPhysicsPointer), 0x30);
        int lod = group < 0 ? -1 : Block(res, res.UInt64(group + 0x10), 0x130);
        if (lod < 0)
            return body;
        // Physics level: 0x30 position offset, 0xD0 children, 0x100 transforms, 0x11D children count.
        int childCount = res.Byte(lod + 0x11D);
        ulong childrenPointer = res.UInt64(lod + 0xD0);
        int transformsBlock = Block(res, res.UInt64(lod + 0x100), 0x20);
        if (childrenPointer == 0 || childCount == 0 || transformsBlock < 0)
            return body;
        int children = res.ResolveArray(childrenPointer, childCount, 8, "morceaux du véhicule");
        int matrixCount = (int)res.UInt32(transformsBlock + 0x10);
        int matrices = res.ResolveArray(res.UInt64(lod + 0x100) + 0x20, matrixCount, 64, "placements des morceaux");

        // Child: 0x12 bone tag, 0xA0 drawable.
        var childOffsets = new int[childCount];
        var bones = new ushort[childCount];
        var drawables = new int[childCount];
        for (int i = 0; i < childCount; i++)
        {
            childOffsets[i] = Block(res, res.UInt64(children + i * 8), 0xA8);
            if (childOffsets[i] < 0)
                continue;
            bones[i] = res.UInt16(childOffsets[i] + 0x12);
            drawables[i] = Block(res, res.UInt64(childOffsets[i] + 0xA0), DrawableLength);
        }

        bool IsWheel(ushort bone) => FrontWheelBones.Contains(bone) || RearWheelBones.Contains(bone);
        var own = new ModelData?[childCount];
        ModelData? front = null, rear = null;
        for (int i = 0; i < childCount; i++)
        {
            if (childOffsets[i] < 0 || !IsWheel(bones[i]) || drawables[i] < 0 || AllModelsCount(res, drawables[i]) == 0)
                continue;
            own[i] = Convert(res, 0, drawables[i]);
            if (FrontWheelBones.Contains(bones[i]))
                front ??= own[i];
            else
                rear ??= own[i];
        }
        if (front is null && rear is null)
            return body;

        var meshes = body.Meshes.ToList();
        var textures = new Dictionary<uint, TextureData>(body.EmbeddedTextures);
        var (min, max) = (body.BoundsMin, body.BoundsMax);
        var offset = Vec3(res, lod + 0x30);
        for (int i = 0; i < childCount && i < matrixCount; i++)
        {
            if (childOffsets[i] < 0 || !IsWheel(bones[i]))
                continue;
            var wheel = own[i] ?? (FrontWheelBones.Contains(bones[i]) ? front ?? rear : rear ?? front);
            if (wheel is null)
                continue;

            var transform = Matrix(res, matrices + i * 64);
            transform.Translation += offset;
            if (RightWheelBones.Contains(bones[i]))
            {
                // Half a turn around the axle's perpendicular (Y): the wheel's outer face looks right.
                transform.M11 = -1; transform.M12 = 0; transform.M13 = 0;
                transform.M21 = 0; transform.M22 = 1; transform.M23 = 0;
                transform.M31 = 0; transform.M32 = 0; transform.M33 = -1;
            }
            foreach (var mesh in wheel.Meshes)
            {
                var placed = Transform(mesh, transform);
                meshes.Add(placed);
                foreach (var p in placed.Positions)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            foreach (var (hash, texture) in wheel.EmbeddedTextures)
                textures.TryAdd(hash, texture);
        }

        return new ModelData { NameHash = body.NameHash, BoundsMin = min, BoundsMax = max, Meshes = meshes, EmbeddedTextures = textures };
    }

    private static ModelMesh Transform(ModelMesh mesh, Matrix4x4 transform) => new()
    {
        Positions = mesh.Positions.Select(p => Vector3.Transform(p, transform)).ToArray(),
        Normals = mesh.Normals.Select(n => Vector3.Normalize(Vector3.TransformNormal(n, transform))).ToArray(),
        TexCoords = mesh.TexCoords,
        Colors = mesh.Colors,
        Indices = mesh.Indices,
        DiffuseTextureName = mesh.DiffuseTextureName,
        BlendMode = mesh.BlendMode,
        Emission = mesh.Emission,
        Terrain = mesh.Terrain,
        VertexColored = mesh.VertexColored,
    };

    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

    private static float F32(ResourceData res, int o) => BitConverter.Int32BitsToSingle((int)res.UInt32(o));

    private static Vector3 Vec3(ResourceData res, int o) => new(F32(res, o), F32(res, o + 4), F32(res, o + 8));

    private static Matrix4x4 Matrix(ResourceData res, int o) => new(
        F32(res, o), F32(res, o + 4), F32(res, o + 8), F32(res, o + 12),
        F32(res, o + 16), F32(res, o + 20), F32(res, o + 24), F32(res, o + 28),
        F32(res, o + 32), F32(res, o + 36), F32(res, o + 40), F32(res, o + 44),
        F32(res, o + 48), F32(res, o + 52), F32(res, o + 56), F32(res, o + 60));
}
