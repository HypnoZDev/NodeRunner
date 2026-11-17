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

    public IReadOnlyDictionary<uint, ModelData> ReadModels(string path) => ReadModels(path, null);

    public IReadOnlyDictionary<uint, ModelData> ReadModels(string path, string? skeletonPath)
    {
        if (_files.ReadFile(path) is not { } data)
            return new Dictionary<uint, ModelData>();
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        try
        {
            var (pose, skeleton) = skeletonPath is null ? (null, null) : ReadSkeleton(skeletonPath);
            return Read(data, header, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path), pose, skeleton);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    // Default pose of the bones of a .yft or .ydr (null without a skeleton), and the bones themselves when stored.
    private (Matrix4x4[]? Pose, SkeletonData? Skeleton) ReadSkeleton(string path)
    {
        if (_files.ReadFile(path) is not { } data || _files.GetResourceHeader(path) is not { } header)
            return (null, null);
        var res = Open(data, header);
        if (!path.EndsWith(".yft", StringComparison.OrdinalIgnoreCase))
            return (SkeletonPose(res, 0), Skeleton(res, 0));
        int main = Block(res, res.UInt64(FragDrawablePointer), DrawableLength);
        return (SkeletonPose(res, main) ?? FragmentPose(res), Skeleton(res, main));
    }

    private static ResourceData Open(ReadOnlySpan<byte> data, ResourceHeader header) =>
        new(data, ResourcePages.Size(header.SystemFlags))
        {
            Enhanced = header.Version is EnhancedDrawableVersion or EnhancedFragmentVersion,
        };

    // pose: the bones the parts of a dictionary are attached to when its drawables have no skeleton of their own.
    // skeleton: those bones, given to the parts with which vertices follow them (to be animated).
    public static Dictionary<uint, ModelData> Read(ReadOnlySpan<byte> data, ResourceHeader header, string name, string extension,
        Matrix4x4[]? pose = null, SkeletonData? skeleton = null)
    {
        var res = Open(data, header);
        uint fileHash = Joaat.Hash(name);
        var models = new Dictionary<uint, ModelData>();
        switch (extension.ToLowerInvariant())
        {
            case ".ydr":
                // Given a skeleton (its own), its parts keep their bones to be animated.
                if (Convert(res, fileHash, 0, SkeletonPose(res, 0), bones: skeleton is null ? null : Skeleton(res, 0)) is { } drawable)
                    models[fileHash] = drawable;
                break;

            case ".yft":
                int main = Block(res, res.UInt64(FragDrawablePointer), DrawableLength);
                if (Convert(res, fileHash, main, FragmentPose(res) ?? SkeletonPose(res, main)) is { } body)
                    models[fileHash] = AddWheels(res, body, main);
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
                    // A part with a skeleton of its own (heads: the face's bones as well) follows the ped's bones of
                    // the same tags.
                    var own = SkeletonPose(res, offset);
                    int[]? map = own is not null && skeleton is not null && Skeleton(res, offset) is { } ownBones ? BoneMap(ownBones, skeleton) : null;
                    var bones = own is null || map is not null ? skeleton : null;
                    if (Convert(res, hash, offset, own ?? pose, bones: bones, boneMap: map) is { } model)
                        models[hash] = model;
                }
                break;

            default:
                throw new InvalidDataException($"Type de modèle non géré : {name}{extension}");
        }
        return models;
    }

    // A geometry as stored in the file, for tools that change it: its detail level (0 = high), where its triangle indices
    // are in the resource data, its vertex positions in the model (placed on their bone like the drawn meshes) and its
    // indices.
    public sealed record StoredGeometry(int Level, int IndicesAt, Vector3[] Positions, ushort[] Indices)
    {
        // Its vertices, to move them; null when their layout cannot be written.
        public StoredVertices? Vertices { get; init; }

        // Boxes holding it (its own, its model's, the drawable's): where each minimum is, the maximum 16 bytes after.
        public int[] Boxes { get; init; } = [];

        // The drawable's bounding sphere: its centre, the radius 12 bytes after; -1 when none.
        public int SphereAt { get; init; } = -1;

        // Where the geometry, its vertex buffer and its index buffer are; where its vertices are and their size.
        public int GeometryAt { get; init; } = -1;
        public int VertexBufferAt { get; init; } = -1;
        public int IndexBufferAt { get; init; } = -1;
        public int VertexAt { get; init; } = -1;
        public int Stride { get; init; }
        public int VertexCount => Positions.Length;

        // Its material: the shader's number in the drawable, and the name of its main texture (null when none).
        public int ShaderIndex { get; init; } = -1;
        public string? Texture { get; init; }
    }

    // A vertex given by its values (model space), to write it in a geometry's layout.
    public readonly record struct VertexValues(Vector3 Position, Vector3 Normal, Vector2 TexCoord, Vector4 Color, Vector4 Tangent);

    // Where a geometry's vertices are in the data and how they are stored.
    public sealed class StoredVertices
    {
        private readonly VertexDeclaration _declaration;

        internal StoredVertices(VertexDeclaration declaration, int at, int secondAt, int count, Matrix4x4 bone)
        {
            _declaration = declaration;
            At = at;
            SecondAt = secondAt;
            Count = count;
            Bone = bone;
        }

        public int At { get; }

        // A second copy of the vertices kept by the file, -1 when none.
        public int SecondAt { get; }

        public int Count { get; }
        public int Stride => _declaration.Stride;

        // From the vertices' own space to the model's (the bone they are placed on).
        public Matrix4x4 Bone { get; }

        // Moves and turns one vertex (matrix in the vertices' own space): position, normal, tangent and binormal.
        public void Transform(Span<byte> vertexBuffer, int vertex, Matrix4x4 move)
        {
            int start = vertex * Stride;
            var p = _declaration.Read(vertexBuffer, start, PositionComponent);
            _declaration.Write(vertexBuffer, start, PositionComponent, new Vector4(Vector3.Transform(Xyz(p), move), p.W));
            foreach (int component in DirectionComponents)
            {
                if (!_declaration.Has(component) || !_declaration.CanWrite(component))
                    continue;
                var d = _declaration.Read(vertexBuffer, start, component);
                var turned = Vector3.TransformNormal(Xyz(d), move);
                if (turned.LengthSquared() > 1e-12f)
                    turned = Vector3.Normalize(turned) * Xyz(d).Length();
                _declaration.Write(vertexBuffer, start, component, new Vector4(turned, d.W));
            }
        }

        private static readonly int[] DirectionComponents = [NormalComponent, TangentComponent, BinormalComponent];

        // The values of vertex v (model space), in the vertex data of the geometry.
        public VertexValues Read(ReadOnlySpan<byte> vertexBuffer, int v)
        {
            int start = v * Stride;
            var tangent = _declaration.Read(vertexBuffer, start, TangentComponent);
            var uv = _declaration.Read(vertexBuffer, start, TexCoordComponent);
            return new VertexValues(
                Vector3.Transform(Xyz(_declaration.Read(vertexBuffer, start, PositionComponent)), Bone),
                Vector3.TransformNormal(Xyz(_declaration.Read(vertexBuffer, start, NormalComponent)), Bone),
                new Vector2(uv.X, uv.Y),
                _declaration.Has(ColorComponent) ? _declaration.Read(vertexBuffer, start, ColorComponent) : Vector4.One,
                new Vector4(Vector3.TransformNormal(Xyz(tangent), Bone), tangent.W));
        }

        // A new vertex mixing three vertices of the geometry (weights adding up to 1): every value it stores (position,
        // directions, colours, texture coordinates) is mixed; what cannot be written is taken from the heaviest one.
        public byte[] Blend(ReadOnlySpan<byte> vertexBuffer, int a, int b, int c, Vector3 weights)
        {
            int heaviest = weights.X >= weights.Y && weights.X >= weights.Z ? a : weights.Y >= weights.Z ? b : c;
            var bytes = vertexBuffer.Slice(heaviest * Stride, Stride).ToArray();
            for (int component = 0; component < 16; component++)
            {
                if (!_declaration.Has(component) || component is BlendWeightsComponent or BlendIndicesComponent)
                    continue;
                var mixed = _declaration.Read(vertexBuffer, a * Stride, component) * weights.X
                    + _declaration.Read(vertexBuffer, b * Stride, component) * weights.Y
                    + _declaration.Read(vertexBuffer, c * Stride, component) * weights.Z;
                if (Array.IndexOf(DirectionComponents, component) >= 0)
                {
                    var kept = _declaration.Read(vertexBuffer, heaviest * Stride, component);
                    var direction = Xyz(mixed);
                    direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) * Xyz(kept).Length() : Xyz(kept);
                    mixed = new Vector4(direction, kept.W);
                }
                _declaration.WriteValue(bytes, 0, component, mixed);
            }
            return bytes;
        }

        // New vertices in this geometry's layout: copies of the template vertex (bones, other values) given these values.
        // Null when the bone cannot be undone.
        public byte[]? Make(ReadOnlySpan<byte> template, IReadOnlyList<VertexValues> vertices)
        {
            if (template.Length < Stride || !Matrix4x4.Invert(Bone, out var toBone))
                return null;
            var bytes = new byte[vertices.Count * Stride];
            var span = bytes.AsSpan();
            for (int i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                int start = i * Stride;
                template[..Stride].CopyTo(span[start..]);
                _declaration.Write(span, start, PositionComponent, new Vector4(Vector3.Transform(v.Position, toBone), 1f));
                var normal = Vector3.TransformNormal(v.Normal, toBone);
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                _declaration.Write(span, start, NormalComponent, new Vector4(normal, 0f));
                var tangent = Vector3.TransformNormal(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z), toBone);
                tangent = tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : Vector3.UnitX;
                float side = v.Tangent.W < 0 ? -1f : 1f;
                _declaration.Write(span, start, TangentComponent, new Vector4(tangent, side));
                _declaration.Write(span, start, BinormalComponent, new Vector4(Vector3.Cross(normal, tangent) * side, 0f));
                _declaration.WriteValue(span, start, TexCoordComponent, new Vector4(v.TexCoord, 0f, 0f));
                _declaration.WriteValue(span, start, ColorComponent, v.Color);
            }
            return bytes;
        }
    }

    // Every geometry of every detail level of one model of the file (the drawable for .ydr and .yft, the one named
    // nameHash for .ydd).
    public static List<StoredGeometry> StoredGeometries(ReadOnlySpan<byte> data, ResourceHeader header, string extension, uint nameHash)
    {
        var res = Open(data, header);
        var found = new List<StoredGeometry>();
        int d = FindDrawable(res, extension, nameHash);
        if (d < 0 || (res.UInt64(d + ModelsPointer) == 0 && res.UInt64(d + ModelsHigh) == 0))
            return found;
        var pose = extension.Equals(".yft", StringComparison.OrdinalIgnoreCase)
            ? FragmentPose(res) ?? SkeletonPose(res, d)
            : SkeletonPose(res, d);
        var shaders = Shaders(res, d);
        for (int level = 0; level < 4; level++)
        {
            if (ModelList(res, res.UInt64(d + ModelsHigh + level * 8)) is not { } models)
                continue;
            foreach (int model in models)
            {
                if (model < 0)
                    continue;
                uint binding = res.UInt32(model + SkeletonBinding);
                bool skinned = ((binding >> 8) & 0xFF) != 0;
                int bone = (int)(binding >> 24);
                var boneTransform = !skinned && pose is not null && bone < pose.Length ? pose[bone] : Matrix4x4.Identity;
                int count = res.UInt16(model + GeometriesCount);
                int geometries = res.ResolveArray(res.UInt64(model + GeometriesPointer), count, 8, "géométries");
                // Model boxes: one for the whole model, then one per geometry when there are several.
                int boxCount = count > 1 ? count + 1 : 1;
                ulong boxesPointer = res.UInt64(model + ModelBoundsPointer);
                int boxes = boxesPointer == 0 ? -1 : res.ResolveArray(boxesPointer, boxCount, BoxLength, "boîtes");
                ulong mappingPointer = res.UInt64(model + ShaderMappingPointer);
                int mapping = mappingPointer == 0 ? -1 : res.ResolveArray(mappingPointer, count, 2, "shaders des géométries");
                for (int i = 0; i < count; i++)
                {
                    int geometry = Block(res, res.UInt64(geometries + i * 8), GeometryLength);
                    if (geometry < 0 || Buffers(res, geometry) is not { } buffers)
                        continue;
                    int shaderIndex = mapping >= 0 ? res.UInt16(mapping + i * 2) : -1;
                    var shader = shaderIndex >= 0 && shaderIndex < shaders.Count ? shaders[shaderIndex] : null;
                    var (declaration, vertexAt, vertexLength, vertexCount, indicesAt, indexCount) = buffers;
                    var held = new List<int> { d + BoundsMinOffset };
                    if (boxes >= 0)
                    {
                        held.Add(boxes);
                        if (count > 1)
                            held.Add(boxes + (i + 1) * BoxLength);
                    }
                    var vertexBytes = res.Bytes(vertexAt, vertexLength);
                    var positions = new Vector3[vertexCount];
                    for (int v = 0; v < vertexCount; v++)
                        positions[v] = Vector3.Transform(Xyz(declaration.Read(vertexBytes, v * declaration.Stride, PositionComponent)), boneTransform);
                    var indices = new ushort[indexCount - indexCount % 3];
                    for (int k = 0; k < indices.Length; k++)
                        indices[k] = res.UInt16(indicesAt + k * 2);
                    found.Add(new StoredGeometry(level, indicesAt, positions, indices)
                    {
                        Vertices = declaration.CanWrite(PositionComponent)
                            ? new StoredVertices(declaration, vertexAt, buffers.SecondVertexAt, vertexCount, boneTransform)
                            : null,
                        Boxes = [.. held],
                        SphereAt = d + BoundsCenterOffset,
                        GeometryAt = geometry,
                        VertexBufferAt = Block(res, res.UInt64(geometry + 0x18), 0x40),
                        IndexBufferAt = Block(res, res.UInt64(geometry + 0x38), 0x40),
                        VertexAt = vertexAt,
                        Stride = declaration.Stride,
                        ShaderIndex = shaderIndex,
                        Texture = shader?.Textures.GetValueOrDefault(DiffuseSamplerHash),
                    });
                }
            }
        }
        return found;
    }

    // Where the pixels of the textures embedded in one model of the file are in its data.
    public static List<StoredTexture> StoredTextures(ReadOnlySpan<byte> data, ResourceHeader header, string extension, uint nameHash)
    {
        var res = Open(data, header);
        int d = FindDrawable(res, extension, nameHash);
        int group = d < 0 ? -1 : Block(res, res.UInt64(d + ShaderGroupPointer), 0x40);
        int dictionary = group < 0 ? -1 : Block(res, res.UInt64(group + EmbeddedTexturesPointer), 0x40);
        return dictionary >= 0 ? YtdReader.LocateDictionary(res, dictionary) : [];
    }

    // The drawable of the file (.ydr, .yft) or the one named nameHash (.ydd), -1 when none.
    private static int FindDrawable(ResourceData res, string extension, uint nameHash)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".ydr":
                return 0;
            case ".yft":
                return Block(res, res.UInt64(FragDrawablePointer), DrawableLength);
            case ".ydd":
                int hashCount = res.UInt16(0x28);
                int drawableCount = res.UInt16(0x38);
                int hashes = res.ResolveArray(res.UInt64(0x20), hashCount, 4, "noms des modèles");
                int pointers = res.ResolveArray(res.UInt64(0x30), drawableCount, 8, "modèles");
                int d = -1;
                for (int i = 0; i < hashCount && i < drawableCount; i++)
                {
                    if (res.UInt32(hashes + i * 4) == nameHash)
                        d = Block(res, res.UInt64(pointers + i * 8), DrawableLength);
                }
                return d;
            default:
                return -1;
        }
    }

    private static readonly uint DiffuseSamplerHash = Joaat.Hash("diffusesampler");

    // Versions of GTA V Enhanced (.ydr / .ydd and .yft; 165 and 162 in the original release).
    public const int EnhancedDrawableVersion = 159;
    public const int EnhancedFragmentVersion = 171;

    private static int Block(ResourceData res, ulong pointer, int length) =>
        pointer == 0 ? -1 : res.ResolveArray(pointer, 1, length, "bloc");

    // Drawable (rmcDrawable), at least 0xA8 bytes.
    private const int DrawableLength = 0xA8;
    private const int ShaderGroupPointer = 0x10;
    private const int BoundsCenterOffset = 0x20;
    private const int BoundsMinOffset = 0x30;
    private const int ModelBoundsPointer = 0x18;
    private const int BoxLength = 0x20;
    private const int BoundsMaxOffset = 0x40;
    private const int ModelsHigh = 0x50; // then medium, low and very low, 8 bytes apart
    private const int ModelsPointer = 0xA0;

    // Shader group: its own textures, then the shaders.
    private const int EmbeddedTexturesPointer = 0x08;
    private const int ShadersPointer = 0x10;
    private const int ShadersCount = 0x18;

    // Model: geometries and the shader of each, bone binding (byte 1: skinned, byte 3: bone index).
    private const int GeometriesPointer = 0x08;
    private const int GeometriesCount = 0x10;
    private const int ShaderMappingPointer = 0x20;
    private const int SkeletonBinding = 0x28;
    // Bit 0 clear: a proxy model (shadow or reflection only), never seen as it is.
    private const int RenderMaskFlags = 0x2C;

    // pose: where each bone is in the model (null: no skeleton). A model that is not skinned is stored relative to its
    // bone (pieces of breakable objects: planks of a bench, arm of a traffic light).
    // shaderSource: drawable whose shaders are used when d has none (the wheels of a vehicle use the body's).
    // bones: the skeleton the meshes then keep the bones of each vertex in; boneMap: from the drawable's own skeleton
    // (which pose comes from) to it, null when it is that one.
    private static ModelData? Convert(ResourceData res, uint hash, int d, Matrix4x4[]? pose = null, int shaderSource = -1,
        SkeletonData? bones = null, int[]? boneMap = null)
    {
        if (d < 0 || Models(res, d) is not { } models)
            return null;

        var shaders = Shaders(res, d);
        if (shaders.Count == 0 && shaderSource >= 0)
            shaders = Shaders(res, shaderSource);
        var meshes = new List<ModelMesh>();
        foreach (int model in models)
        {
            if (model < 0 || (res.UInt16(model + RenderMaskFlags) & 1) == 0)
                continue;
            uint binding = res.UInt32(model + SkeletonBinding);
            bool skinned = ((binding >> 8) & 0xFF) != 0;
            int bone = (int)(binding >> 24);
            var boneTransform = !skinned && pose is not null && bone < pose.Length ? pose[bone] : Matrix4x4.Identity;
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
                if (ConvertGeometry(res, geometry, shaderId < shaders.Count ? shaders[shaderId] : null, shaders) is not { } mesh)
                    continue;
                if (!boneTransform.IsIdentity)
                    mesh = Transform(mesh, boneTransform);
                if (bones is not null)
                {
                    try
                    {
                        mesh = WithBones(res, geometry, mesh, bones, boneMap, skinned, bone);
                    }
                    catch (InvalidDataException)
                    {
                        // Drawn still, in its default pose.
                    }
                }
                meshes.Add(mesh);
            }
        }
        FollowNeighbours(meshes);

        int group = Block(res, res.UInt64(d + ShaderGroupPointer), 0x40);
        int dictionary = group < 0 ? -1 : Block(res, res.UInt64(group + EmbeddedTexturesPointer), 0x40);
        return new ModelData
        {
            NameHash = hash,
            BoundsMin = Vec3(res, d + BoundsMinOffset),
            BoundsMax = Vec3(res, d + BoundsMaxOffset),
            Meshes = meshes,
            EmbeddedTextures = dictionary >= 0 ? YtdReader.ReadDictionary(res, dictionary) : new Dictionary<uint, TextureData>(),
            Skeleton = meshes.Any(m => m.BoneIndices is not null) ? bones : null,
        };
    }

    // The bones of each vertex: from the vertices of a skinned geometry (blend indices, numbered in the geometry's own
    // bone list when it has one shorter than the skeleton's, and weights), or the one bone a rigid part is placed on.
    // map: from the bones of the drawable's skeleton to those of the model's (null: the same).
    private static ModelMesh WithBones(ResourceData res, int g, ModelMesh mesh, SkeletonData bones, int[]? map, bool skinned, int bone)
    {
        int count = mesh.Positions.Length;
        int sourceCount = map?.Length ?? bones.Count;
        var indices = new ulong[count];
        var weights = new Vector4[count];
        if (!skinned)
        {
            if (bone >= sourceCount)
                return mesh;
            Array.Fill(indices, (ulong)(map?[bone] ?? bone));
            Array.Fill(weights, Vector4.UnitX);
            return WithBones(mesh, indices, weights);
        }
        if (Buffers(res, g) is not { } found || found.VertexCount != count
            || !found.Declaration.Has(BlendWeightsComponent) || !found.Declaration.Has(BlendIndicesComponent))
            return mesh;
        int idCount = res.UInt16(g + GeometryBoneIdsCount);
        ulong idPointer = res.UInt64(g + GeometryBoneIdsPointer);
        int ids = idCount > 0 && idCount != sourceCount && idPointer != 0 ? res.ResolveArray(idPointer, idCount, 2, "os de la géométrie") : -1;
        var declaration = found.Declaration;
        ReadOnlySpan<byte> bytes = res.Bytes(found.VertexAt, found.VertexLength);
        for (int v = 0; v < count; v++)
        {
            int start = v * declaration.Stride;
            var w = declaration.Read(bytes, start, BlendWeightsComponent);
            var b = declaration.Read(bytes, start, BlendIndicesComponent);
            if (MathF.Round(b.Z * 255f) >= 255f)
            {
                // Moved by the game's cloth simulation (between three cloth points), not by bones: see FollowNeighbours.
                indices[v] = ClothVertex;
                continue;
            }
            ulong packed = 0;
            for (int k = 0; k < 4; k++)
            {
                int index = (int)MathF.Round(b[k] * 255f);
                if (ids >= 0)
                    index = index < idCount ? res.UInt16(ids + index * 2) : 0;
                if (index >= sourceCount)
                {
                    index = 0;
                    w[k] = 0f;
                }
                packed |= (ulong)(map?[index] ?? index) << (16 * k);
            }
            float sum = w.X + w.Y + w.Z + w.W;
            indices[v] = packed;
            weights[v] = sum > 1e-4f ? w / sum : Vector4.UnitX;
        }
        return WithBones(mesh, indices, weights);
    }

    private const ulong ClothVertex = ulong.MaxValue;

    // Cloth vertices (jacket tails...) take the bones of the nearest vertex moved by bones in the model: they follow the
    // body without the game's cloth simulation.
    private static void FollowNeighbours(List<ModelMesh> meshes)
    {
        if (!meshes.Any(m => m.BoneIndices is { } b && Array.IndexOf(b, ClothVertex) >= 0))
            return;
        var donors = new List<(Vector3 Position, ulong Bones, Vector4 Weights)>();
        foreach (var mesh in meshes)
        {
            for (int v = 0; mesh.BoneIndices is { } bones && v < bones.Length; v++)
            {
                if (bones[v] != ClothVertex)
                    donors.Add((mesh.Positions[v], bones[v], mesh.BoneWeights![v]));
            }
        }
        foreach (var mesh in meshes)
        {
            for (int v = 0; mesh.BoneIndices is { } bones && v < bones.Length; v++)
            {
                if (bones[v] != ClothVertex)
                    continue;
                (ulong Bones, Vector4 Weights) nearest = (0, Vector4.UnitX);
                float best = float.MaxValue;
                foreach (var donor in donors)
                {
                    float d = Vector3.DistanceSquared(donor.Position, mesh.Positions[v]);
                    if (d < best)
                        (best, nearest) = (d, (donor.Bones, donor.Weights));
                }
                bones[v] = nearest.Bones;
                mesh.BoneWeights![v] = nearest.Weights;
            }
        }
    }

    private static ModelMesh WithBones(ModelMesh mesh, ulong[] indices, Vector4[] weights) => new()
    {
        Positions = mesh.Positions,
        Normals = mesh.Normals,
        TexCoords = mesh.TexCoords,
        Colors = mesh.Colors,
        Indices = mesh.Indices,
        BoneIndices = indices,
        BoneWeights = weights,
        DiffuseTextureName = mesh.DiffuseTextureName,
        BlendMode = mesh.BlendMode,
        Emission = mesh.Emission,
        Terrain = mesh.Terrain,
        VertexColored = mesh.VertexColored,
        TintTextureName = mesh.TintTextureName,
        TintTexCoords = mesh.TintTexCoords,
        ShaderHash = mesh.ShaderHash,
        ShaderTextures = mesh.ShaderTextures,
        DiffuseColor = mesh.DiffuseColor,
        SpecularIntensity = mesh.SpecularIntensity,
        SpecularFalloff = mesh.SpecularFalloff,
    };

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
    private sealed record Shader(uint Name, uint FileName, byte RenderBucket, Dictionary<uint, string> Textures, Dictionary<uint, Vector4> Vectors);

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
            shaders.Add(s < 0 ? null : res.Enhanced ? ReadEnhancedShader(res, s) : ReadShader(res, s));
        }
        return shaders;
    }

    // Shader (grmShaderFx): 0x00 parameters, 0x08 name hash, 0x10 parameter count, 0x11 render bucket, 0x18 hash of
    // its preset file (e.g. "trees_normal_spec.sps"). Parameters: 16 bytes each (type, pointer: type 0 = texture, N =
    // N vectors), then the vectors, then the name hash of each parameter.
    private static Shader ReadShader(ResourceData res, int s)
    {
        var textures = new Dictionary<uint, string>();
        var values = new Dictionary<uint, Vector4>();
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
                if (texture == 0)
                    continue;
                if (res.Byte(p) != 0)
                {
                    // First vector of a value parameter.
                    int v = res.ResolveArray(texture, 1, 16, "valeur de shader");
                    values.TryAdd(res.UInt32(hashes + i * 4), new Vector4(F32(res, v), F32(res, v + 4), F32(res, v + 8), F32(res, v + 12)));
                    continue;
                }
                // Texture reference: its name pointer is at 0x28, as in a full texture.
                if (res.Text(res.UInt64(res.ResolveArray(texture, 1, 0x30, "texture") + 0x28), "nom de texture") is { } name)
                    textures.TryAdd(res.UInt32(hashes + i * 4), name.ToLowerInvariant());
            }
        }
        return new Shader(res.UInt32(s + 0x08), res.UInt32(s + 0x18), res.Byte(s + 0x11), textures, values);
    }

    // Enhanced shader (0x40 bytes): 0x00 name hash, 0x08 constant buffers (one pointer each), 0x10 texture pointers,
    // 0x20 parameter table, 0x39 render bucket. Table: 0x00 buffer count, 0x01 texture count, 0x04 parameter count, then
    // 8 bytes per parameter: name hash, then bits 0-1 kind (0 texture, 3 constant), 2-9 texture index (2-7 buffer
    // index for a constant), 8-19 offset in the buffer, 20-31 length.
    private static Shader ReadEnhancedShader(ResourceData res, int s)
    {
        var textures = new Dictionary<uint, string>();
        var values = new Dictionary<uint, Vector4>();
        uint name = res.UInt32(s);
        ulong tablePointer = res.UInt64(s + 0x20);
        if (tablePointer != 0)
        {
            int table = res.ResolveArray(tablePointer, 1, 8, "paramètres de shader");
            int bufferCount = res.Byte(table);
            int textureCount = res.Byte(table + 1);
            int count = res.Byte(table + 4);
            int items = res.ResolveArray(tablePointer + 8, count, 8, "paramètres de shader");
            ulong buffersPointer = res.UInt64(s + 0x08);
            ulong texturesPointer = res.UInt64(s + 0x10);
            int buffers = bufferCount > 0 && buffersPointer != 0 ? res.ResolveArray(buffersPointer, bufferCount, 8, "valeurs de shader") : -1;
            int texturePointers = textureCount > 0 && texturesPointer != 0 ? res.ResolveArray(texturesPointer, textureCount, 8, "textures de shader") : -1;
            for (int i = 0; i < count; i++)
            {
                uint hash = res.UInt32(items + i * 8);
                hash = EnhancedParameterNames.GetValueOrDefault(hash, hash);
                uint info = res.UInt32(items + i * 8 + 4);
                switch (info & 3)
                {
                    case 0:
                    {
                        int index = (int)((info >> 2) & 0xFF);
                        if (texturePointers < 0 || index >= textureCount || res.UInt64(texturePointers + index * 8) is not (> 0 and var texture))
                            break;
                        if (res.Text(res.UInt64(res.ResolveArray(texture, 1, 0x30, "texture") + 0x28), "nom de texture") is { } textureName)
                            textures.TryAdd(hash, textureName.ToLowerInvariant());
                        break;
                    }
                    case 3:
                    {
                        int index = (int)((info >> 2) & 0x3F);
                        int offset = (int)((info >> 8) & 0xFFF);
                        int floats = Math.Min(4, (int)(info >> 20) / 4);
                        if (buffers < 0 || index >= bufferCount || floats == 0 || res.UInt64(buffers + index * 8) is not (> 0 and var buffer))
                            break;
                        int v = res.ResolveArray(buffer + (ulong)offset, floats, 4, "valeur de shader");
                        var value = new Vector4(F32(res, v), floats > 1 ? F32(res, v + 4) : 0, floats > 2 ? F32(res, v + 8) : 0,
                            floats > 3 ? F32(res, v + 12) : 0);
                        values.TryAdd(hash, value);
                        break;
                    }
                }
            }
        }
        return new Shader(name, EnhancedPresets.GetValueOrDefault(name), res.Byte(s + 0x39), textures, values);
    }

    // Enhanced renames the shader parameters (from the shader sources): back to the names of the original release,
    // which everything else uses.
    private static readonly Dictionary<uint, uint> EnhancedParameterNames = new (string Enhanced, string Original)[]
    {
        ("DiffuseTex", "DiffuseSampler"), ("DiffuseTex2", "DiffuseSampler2"), ("DiffuseHfTex", "DiffuseHfSampler"),
        ("BumpTex", "BumpSampler"), ("SpecularTex", "SpecSampler"), ("DetailTex", "DetailSampler"),
        ("DirtTex", "DirtSampler"), ("damagetexture", "DamageSampler"), ("TintPaletteTex", "TintPaletteSampler"),
        ("PlateBgTex", "PlateBgSampler"), ("heightTexture", "heightSampler"), ("lookupTexture", "lookupSampler"),
        ("DiffuseTexture_layer0", "TextureSampler_layer0"), ("DiffuseTexture_layer1", "TextureSampler_layer1"),
        ("DiffuseTexture_layer2", "TextureSampler_layer2"), ("DiffuseTexture_layer3", "TextureSampler_layer3"),
        ("BumpTexture_layer0", "BumpSampler_layer0"), ("BumpTexture_layer1", "BumpSampler_layer1"),
        ("BumpTexture_layer2", "BumpSampler_layer2"), ("BumpTexture_layer3", "BumpSampler_layer3"),
        ("DiffuseColor", "matDiffuseColor"), ("DiffuseColor2", "matDiffuseColor2"),
        ("SpecularColor", "specularIntensityMult"), ("Specular", "specularFalloffMult"), ("Bumpiness", "bumpiness"),
        ("EmissiveMultiplier", "emissiveMultiplier"),
    }.ToDictionary(p => Joaat.Hash(p.Enhanced), p => Joaat.Hash(p.Original));

    // Enhanced shaders no longer name their preset file: the tree presets carry the shader's name.
    private static readonly Dictionary<uint, uint> EnhancedPresets = TreePresetNames.ToDictionary(Joaat.Hash, n => Joaat.Hash(n + ".sps"));

    // Shader parameters holding the colour texture, by priority. Terrain shaders blend several layers: the
    // first one is the colour texture, the others are in TerrainBlend.
    private static readonly uint[] ColorSamplers = [Joaat.Hash("DiffuseSampler"), Joaat.Hash("TextureSampler_layer0")];

    private static readonly uint[] TerrainLayers =
        [Joaat.Hash("TextureSampler_layer1"), Joaat.Hash("TextureSampler_layer2"), Joaat.Hash("TextureSampler_layer3")];

    private static readonly uint TerrainMask = Joaat.Hash("lookupSampler");

    private static readonly uint DiffuseColorParameter = Joaat.Hash("matDiffuseColor");

    private static readonly uint SpikedHairPreset = Joaat.Hash("ped_hair_spiked.sps");
    private static readonly uint OrderNumberParameter = Joaat.Hash("orderNumber");
    private static readonly uint SpecularIntensityParameter = Joaat.Hash("specularIntensityMult");
    private static readonly uint SpecularFalloffParameter = Joaat.Hash("specularFalloffMult");

    // Terrain shaders taking their weights from the vertex colour only, or from the mask only.
    private static readonly uint TerrainVertexOnlyShader = Joaat.Hash("terrain_cb_w_4lyr_lod");

    private static readonly HashSet<uint> TerrainMaskOnlyShaders = new[]
    {
        "terrain_cb_w_4lyr_cm", "terrain_cb_w_4lyr_cm_tnt", "terrain_cb_w_4lyr_cm_pxm", "terrain_cb_w_4lyr_cm_pxm_tnt",
    }.Select(Joaat.Hash).ToHashSet();

    // Mirror surfaces show a live reflection in the game, not their placeholder texture: not drawn for now.
    // Simple shapes inside trees only cast the foliage's shadow in the game (white balls if drawn).
    // Normal-only decals only bend the lighting of what is under them; their colour slot holds the normal map (purple
    // patches if drawn).
    private static readonly HashSet<uint> HiddenShaders = new[]
    {
        "mirror_default", "mirror_decal", "mirror_crack", "trees_shadow_proxy", "decal_normal_only",
    }.Select(Joaat.Hash).ToHashSet();

    // Shaders without texture, coloured by the vertex colours.
    private static readonly uint VertexColorShader = Joaat.Hash("cpv_only");
    private static readonly uint TintSampler = Joaat.Hash("DiffuseHfSampler");

    // Presets of the tree and bush shaders (hash of the file name).
    private static readonly HashSet<uint> TreePresets = TreePresetNames.Select(n => Joaat.Hash(n + ".sps")).ToHashSet();

    // A property, so that the fields above can use it (fields are initialised in order).
    private static string[] TreePresetNames =>
    [
        "trees", "trees_camera_aligned", "trees_camera_facing", "trees_lod", "trees_lod2", "trees_lod2d", "trees_lod_tnt",
        "trees_normal", "trees_normal_diffspec", "trees_normal_diffspec_tnt", "trees_normal_spec",
        "trees_normal_spec_camera_aligned", "trees_normal_spec_camera_aligned_tnt", "trees_normal_spec_camera_facing",
        "trees_normal_spec_camera_facing_tnt", "trees_normal_spec_tnt", "trees_normal_spec_wind", "trees_shadow_proxy",
        "trees_tnt",
    ];

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

    // Geometry: 0x18 vertex buffer, 0x38 index buffer, 0x68 bones its vertices are numbered in (u16 each) and 0x72 count.
    private const int GeometryLength = 0x98;
    private const int GeometryBoneIdsPointer = 0x68;
    private const int GeometryBoneIdsCount = 0x72;

    // Vertex components (bit index in the vertex declaration flags).
    private const int PositionComponent = 0;
    private const int BlendWeightsComponent = 1;
    private const int BlendIndicesComponent = 2;
    private const int NormalComponent = 3;
    private const int ColorComponent = 4;
    private const int SecondColorComponent = 5;
    private const int TexCoordComponent = 6;
    private const int SecondTexCoordComponent = 7;
    private const int TangentComponent = 14;
    private const int BinormalComponent = 15;

    // Where a geometry's vertices and indices are in the data, null when it has nothing to draw.
    private readonly record struct GeometryBuffers(VertexDeclaration Declaration, int VertexAt, int VertexLength, int VertexCount,
        int IndicesAt, int IndexCount)
    {
        // A second copy of the vertices (original release), -1 when none.
        public int SecondVertexAt { get; init; } = -1;
    }

    private static GeometryBuffers? Buffers(ResourceData res, int g)
    {
        // Vertex buffer: 0x08 stride, 0x10 data, 0x18 count, 0x20 second copy of the data, 0x30 declaration.
        // Enhanced: 0x08 count, 0x0C stride, 0x18 data, 0x38 declaration.
        bool enhanced = res.Enhanced;
        int buffer = Block(res, res.UInt64(g + 0x18), 0x40);
        if (buffer < 0)
            return null;
        ulong dataPointer = res.UInt64(buffer + (enhanced ? 0x18 : 0x10));
        if (dataPointer == 0 && !enhanced)
            dataPointer = res.UInt64(buffer + 0x20);
        if (dataPointer == 0)
            return null;
        int count = (int)res.UInt32(buffer + (enhanced ? 0x08 : 0x18));
        int bufferStride = res.UInt16(buffer + (enhanced ? 0x0C : 0x08));
        ulong declarationPointer = res.UInt64(buffer + (enhanced ? 0x38 : 0x30));
        int declarationOffset = Block(res, declarationPointer, enhanced ? VertexDeclaration.EnhancedLength : 16)
            is >= 0 and var found ? found : throw new InvalidDataException("Format des sommets absent.");
        var declaration = enhanced
            ? VertexDeclaration.Enhanced(res, declarationOffset, bufferStride)
            : VertexDeclaration.Original(res.UInt32(declarationOffset), res.UInt16(declarationOffset + 4), res.UInt64(declarationOffset + 8));
        int vertexLength = count * bufferStride;
        int vertexAt = vertexLength == 0 ? 0 : res.ResolveArray(dataPointer, vertexLength, 1, "sommets");

        // Index buffer: 0x08 count, 0x10 indices (16 bits); Enhanced: indices at 0x18.
        int indexBuffer = Block(res, res.UInt64(g + 0x38), enhanced ? 0x40 : 0x60);
        if (indexBuffer < 0)
            return null;
        int indexCount = (int)res.UInt32(indexBuffer + 0x08);
        ulong indicesPointer = res.UInt64(indexBuffer + (enhanced ? 0x18 : 0x10));
        if (indexCount == 0 || indicesPointer == 0)
            return null;
        int indicesAt = res.ResolveArray(indicesPointer, indexCount, 2, "indices");

        int stride = declaration.Stride;
        if (count == 0 || stride == 0 || (long)count * stride > vertexLength)
            return null;
        int secondAt = -1;
        ulong secondPointer = enhanced ? 0 : res.UInt64(buffer + 0x20);
        if (secondPointer != 0 && secondPointer != dataPointer)
            secondAt = res.ResolveArray(secondPointer, vertexLength, 1, "sommets");
        return new GeometryBuffers(declaration, vertexAt, vertexLength, count, indicesAt, indexCount) { SecondVertexAt = secondAt };
    }

    private static ModelMesh? ConvertGeometry(ResourceData res, int g, Shader? shader, List<Shader?> shaders)
    {
        if (Buffers(res, g) is not { } found)
            return null;
        var (declaration, vertexAt, vertexLength, count, indicesAt, indexCount) = found;
        ReadOnlySpan<byte> bytes = res.Bytes(vertexAt, vertexLength);
        ReadOnlySpan<byte> indexBytes = res.Bytes(indicesAt, indexCount * 2);

        if (shader is not null && HiddenShaders.Contains(shader.Name))
            return null;
        // Hair layers with an order number above 0 are extra layers the game leaves out by default (drawn, they make
        // a thick shell with gaps over the hair).
        if (shader is not null && shader.FileName == SpikedHairPreset && shader.Vectors.TryGetValue(OrderNumberParameter, out var order) && order.X > 0f)
            return null;
        // In trees and bushes, the untextured heart (cpv_only) keeps other data than a colour in its vertex colours
        // (drawn with them, it is a bright yellow ball): not drawn.
        if (shader?.Name == VertexColorShader && shaders.Any(s => s is not null && TreePresets.Contains(s.FileName)))
            return null;

        int stride = declaration.Stride;

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

        // grass_fur: large-scale colour of the patch, multiplied over the tiled grass (without it, far too light).
        string? tintName = shader?.Textures.GetValueOrDefault(TintSampler);
        Vector2[]? tintCoords = null;
        if (tintName is not null)
        {
            tintCoords = texCoords;
            if (declaration.Has(SecondTexCoordComponent))
            {
                tintCoords = new Vector2[count];
                for (int v = 0; v < count; v++)
                {
                    var uv = declaration.Read(bytes, v * stride, SecondTexCoordComponent);
                    tintCoords[v] = new Vector2(uv.X, uv.Y);
                }
            }
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
            TintTextureName = tintName,
            TintTexCoords = tintCoords,
            ShaderHash = shader?.Name ?? 0,
            ShaderTextures = shader?.Textures ?? (IReadOnlyDictionary<uint, string>)new Dictionary<uint, string>(),
            DiffuseColor = shader is not null && shader.Vectors.TryGetValue(DiffuseColorParameter, out var diffuse) ? diffuse : null,
            SpecularIntensity = shader is not null && shader.Vectors.TryGetValue(SpecularIntensityParameter, out var intensity) ? intensity.X : 1f,
            SpecularFalloff = shader is not null && shader.Vectors.TryGetValue(SpecularFalloffParameter, out var falloff) ? falloff.X : 100f,
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

    // Where each vertex component (numbered as in the original release: 0 position, 3 normal, 4 and 5 colours, 6 to 13
    // texture coordinates...) is in a vertex, and how it is stored.
    internal sealed class VertexDeclaration
    {
        private const int Components = 16;
        private readonly int[] _offsets = new int[Components];
        private readonly int[] _types = new int[Components];

        public int Stride { get; private init; }

        // Original release: one flag bit per component present, 4 bits of storage type per component; components follow
        // one another in bit order.
        public static VertexDeclaration Original(uint flags, int stride, ulong types)
        {
            var declaration = new VertexDeclaration { Stride = stride };
            int offset = 0;
            for (int k = 0; k < Components; k++)
            {
                declaration._offsets[k] = -1;
                if (((flags >> k) & 1) == 0)
                    continue;
                int type = (int)((types >> (k * 4)) & 0xF);
                declaration._offsets[k] = offset;
                declaration._types[k] = type;
                offset += Size(type);
            }
            return declaration;
        }

        // Enhanced (0x140 bytes): offset of each of the 52 semantics (uint32), then 52 bytes of stride, then 52 bytes of
        // format (0 = absent).
        public const int EnhancedLength = 0x140;

        public static VertexDeclaration Enhanced(ResourceData res, int at, int stride)
        {
            var declaration = new VertexDeclaration { Stride = stride };
            Array.Fill(declaration._offsets, -1);
            for (int semantic = 0; semantic < 52; semantic++)
            {
                int format = res.Byte(at + 260 + semantic);
                int component = ComponentOf(semantic);
                if (format == 0 || component < 0)
                    continue;
                declaration._offsets[component] = (int)res.UInt32(at + semantic * 4);
                declaration._types[component] = format switch
                {
                    2 => 7,   // float4
                    6 => 6,   // float3
                    10 => 3,  // half4
                    16 => 5,  // float2
                    34 => 1,  // half2
                    28 => 9,  // rgba8 unorm
                    24 => 11, // 10-10-10-2
                    _ => 0,
                };
            }
            return declaration;
        }

        // Semantics: position, normal, tangent, blend weights, blend indices, colours 0-1, texture coordinates 0-5.
        private static int ComponentOf(int semantic) => semantic switch
        {
            0 => 0, 4 => 3, 8 => 14, 16 => 1, 20 => 2, 24 => 4, 25 => 5,
            >= 28 and <= 33 => semantic - 22,
            _ => -1,
        };

        public bool Has(int component) => _offsets[component] >= 0;

        // Bytes of each storage type: half2, float, half4, (unused), float2, float3, float4, ubyte4, colour, rgba8 snorm.
        private static int Size(int type) => type switch
        {
            1 => 4, 2 => 4, 3 => 8, 5 => 8, 6 => 12, 7 => 16, 8 => 4, 9 => 4, 10 => 4,
            _ => 0,
        };

        public Vector4 Read(ReadOnlySpan<byte> bytes, int vertexStart, int component)
        {
            if (!Has(component))
                return Vector4.Zero;
            int o = vertexStart + _offsets[component];
            return _types[component] switch
            {
                2 => new Vector4(F(bytes, o), 0, 0, 0),
                5 => new Vector4(F(bytes, o), F(bytes, o + 4), 0, 0),
                6 => new Vector4(F(bytes, o), F(bytes, o + 4), F(bytes, o + 8), 0),
                7 => new Vector4(F(bytes, o), F(bytes, o + 4), F(bytes, o + 8), F(bytes, o + 12)),
                1 => new Vector4(H(bytes, o), H(bytes, o + 2), 0, 0),
                3 => new Vector4(H(bytes, o), H(bytes, o + 2), H(bytes, o + 4), H(bytes, o + 6)),
                9 => new Vector4(B(bytes, o), B(bytes, o + 1), B(bytes, o + 2), B(bytes, o + 3)),
                11 => Packed(bytes, o),
                _ => Vector4.Zero,
            };
        }

        // Like Write, plus texture coordinates and colours (half2, float2, colour).
        public void WriteValue(Span<byte> bytes, int vertexStart, int component, Vector4 value)
        {
            if (!Has(component))
                return;
            int o = vertexStart + _offsets[component];
            switch (_types[component])
            {
                case 1:
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[o..], (Half)value.X);
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[(o + 2)..], (Half)value.Y);
                    break;
                case 5:
                    BinaryPrimitives.WriteSingleLittleEndian(bytes[o..], value.X);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 4)..], value.Y);
                    break;
                case 9:
                    static byte Byte(float x) => (byte)Math.Clamp(MathF.Round(x * 255f), 0f, 255f);
                    bytes[o] = Byte(value.X);
                    bytes[o + 1] = Byte(value.Y);
                    bytes[o + 2] = Byte(value.Z);
                    bytes[o + 3] = Byte(value.W);
                    break;
                default:
                    Write(bytes, vertexStart, component, value);
                    break;
            }
        }

        // Storage types Write knows: float3, float4, half4, packed 10-10-10-2.
        public bool CanWrite(int component) => Has(component) && _types[component] is 3 or 6 or 7 or 11;

        public void Write(Span<byte> bytes, int vertexStart, int component, Vector4 value)
        {
            if (!CanWrite(component))
                return;
            int o = vertexStart + _offsets[component];
            switch (_types[component])
            {
                case 6:
                case 7:
                    BinaryPrimitives.WriteSingleLittleEndian(bytes[o..], value.X);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 4)..], value.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 8)..], value.Z);
                    if (_types[component] == 7)
                        BinaryPrimitives.WriteSingleLittleEndian(bytes[(o + 12)..], value.W);
                    break;
                case 3:
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[o..], (Half)value.X);
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[(o + 2)..], (Half)value.Y);
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[(o + 4)..], (Half)value.Z);
                    BinaryPrimitives.WriteHalfLittleEndian(bytes[(o + 6)..], (Half)value.W);
                    break;
                case 11:
                    static uint Unsigned(float x) => (uint)Math.Clamp(MathF.Round((x + 1f) * 0.5f * 1023f), 0f, 1023f);
                    uint packed = Unsigned(value.X) | (Unsigned(value.Y) << 10) | (Unsigned(value.Z) << 20)
                        | ((uint)Math.Clamp(MathF.Round(value.W * 3f), 0f, 3f) << 30);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[o..], packed);
                    break;
            }
        }

        private static float F(ReadOnlySpan<byte> b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b[at..]) : 0f;
        private static float H(ReadOnlySpan<byte> b, int at) => at + 2 <= b.Length ? (float)BinaryPrimitives.ReadHalfLittleEndian(b[at..]) : 0f;
        private static float B(ReadOnlySpan<byte> b, int at) => at < b.Length ? b[at] / 255f : 0f;

        // Three 10-bit values from -1 to 1 and a 2-bit one.
        private static Vector4 Packed(ReadOnlySpan<byte> b, int at)
        {
            if (at + 4 > b.Length)
                return Vector4.Zero;
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
            static float Signed(uint x) => x / 1023f * 2f - 1f;
            return new Vector4(Signed(v & 0x3FF), Signed((v >> 10) & 0x3FF), Signed((v >> 20) & 0x3FF), (v >> 30) / 3f);
        }
    }

    // ---- Skeleton ----

    // Where each bone is in the model, from the drawable's skeleton (0x18): 0x30 bone matrices relative to the parent
    // bone, 0x38 parent indices, 0x5E bone count. Null without a skeleton.
    private static Matrix4x4[]? SkeletonPose(ResourceData res, int d)
    {
        if (d < 0)
            return null;
        int skeleton = Block(res, res.UInt64(d + 0x18), 0x70);
        if (skeleton < 0)
            return null;
        int count = res.UInt16(skeleton + 0x5E);
        ulong matricesPointer = res.UInt64(skeleton + 0x30);
        ulong parentsPointer = res.UInt64(skeleton + 0x38);
        if (count == 0 || matricesPointer == 0)
            return null;
        int matrices = res.ResolveArray(matricesPointer, count, 64, "os du squelette");
        int parents = parentsPointer == 0 ? -1 : res.ResolveArray(parentsPointer, count, 2, "parents des os");

        var local = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            local[i] = Matrix(res, matrices + i * 64);
            // The 4th column holds something else than a projection.
            local[i].M14 = local[i].M24 = local[i].M34 = 0f;
            local[i].M44 = 1f;
        }

        // Bones are not always stored parents first: each one goes up its own chain (depth capped against loops).
        var pose = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var transform = local[i];
            int parent = parents < 0 ? -1 : (short)res.UInt16(parents + i * 2);
            for (int depth = 0; parent >= 0 && parent < count && depth < count; depth++)
            {
                transform *= local[parent];
                parent = (short)res.UInt16(parents + parent * 2);
            }
            pose[i] = transform;
        }
        return pose;
    }

    // The bones of the drawable's skeleton (0x20: 0x50 bytes each: 0x00 rotation x, y, z, w, 0x10 position, 0x20 scale,
    // 0x44 tag), null without one.
    private static SkeletonData? Skeleton(ResourceData res, int d)
    {
        if (d < 0 || SkeletonPose(res, d) is not { } pose)
            return null;
        int skeleton = Block(res, res.UInt64(d + 0x18), 0x70);
        int count = res.UInt16(skeleton + 0x5E);
        ulong bonesPointer = res.UInt64(skeleton + 0x20);
        ulong parentsPointer = res.UInt64(skeleton + 0x38);
        if (bonesPointer == 0 || parentsPointer == 0)
            return null;
        int bones = res.ResolveArray(bonesPointer, count, 0x50, "os du squelette");
        int parentsAt = res.ResolveArray(parentsPointer, count, 2, "parents des os");
        var tags = new ushort[count];
        var parents = new int[count];
        var rotations = new Quaternion[count];
        var translations = new Vector3[count];
        var scales = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int b = bones + i * 0x50;
            rotations[i] = Quaternion.Normalize(new Quaternion(F32(res, b), F32(res, b + 4), F32(res, b + 8), F32(res, b + 12)));
            translations[i] = Vec3(res, b + 0x10);
            scales[i] = Vec3(res, b + 0x20);
            tags[i] = res.UInt16(b + 0x44);
            int parent = (short)res.UInt16(parentsAt + i * 2);
            parents[i] = parent >= 0 && parent < count && parent != i ? parent : -1;
        }
        return new SkeletonData { Tags = tags, Parents = parents, Rotations = rotations, Translations = translations, Scales = scales, BindPose = pose };
    }

    // For each bone of a skeleton, the bone of the same tag in another one; a bone it lacks takes its nearest ancestor's.
    private static int[] BoneMap(SkeletonData from, SkeletonData to)
    {
        var map = new int[from.Count];
        for (int i = 0; i < from.Count; i++)
        {
            int found = -1;
            for (int b = i, depth = 0; b >= 0 && found < 0 && depth <= from.Count; b = from.Parents[b], depth++)
                found = to.IndexOf(from.Tags[b]);
            map[i] = Math.Max(found, 0);
        }
        return map;
    }

    // Default pose of a fragment's bones, in the model (0xA8: 0x10 count, 0x20 matrices of 3 rows x 4 floats, the
    // 4th float of each row being the translation). Null when absent.
    private static Matrix4x4[]? FragmentPose(ResourceData res)
    {
        int block = Block(res, res.UInt64(FragBoneTransformsPointer), 0x20);
        if (block < 0)
            return null;
        int count = res.Byte(block + 0x10);
        if (count == 0)
            return null;
        int items = res.ResolveArray(res.UInt64(FragBoneTransformsPointer) + 0x20, count, 48, "pose du fragment");
        var pose = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            int o = items + i * 48;
            pose[i] = new Matrix4x4(
                F32(res, o), F32(res, o + 16), F32(res, o + 32), 0f,
                F32(res, o + 4), F32(res, o + 20), F32(res, o + 36), 0f,
                F32(res, o + 8), F32(res, o + 24), F32(res, o + 40), 0f,
                F32(res, o + 12), F32(res, o + 28), F32(res, o + 44), 1f);
        }
        return pose;
    }

    // ---- Fragments (vehicles) ----

    // Fragment: 0x30 main drawable, 0xA8 default pose of the bones, 0xF0 physics levels (0x10: the first level).
    private const int FragDrawablePointer = 0x30;
    private const int FragBoneTransformsPointer = 0xA8;
    private const int FragPhysicsPointer = 0xF0;

    // Bone tags of the wheels: left / right, front, middle (6-wheelers and more) and rear.
    private static readonly HashSet<ushort> FrontWheelBones = [27922, 26418];
    private static readonly HashSet<ushort> RearWheelBones = [29921, 29922, 29923, 27902, 5857, 5858, 5859, 26398];
    private static readonly HashSet<ushort> RightWheelBones = [26418, 5857, 5858, 5859, 26398];

    // Adds the wheels of a vehicle to its body.
    private static ModelData AddWheels(ResourceData res, ModelData body, int main)
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
            own[i] = Convert(res, 0, drawables[i], shaderSource: main);
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
        TintTextureName = mesh.TintTextureName,
        TintTexCoords = mesh.TintTexCoords,
        ShaderHash = mesh.ShaderHash,
        ShaderTextures = mesh.ShaderTextures,
        DiffuseColor = mesh.DiffuseColor,
        SpecularIntensity = mesh.SpecularIntensity,
        SpecularFalloff = mesh.SpecularFalloff,
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
