using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Gta4;

// Reads the models of GTA IV: ".wdr" (one drawable), ".wdd" (a dictionary of drawables) and ".wft" (fragments:
// breakable objects made of pieces). A resource holds 32-bit
// pointers: 0x5xxxxxxx into the system pages, 0x6xxxxxxx into the graphics pages (vertex and index data), which
// follow the system pages once decompressed. Only the most detailed level of each drawable is read.
public static class Gta4Drawables
{
    private const int MaxVertices = 0x10000;

    public sealed class Resource(byte[] data, int systemSize)
    {
        public byte[] Data { get; } = data;

        public int SystemSize { get; } = systemSize;

        // Offset of a pointer in the data, -1 if null or outside.
        public int Offset(uint pointer)
        {
            long offset = (pointer >> 28) switch
            {
                5 => pointer & 0x0FFFFFFF,
                6 => SystemSize + (long)(pointer & 0x0FFFFFFF),
                _ => -1,
            };
            return offset >= 0 && offset < Data.Length ? (int)offset : -1;
        }

        public int Pointer(int at) => at >= 0 && at + 4 <= Data.Length ? Offset(UInt(at)) : -1;

        public uint UInt(int at) => at >= 0 && at + 4 <= Data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at)) : 0;

        public ushort UShort(int at) => at >= 0 && at + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at)) : (ushort)0;

        public float Float(int at) => at >= 0 && at + 4 <= Data.Length ? BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(at)) : 0f;

        public Vector3 Vector(int at) => new(Float(at), Float(at + 4), Float(at + 8));

        public string? String(int at)
        {
            if (at < 0)
                return null;
            int end = Array.IndexOf(Data, (byte)0, at, Math.Min(256, Data.Length - at));
            return end < 0 ? null : Encoding.ASCII.GetString(Data, at, end - at);
        }

        // An array of pointers: {pointer, ushort count}.
        public IEnumerable<int> Pointers(int at)
        {
            int items = Pointer(at);
            int count = UShort(at + 4);
            for (int i = 0; i < count && items >= 0; i++)
                yield return Pointer(items + i * 4);
        }
    }

    // Shader group: embedded texture dictionary at 4, shaders at 8.
    private const int ShaderGroupTextures = 0x04;
    // Drawable: shader group at 8, bounds at 0x20 / 0x30, model lists of the 4 levels of detail at 0x40.
    private const int DrawableShaderGroup = 0x08, DrawableBoundsMin = 0x20, DrawableBoundsMax = 0x30, DrawableModels = 0x40;
    // Dictionary: hashes at 0x10, drawables at 0x18 (pointer arrays).
    private const int DictionaryHashes = 0x10, DictionaryItems = 0x18;

    public static ModelData? ReadDrawable(Resource res, int drawable, uint nameHash) => ReadDrawable(res, drawable, nameHash, []);

    // A drawable without its own shader group uses sharedShaders (the pieces of a fragment).
    private static ModelData? ReadDrawable(Resource res, int drawable, uint nameHash, List<Shader> sharedShaders)
    {
        if (drawable < 0)
            return null;
        int group = res.Pointer(drawable + DrawableShaderGroup);
        var shaders = group >= 0 ? ReadShaders(res, group) : sharedShaders;
        var meshes = new List<ModelMesh>();
        int lod = res.Pointer(drawable + DrawableModels);
        if (lod >= 0)
        {
            foreach (int model in res.Pointers(lod))
                ReadModel(res, model, shaders, meshes);
        }
        if (meshes.Count == 0)
            return null;
        return new ModelData
        {
            NameHash = nameHash,
            BoundsMin = res.Vector(drawable + DrawableBoundsMin),
            BoundsMax = res.Vector(drawable + DrawableBoundsMax),
            Meshes = meshes,
            EmbeddedTextures = group >= 0 ? Gta4Textures.ReadDictionary(res, res.Pointer(group + ShaderGroupTextures)) : new Dictionary<uint, TextureData>(),
        };
    }

    public static Dictionary<uint, ModelData> ReadDictionary(Resource res)
    {
        var models = new Dictionary<uint, ModelData>();
        int hashes = res.Pointer(DictionaryHashes);
        int i = 0;
        foreach (int drawable in res.Pointers(DictionaryItems))
        {
            uint hash = res.UInt(hashes + i++ * 4);
            if (ReadDrawable(res, drawable, hash) is { } model)
                models[hash] = model;
        }
        return models;
    }

    // Fragment: main drawable at 0xB4 (its skeleton at 0x0C: bone matrices at 0x10, bone count at 0x14), pieces at
    // 0xD4, piece count in bytes 0x1F3 / 0x1F6. Piece: bone at 0x0E, drawable at 0x90, modelled around its bone; the
    // pieces have no shader group, they use the main drawable's.
    private const int FragmentDrawable = 0xB4, FragmentChildren = 0xD4, FragmentChildCountA = 0x1F3, FragmentChildCountB = 0x1F6;
    private const int ChildBone = 0x0E, ChildDrawable = 0x90, SkeletonMatrices = 0x10, SkeletonBoneCount = 0x14;

    public static ModelData? ReadFragment(Resource res, uint nameHash)
    {
        int main = res.Pointer(FragmentDrawable);
        var meshes = new List<ModelMesh>();
        var textures = new Dictionary<uint, TextureData>();
        int mainGroup = main >= 0 ? res.Pointer(main + DrawableShaderGroup) : -1;
        var sharedShaders = mainGroup >= 0 ? ReadShaders(res, mainGroup) : [];
        if (mainGroup >= 0)
            Merge(textures, Gta4Textures.ReadDictionary(res, res.Pointer(mainGroup + ShaderGroupTextures)));
        if (ReadDrawable(res, main, nameHash) is { } body)
        {
            meshes.AddRange(body.Meshes);
            Merge(textures, body.EmbeddedTextures);
        }

        int skeleton = main >= 0 ? res.Pointer(main + 0x0C) : -1;
        int matrices = skeleton >= 0 ? res.Pointer(skeleton + SkeletonMatrices) : -1;
        int bones = skeleton >= 0 ? res.UShort(skeleton + SkeletonBoneCount) : 0;
        int children = res.Pointer(FragmentChildren);
        int count = res.Data.Length > FragmentChildCountB ? Math.Max(res.Data[FragmentChildCountA], res.Data[FragmentChildCountB]) : 0;
        for (int i = 0; i < count && children >= 0; i++)
        {
            int child = res.Pointer(children + i * 4);
            if (child < 0 || ReadDrawable(res, res.Pointer(child + ChildDrawable), nameHash, sharedShaders) is not { } piece)
                continue;
            Merge(textures, piece.EmbeddedTextures);
            int bone = res.UShort(child + ChildBone);
            var transform = matrices >= 0 && bone < bones ? Matrix(res, matrices + bone * 64) : Matrix4x4.Identity;
            meshes.AddRange(transform.IsIdentity ? piece.Meshes : piece.Meshes.Select(m => Transformed(m, transform)));
        }
        if (meshes.Count == 0)
            return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
        {
            foreach (var p in mesh.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return new ModelData { NameHash = nameHash, BoundsMin = min, BoundsMax = max, Meshes = meshes, EmbeddedTextures = textures };
    }

    private static void Merge(Dictionary<uint, TextureData> into, IReadOnlyDictionary<uint, TextureData> textures)
    {
        foreach (var (hash, texture) in textures)
            into.TryAdd(hash, texture);
    }

    // Bone matrix: three rows of axes then the position, each over 16 bytes.
    private static Matrix4x4 Matrix(Resource res, int at) => new(
        res.Float(at), res.Float(at + 4), res.Float(at + 8), 0f,
        res.Float(at + 16), res.Float(at + 20), res.Float(at + 24), 0f,
        res.Float(at + 32), res.Float(at + 36), res.Float(at + 40), 0f,
        res.Float(at + 48), res.Float(at + 52), res.Float(at + 56), 1f);

    private static ModelMesh Transformed(ModelMesh mesh, Matrix4x4 transform) => new()
    {
        Positions = mesh.Positions.Select(p => Vector3.Transform(p, transform)).ToArray(),
        Normals = mesh.Normals.Select(n => Vector3.TransformNormal(n, transform) is var t && t.LengthSquared() > 1e-12f ? Vector3.Normalize(t) : n).ToArray(),
        TexCoords = mesh.TexCoords,
        Colors = mesh.Colors,
        Terrain = mesh.Terrain,
        Indices = mesh.Indices,
        DiffuseTextureName = mesh.DiffuseTextureName,
        ShaderHash = mesh.ShaderHash,
        BlendMode = mesh.BlendMode,
        Emission = mesh.Emission,
    };

    // A shader: its kind (name, "gta_normal_spec"), its preset ("gta_cutout", which tells how it is blended), its
    // diffuse texture and, for the ground shaders, the textures of its layers.
    private sealed record Shader(string? Name, string? Preset, string? Diffuse, string?[] Layers);

    private static readonly uint DiffuseSampler = Joaat.Hash("texturesampler");

    private static readonly uint[] LayerSamplers = [.. Enumerable.Range(0, 4).Select(i => Joaat.Hash($"texturesampler_layer{i}"))];

    // Shader group: shaders at 8. Shader: parameter data at 0x14, count at 0x1C, types at 0x24 (0 = texture), name
    // hashes at 0x34, name at 0x44, preset file (".sps") at 0x48. Texture parameter: name at 0x14.
    private static List<Shader> ReadShaders(Resource res, int group)
    {
        var shaders = new List<Shader>();
        if (group < 0)
            return shaders;
        foreach (int shader in res.Pointers(group + 0x08))
        {
            if (shader < 0)
            {
                shaders.Add(new Shader(null, null, null, new string?[4]));
                continue;
            }
            int values = res.Pointer(shader + 0x14);
            int count = (int)Math.Min(res.UInt(shader + 0x1C), 256);
            int types = res.Pointer(shader + 0x24);
            int hashes = res.Pointer(shader + 0x34);
            string? diffuse = null;
            var layers = new string?[LayerSamplers.Length];
            for (int i = 0; i < count && values >= 0 && types >= 0 && hashes >= 0; i++)
            {
                if (types + i >= res.Data.Length || res.Data[types + i] != 0)
                    continue;
                uint parameter = res.UInt(hashes + i * 4);
                int layer = Array.IndexOf(LayerSamplers, parameter);
                if (parameter != DiffuseSampler && layer < 0)
                    continue;
                int texture = res.Pointer(values + i * 4);
                string? name = texture >= 0 && res.String(res.Pointer(texture + 0x14)) is { } text ? Gta4Textures.BareName(text) : null;
                if (layer >= 0)
                    layers[layer] = name;
                else
                    diffuse = name;
            }
            string? preset = res.String(res.Pointer(shader + 0x48));
            if (preset is not null && preset.EndsWith(".sps", StringComparison.OrdinalIgnoreCase))
                preset = preset[..^4];
            shaders.Add(new Shader(res.String(res.Pointer(shader + 0x44)), preset, diffuse ?? layers[0], layers));
        }
        return shaders;
    }

    // Model: geometries at 4, shader of each geometry (ushort) at 0x10.
    private static void ReadModel(Resource res, int model, List<Shader> shaders, List<ModelMesh> meshes)
    {
        if (model < 0)
            return;
        int shaderMap = res.Pointer(model + 0x10);
        int index = 0;
        foreach (int geometry in res.Pointers(model + 0x04))
        {
            int shaderIndex = shaderMap >= 0 ? res.UShort(shaderMap + index * 2) : 0;
            index++;
            var shader = shaderIndex < shaders.Count ? shaders[shaderIndex] : null;
            if (ReadGeometry(res, geometry, shader) is { } mesh)
                meshes.Add(mesh);
        }
    }

    // Geometry: vertex buffer at 0x0C, index buffer at 0x1C. Vertex buffer: vertex count (ushort) at 4, stride at 0x0C,
    // declaration at 0x10, data at 0x18. Index buffer: count at 4, data at 8 (ushort triangle list).
    private static ModelMesh? ReadGeometry(Resource res, int geometry, Shader? shader)
    {
        if (geometry < 0)
            return null;
        int vertexBuffer = res.Pointer(geometry + 0x0C);
        int indexBuffer = res.Pointer(geometry + 0x1C);
        if (vertexBuffer < 0 || indexBuffer < 0)
            return null;
        int vertexCount = res.UShort(vertexBuffer + 4);
        int stride = (int)res.UInt(vertexBuffer + 0x0C);
        int declaration = res.Pointer(vertexBuffer + 0x10);
        int vertices = res.Pointer(vertexBuffer + 0x18);
        int indexCount = (int)res.UInt(indexBuffer + 4);
        int indices = res.Pointer(indexBuffer + 8);
        if (vertexCount == 0 || vertexCount > MaxVertices || stride <= 0 || declaration < 0 || vertices < 0 || indices < 0
            || indexCount < 3 || (long)vertices + (long)vertexCount * stride > res.Data.Length || (long)indices + indexCount * 2L > res.Data.Length)
            return null;

        var layout = VertexLayout.Read(res, declaration, stride);
        if (layout is null)
            return null;

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var texCoords = new Vector2[vertexCount];
        var colors = new Vector4[vertexCount];
        // Ground: one set of texture coordinates per layer, then the layer weights two by two (the first one, always
        // 1, is the base layer's).
        int layerCount = shader is null ? 0 : Array.FindLastIndex(shader.Layers, l => l is not null) + 1;
        var weights = layerCount > 1 ? new Vector4[vertexCount] : null;
        var span = res.Data.AsSpan(vertices, vertexCount * stride);
        for (int v = 0; v < vertexCount; v++)
        {
            var vertex = span.Slice(v * stride, stride);
            positions[v] = layout.Position(vertex);
            normals[v] = layout.Normal(vertex);
            texCoords[v] = layout.TexCoord(vertex);
            colors[v] = layout.Color(vertex);
            if (weights is not null)
            {
                var first = layout.TexCoord(vertex, layerCount);
                var second = layout.TexCoord(vertex, layerCount + 1);
                weights[v] = Vector4.Clamp(new Vector4(first.Y, second.X, second.Y, 0f), Vector4.Zero, Vector4.One);
            }
        }

        indexCount -= indexCount % 3;
        var triangles = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            ushort value = res.UShort(indices + i * 2);
            if (value >= vertexCount)
                return null;
            triangles[i] = value;
        }
        if (!layout.HasNormal)
            FlatNormals(positions, triangles, normals);

        string? name = shader?.Name?.ToLowerInvariant();
        // The preset tells how the surface is blended ("gta_default" shaders are used with "gta_decal", "gta_alpha"...).
        string? preset = (shader?.Preset ?? shader?.Name)?.ToLowerInvariant();
        return new ModelMesh
        {
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Colors = colors,
            Indices = triangles,
            DiffuseTextureName = shader?.Diffuse,
            ShaderHash = name is null ? 0 : Joaat.Hash(name),
            BlendMode = weights is not null ? MeshBlendMode.Opaque : BlendModeOf(preset),
            Emission = preset is not null && preset.Contains("emissive", StringComparison.Ordinal)
                ? preset.Contains("night", StringComparison.Ordinal) ? MeshEmission.Night : MeshEmission.Always
                : MeshEmission.None,
            Terrain = weights is null ? null : new TerrainBlend
            {
                LayerNames = [shader!.Layers[1], shader.Layers[2], shader.Layers[3]],
                Mode = TerrainBlendMode.Layered,
                Weights = weights,
                MaskTexCoords = new Vector2[vertexCount],
            },
        };
    }

    private static MeshBlendMode BlendModeOf(string? preset) => preset switch
    {
        null => MeshBlendMode.Opaque,
        _ when preset.Contains("cutout", StringComparison.Ordinal) || preset.Contains("trees", StringComparison.Ordinal) => MeshBlendMode.Cutout,
        _ when preset.Contains("glass", StringComparison.Ordinal) || preset.Contains("decal", StringComparison.Ordinal)
            || preset.Contains("alpha", StringComparison.Ordinal) => MeshBlendMode.Translucent,
        _ => MeshBlendMode.Opaque,
    };

    private static void FlatNormals(Vector3[] positions, ushort[] triangles, Vector3[] normals)
    {
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            var n = Vector3.Cross(positions[triangles[i + 1]] - positions[triangles[i]], positions[triangles[i + 2]] - positions[triangles[i]]);
            normals[triangles[i]] += n;
            normals[triangles[i + 1]] += n;
            normals[triangles[i + 2]] += n;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
    }

    // Vertex declaration: used elements (bit per element) at 0, stride at 4, element types (4 bits per element) at 8.
    // Elements in this order: position, blend weights, blend indices, normal, colour, specular, 8 texture coordinates,
    // tangent, binormal.
    private sealed class VertexLayout
    {
        private const int PositionElement = 0, NormalElement = 3, ColorElement = 4, TexCoordElement = 6, ElementCount = 16;

        private readonly int[] _offsets = new int[ElementCount];
        private readonly int[] _types = new int[ElementCount];

        public bool HasNormal => _offsets[NormalElement] >= 0;

        public static VertexLayout? Read(Resource res, int declaration, int stride)
        {
            uint used = res.UInt(declaration);
            ulong types = (ulong)res.UInt(declaration + 8) | (ulong)res.UInt(declaration + 12) << 32;
            var layout = new VertexLayout();
            int offset = 0;
            for (int e = 0; e < ElementCount; e++)
            {
                layout._offsets[e] = -1;
                if ((used & (1u << e)) == 0)
                    continue;
                int type = (int)(types >> (e * 4)) & 0xF;
                int size = Size(type);
                if (size == 0)
                    return null;
                layout._offsets[e] = offset;
                layout._types[e] = type;
                offset += size;
            }
            return layout._offsets[PositionElement] >= 0 && offset <= stride ? layout : null;
        }

        // Type codes: 0-3 half floats (1 to 4), 4-7 floats (1 to 4), 8 four bytes, 9 colour (BGRA), 10 packed normal.
        private static int Size(int type) => type switch
        {
            0 => 2,
            1 => 4,
            2 => 6,
            3 => 8,
            4 => 4,
            5 => 8,
            6 => 12,
            7 => 16,
            8 or 9 or 10 => 4,
            _ => 0,
        };

        public Vector3 Position(ReadOnlySpan<byte> v) => Read3(v, PositionElement);

        public Vector3 Normal(ReadOnlySpan<byte> v)
        {
            if (_offsets[NormalElement] < 0)
                return Vector3.Zero;
            var n = Read3(v, NormalElement);
            return n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }

        // Texture coordinates of the given set (0 to 7).
        public Vector2 TexCoord(ReadOnlySpan<byte> v, int set = 0)
        {
            int element = TexCoordElement + set;
            int at = set is >= 0 and < 8 ? _offsets[element] : -1;
            if (at < 0)
                return Vector2.Zero;
            return _types[element] switch
            {
                1 or 3 => new Vector2(Half(v, at), Half(v, at + 2)),
                5 or 6 or 7 => new Vector2(Single(v, at), Single(v, at + 4)),
                _ => Vector2.Zero,
            };
        }

        public Vector4 Color(ReadOnlySpan<byte> v)
        {
            int at = _offsets[ColorElement];
            if (at < 0 || _types[ColorElement] is not (8 or 9))
                return Vector4.One;
            return new Vector4(v[at + 2], v[at + 1], v[at], v[at + 3]) / 255f;
        }

        private Vector3 Read3(ReadOnlySpan<byte> v, int element)
        {
            int at = _offsets[element];
            return _types[element] switch
            {
                2 or 3 => new Vector3(Half(v, at), Half(v, at + 2), Half(v, at + 4)),
                6 or 7 => new Vector3(Single(v, at), Single(v, at + 4), Single(v, at + 8)),
                10 => Packed(BinaryPrimitives.ReadUInt32LittleEndian(v[at..])),
                _ => Vector3.Zero,
            };
        }

        // Three signed 10-bit values.
        private static Vector3 Packed(uint value)
        {
            static float Part(uint bits) => ((int)(bits << 22) >> 22) / 511f;
            return new Vector3(Part(value), Part(value >> 10), Part(value >> 20));
        }

        private static float Half(ReadOnlySpan<byte> v, int at) => (float)BinaryPrimitives.ReadHalfLittleEndian(v[at..]);

        private static float Single(ReadOnlySpan<byte> v, int at) => BinaryPrimitives.ReadSingleLittleEndian(v[at..]);
    }
}

// Models of a GTA IV installation for the 3D view: ".wdr" and ".wft" files give one model named after the file,
// ".wdd" files every model of the dictionary.
public sealed class Gta4ModelReader(Gta4FileSystem files) : IModelReader
{
    public IReadOnlyDictionary<uint, ModelData> ReadModels(string path)
    {
        if (files.ReadResource(path) is not { } resource)
            return new Dictionary<uint, ModelData>();
        var res = new Gta4Drawables.Resource(resource.Data, resource.SystemSize);
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".wdr":
                    uint hash = Joaat.Hash(Path.GetFileNameWithoutExtension(path));
                    return Gta4Drawables.ReadDrawable(res, 0, hash) is { } model
                        ? new Dictionary<uint, ModelData> { [hash] = model }
                        : new Dictionary<uint, ModelData>();
                case ".wdd":
                    return Gta4Drawables.ReadDictionary(res);
                case ".wft":
                    uint fragment = Joaat.Hash(Path.GetFileNameWithoutExtension(path));
                    return Gta4Drawables.ReadFragment(res, fragment) is { } pieces
                        ? new Dictionary<uint, ModelData> { [fragment] = pieces }
                        : new Dictionary<uint, ModelData>();
                default:
                    return new Dictionary<uint, ModelData>();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{path} : modèle illisible ({ex.Message})", ex);
        }
    }
}
