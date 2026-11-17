using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.Data.Native.Classic;

// Reads the models of the first trilogy (".dff", a RenderWare clump): frames (a hierarchy of matrices with names),
// geometries (vertices, pre-lit colours, texture coordinates, triangles with a material each, materials naming their
// texture) and atomics (a geometry placed on a frame). Only the most detailed version is kept: atomics of damaged
// parts ("_dam") and of lower detail levels ("_lo", "_vlo", "_l1"...) are left out.
public static class ClassicModels
{
    private const uint Prelit = 0x08, Textured = 0x04, Textured2 = 0x80, Native = 0x01000000;
    private const int AtomicRender = 0x04;
    // San Andreas: second set of pre-lit colours, for the night (lit windows, walls under street lamps).
    private const uint NightColorsPlugin = 0x253F2F9;

    // wheel: for a vehicle without its own wheel, the meshes of the wheel to put on its "wheel_xx_dummy" frames, sized by
    // wheelScale.
    public static ModelData? ReadClump(byte[] data, uint nameHash, IReadOnlyList<ModelMesh>? wheel = null, float wheelScale = 1f)
    {
        // Animated textures (San Andreas waterfalls, neon signs) come in a section before the clump.
        var top = RwChunk.At(data, 0, data.Length);
        while (top is { Type: not RwChunk.Clump } skipped)
            top = RwChunk.At(data, skipped.End, data.Length);
        if (top is not { } clump)
            throw new InvalidDataException("pas un modèle RenderWare (clump)");
        var children = clump.Children(data);
        var frames = children.FirstOrDefault(c => c.Type == RwChunk.FrameList) is { Type: RwChunk.FrameList } frameList
            ? ReadFrames(data, frameList)
            : [];
        var geometries = children.FirstOrDefault(c => c.Type == RwChunk.GeometryList) is { Type: RwChunk.GeometryList } geometryList
            ? geometryList.Children(data).Where(c => c.Type == RwChunk.Geometry).ToList()
            : [];

        var atomics = new List<(int Frame, int Geometry)>();
        foreach (var atomic in children.Where(c => c.Type == RwChunk.Atomic))
        {
            if (atomic.Child(data, RwChunk.Struct) is not { Size: >= 16 } s)
                continue;
            int frame = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset));
            int geometry = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset + 4));
            int flags = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset + 8));
            if (geometry < 0 || geometry >= geometries.Count || (flags & AtomicRender) == 0)
                continue;
            atomics.Add((frame, geometry));
        }

        // Lowest "_lN" level of each part; parts without a level are always kept.
        string Name(int frame) => frame >= 0 && frame < frames.Count ? frames[frame].Name : "";
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (frame, _) in atomics)
        {
            var (part, level) = DetailLevel(Name(frame));
            if (level >= 0 && (!best.TryGetValue(part, out int current) || level < current))
                best[part] = level;
        }

        var meshes = new List<ModelMesh>();
        foreach (var (frame, geometry) in atomics)
        {
            string name = Name(frame);
            if (IsLeftOut(name))
                continue;
            var (part, level) = DetailLevel(name);
            if (level >= 0 && best[part] != level)
                continue;
            var transform = frame >= 0 && frame < frames.Count ? frames[frame].World : Matrix4x4.Identity;
            meshes.AddRange(ReadGeometry(data, geometries[geometry], transform));
        }
        AddWheels(data, frames, geometries, atomics, meshes, wheel, wheelScale);
        if (meshes.Count == 0)
            return null;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
            foreach (var p in mesh.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        return new ModelData { NameHash = nameHash, Meshes = meshes, BoundsMin = min, BoundsMax = max };
    }

    // The game puts a wheel on each "wheel_xx_dummy" frame of a vehicle: San Andreas cars hold one (on a "wheel" frame
    // under a dummy), GTA III / Vice City ones take a shared model. Left wheels are the right one mirrored.
    private static void AddWheels(byte[] data, List<Frame> frames, List<RwChunk> geometries, List<(int Frame, int Geometry)> atomics,
        List<ModelMesh> meshes, IReadOnlyList<ModelMesh>? shared, float scale)
    {
        var dummies = Enumerable.Range(0, frames.Count)
            .Where(i => frames[i].Name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase)
                && frames[i].Name.EndsWith("_dummy", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dummies.Count == 0)
            return;
        List<ModelMesh>? wheel = null;
        int owner = -1;
        foreach (var (frame, geometry) in atomics)
        {
            if (frame < 0 || frame >= frames.Count || !frames[frame].Name.Equals("wheel", StringComparison.OrdinalIgnoreCase))
                continue;
            owner = frames[frame].Parent;
            wheel = ReadGeometry(data, geometries[geometry], frames[frame].Local);
            break;
        }
        if (wheel is null && shared is not null)
            wheel = [.. shared.Select(m => Transformed(m, Matrix4x4.CreateScale(scale), false))];
        if (wheel is null)
            return;
        foreach (int dummy in dummies)
        {
            if (dummy == owner)
                continue;
            bool left = frames[dummy].Name.StartsWith("wheel_l", StringComparison.OrdinalIgnoreCase);
            var place = (left ? Matrix4x4.CreateScale(-1f, 1f, 1f) : Matrix4x4.Identity) * frames[dummy].World;
            meshes.AddRange(wheel.Select(m => Transformed(m, place, left)));
        }
    }

    private static ModelMesh Transformed(ModelMesh mesh, Matrix4x4 transform, bool mirrored)
    {
        var indices = mesh.Indices.ToArray();
        if (mirrored)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        }
        return new ModelMesh
        {
            Positions = [.. mesh.Positions.Select(p => Vector3.Transform(p, transform))],
            Normals = [.. mesh.Normals.Select(n => SafeNormalize(Vector3.TransformNormal(n, transform)))],
            TexCoords = mesh.TexCoords,
            Colors = mesh.Colors,
            Indices = indices,
            DiffuseTextureName = mesh.DiffuseTextureName,
            DiffuseColor = mesh.DiffuseColor,
            NightColors = mesh.NightColors,
            ClassicPrelit = mesh.ClassicPrelit,
            BlendMode = mesh.BlendMode,
        };
    }

    // The meshes of one part of a clump holding several models (GTA III / Vice City "wheels.dff": a frame per wheel
    // model), its most detailed level, around its own origin.
    public static List<ModelMesh> ReadPart(byte[] data, string part)
    {
        var top = RwChunk.At(data, 0, data.Length);
        if (top is not { Type: RwChunk.Clump } clump)
            return [];
        var children = clump.Children(data);
        var frames = children.FirstOrDefault(c => c.Type == RwChunk.FrameList) is { Type: RwChunk.FrameList } frameList
            ? ReadFrames(data, frameList)
            : [];
        var geometries = children.FirstOrDefault(c => c.Type == RwChunk.GeometryList) is { Type: RwChunk.GeometryList } geometryList
            ? geometryList.Children(data).Where(c => c.Type == RwChunk.Geometry).ToList()
            : [];
        var found = new List<(int Level, int Geometry)>();
        foreach (var atomic in children.Where(c => c.Type == RwChunk.Atomic))
        {
            if (atomic.Child(data, RwChunk.Struct) is not { Size: >= 16 } s)
                continue;
            int frame = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset));
            int geometry = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset + 4));
            if (frame < 0 || frame >= frames.Count || geometry < 0 || geometry >= geometries.Count)
                continue;
            // The atomic hangs on the part's frame or on a level frame under it ("wheel_rim_l0").
            string name = frames[frame].Name;
            int parentIndex = frames[frame].Parent;
            string parent = parentIndex >= 0 && parentIndex < frames.Count ? frames[parentIndex].Name : "";
            var (own, level) = DetailLevel(name);
            if (own.Equals(part, StringComparison.OrdinalIgnoreCase) || parent.Equals(part, StringComparison.OrdinalIgnoreCase))
                found.Add((Math.Max(level, 0), geometry));
        }
        if (found.Count == 0)
            return [];
        int best = found.Min(f => f.Level);
        return [.. found.Where(f => f.Level == best).SelectMany(f => ReadGeometry(data, geometries[f.Geometry], Matrix4x4.Identity))];
    }

    private static bool IsLeftOut(string name) =>
        name.EndsWith("_dam", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_lo", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_vlo", StringComparison.OrdinalIgnoreCase);

    // "building_l1" -> ("building", 1); a name without a level -> (name, -1).
    private static (string Part, int Level) DetailLevel(string name)
    {
        if (name.Length > 3 && name[^3] == '_' && (name[^2] is 'l' or 'L') && char.IsAsciiDigit(name[^1]))
            return (name[..^3], name[^1] - '0');
        return (name, -1);
    }

    private sealed record Frame(string Name, Matrix4x4 Local, Matrix4x4 World, int Parent);

    // A 56-byte entry per frame: 3x3 rotation (right, up, at), position, parent index, flags; names in the extensions.
    private static List<Frame> ReadFrames(byte[] data, RwChunk list)
    {
        var children = list.Children(data);
        if (children.FirstOrDefault(c => c.Type == RwChunk.Struct) is not { Type: RwChunk.Struct, Size: >= 4 } s)
            return [];
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset));
        if (count < 0 || 4 + (long)count * 56 > s.Size)
            return [];
        var extensions = children.Where(c => c.Type == RwChunk.Extension).ToList();
        var frames = new List<Frame>(count);
        for (int i = 0; i < count; i++)
        {
            int e = s.Offset + 4 + i * 56;
            float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(e + at));
            var local = new Matrix4x4(F(0), F(4), F(8), 0, F(12), F(16), F(20), 0, F(24), F(28), F(32), 0, F(36), F(40), F(44), 1);
            int parent = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e + 48));
            var world = parent >= 0 && parent < i ? local * frames[parent].World : local;
            string name = i < extensions.Count && extensions[i].Child(data, RwChunk.FrameName) is { } n
                ? System.Text.Encoding.ASCII.GetString(data, n.Offset, n.Size).TrimEnd('\0')
                : "";
            frames.Add(new Frame(name, local, world, parent >= 0 && parent < i ? parent : -1));
        }
        return frames;
    }

    private sealed record Material(string? Texture, Vector4 Color);

    // One mesh per material of the geometry, positions placed by the frame's matrix.
    private static List<ModelMesh> ReadGeometry(byte[] data, RwChunk geometry, Matrix4x4 transform)
    {
        var children = geometry.Children(data);
        if (children.FirstOrDefault(c => c.Type == RwChunk.Struct) is not { Type: RwChunk.Struct } s || s.Size < 16)
            return [];
        var d = data.AsSpan(s.Offset, s.Size);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
        int triangles = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        int vertices = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        int morphs = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        if ((flags & Native) != 0 || triangles <= 0 || vertices <= 0 || vertices > 0x10000 || morphs < 1)
            return [];
        int at = 16 + (geometry.Version < 0x34000 ? 12 : 0);

        // Kept as stored: the renderer adds the ambient light of the hour (GTA III, Vice City) or blends towards the
        // night colours (San Andreas).
        Vector4[]? colors = null;
        if ((flags & Prelit) != 0)
        {
            Need(d, at, vertices * 4);
            colors = new Vector4[vertices];
            for (int i = 0; i < vertices; i++, at += 4)
                colors[i] = new Vector4(d[at], d[at + 1], d[at + 2], d[at + 3]) / 255f;
        }
        var nightColors = colors is null ? null : NightColors(data, children, vertices);
        int sets = (int)((flags >> 16) & 0xFF);
        if (sets == 0)
            sets = (flags & Textured2) != 0 ? 2 : (flags & Textured) != 0 ? 1 : 0;
        var uv = new Vector2[vertices];
        if (sets > 0)
        {
            Need(d, at, vertices * 8 * sets);
            for (int i = 0; i < vertices; i++)
                uv[i] = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(d[(at + i * 8)..]), BinaryPrimitives.ReadSingleLittleEndian(d[(at + i * 8 + 4)..]));
            at += vertices * 8 * sets;
        }
        Need(d, at, triangles * 8);
        // Stored as (v1, v0, material, v2): swapped to wind like the other games' models.
        var tris = new (int A, int B, int C, int Material)[triangles];
        for (int i = 0; i < triangles; i++, at += 8)
            tris[i] = (BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 2)..]), BinaryPrimitives.ReadUInt16LittleEndian(d[at..]),
                BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 6)..]), BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 4)..]));

        Need(d, at, 24);
        bool hasPositions = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 16)..]) != 0;
        bool hasNormals = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 20)..]) != 0;
        at += 24;
        if (!hasPositions)
            return [];
        Need(d, at, vertices * 12 * (hasNormals ? 2 : 1));
        var positions = new Vector3[vertices];
        for (int i = 0; i < vertices; i++, at += 12)
            positions[i] = Vector3.Transform(Vec(d[at..]), transform);
        Vector3[]? normals = null;
        if (hasNormals)
        {
            normals = new Vector3[vertices];
            for (int i = 0; i < vertices; i++, at += 12)
                normals[i] = SafeNormalize(Vector3.TransformNormal(Vec(d[at..]), transform));
        }
        normals ??= ComputeNormals(positions, tris);

        var materials = children.FirstOrDefault(c => c.Type == RwChunk.MaterialList) is { Type: RwChunk.MaterialList } list
            ? ReadMaterials(data, list)
            : [];

        var meshes = new List<ModelMesh>();
        foreach (var group in tris.GroupBy(t => t.Material).OrderBy(g => g.Key))
        {
            var material = group.Key < materials.Count ? materials[group.Key] : new Material(null, Vector4.One);
            var remap = new Dictionary<int, ushort>();
            var indices = new List<ushort>();
            foreach (var (a, b, c, _) in group)
            {
                if (a >= vertices || b >= vertices || c >= vertices)
                    continue;
                foreach (int v in new[] { a, b, c })
                {
                    if (!remap.TryGetValue(v, out ushort index))
                        remap[v] = index = (ushort)remap.Count;
                    indices.Add(index);
                }
            }
            if (indices.Count == 0)
                continue;
            var used = remap.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
            meshes.Add(new ModelMesh
            {
                Positions = used.Select(v => positions[v]).ToArray(),
                Normals = used.Select(v => normals[v]).ToArray(),
                TexCoords = used.Select(v => uv[v]).ToArray(),
                Colors = colors is null ? used.Select(_ => Vector4.One).ToArray() : used.Select(v => colors[v]).ToArray(),
                NightColors = nightColors is null ? null : used.Select(v => nightColors[v]).ToArray(),
                ClassicPrelit = colors is not null,
                Indices = [.. indices],
                DiffuseTextureName = material.Texture,
                DiffuseColor = material.Color == Vector4.One ? null : material.Color,
                BlendMode = material.Color.W < 1f ? MeshBlendMode.Translucent
                    : material.Texture is not null ? MeshBlendMode.Cutout : MeshBlendMode.Opaque,
            });
        }
        return meshes;
    }

    // In the geometry's extension: a non-zero flag, then one RGBA colour per vertex.
    private static Vector4[]? NightColors(byte[] data, List<RwChunk> children, int vertices)
    {
        if (children.FirstOrDefault(c => c.Type == RwChunk.Extension) is not { Type: RwChunk.Extension } extension
            || extension.Child(data, NightColorsPlugin) is not { } plugin || plugin.Size < 4 + vertices * 4L
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(plugin.Offset)) == 0)
            return null;
        var colors = new Vector4[vertices];
        for (int i = 0, at = plugin.Offset + 4; i < vertices; i++, at += 4)
            colors[i] = new Vector4(data[at], data[at + 1], data[at + 2], data[at + 3]) / 255f;
        return colors;
    }

    // The list gives, for each material slot, -1 for a new material (the next "Material" section) or the index of an
    // earlier slot to reuse.
    private static List<Material> ReadMaterials(byte[] data, RwChunk list)
    {
        var children = list.Children(data);
        if (children.FirstOrDefault(c => c.Type == RwChunk.Struct) is not { Type: RwChunk.Struct, Size: >= 4 } s)
            return [];
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset));
        if (count < 0 || 4 + (long)count * 4 > s.Size)
            return [];
        var sections = children.Where(c => c.Type == RwChunk.Material).ToList();
        var materials = new List<Material>(count);
        int next = 0;
        for (int i = 0; i < count; i++)
        {
            int reuse = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(s.Offset + 4 + i * 4));
            if (reuse >= 0 && reuse < materials.Count)
                materials.Add(materials[reuse]);
            else
                materials.Add(next < sections.Count ? ReadMaterial(data, sections[next++]) : new Material(null, Vector4.One));
        }
        return materials;
    }

    // Struct: flags, colour (RGBA bytes), unused, textured, lighting values; then a "Texture" section naming it.
    private static Material ReadMaterial(byte[] data, RwChunk material)
    {
        var color = Vector4.One;
        if (material.Child(data, RwChunk.Struct) is { Size: >= 16 } s)
        {
            var c = data.AsSpan(s.Offset + 4, 4);
            color = new Vector4(c[0], c[1], c[2], c[3]) / 255f;
        }
        string? texture = null;
        if (material.Child(data, RwChunk.Texture) is { } t && t.Child(data, RwChunk.String) is { } name)
        {
            texture = name.ReadString(data);
            if (texture.Length == 0)
                texture = null;
        }
        return new Material(texture, color);
    }

    private static Vector3[] ComputeNormals(Vector3[] positions, (int A, int B, int C, int Material)[] tris)
    {
        var normals = new Vector3[positions.Length];
        foreach (var (a, b, c, _) in tris)
        {
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length)
                continue;
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = SafeNormalize(normals[i]);
        return normals;
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;

    private static Vector3 Vec(ReadOnlySpan<byte> s) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));

    private static void Need(ReadOnlySpan<byte> d, int at, long count)
    {
        if (at + count > d.Length)
            throw new InvalidDataException("géométrie tronquée");
    }
}

// Models of a GTA III / Vice City / San Andreas installation for the 3D view: a ".dff" gives one model named after
// the file; a car of GTA III / Vice City gets the wheels its definition names.
public sealed class ClassicModelReader(ClassicFileSystem files) : IModelReader
{
    public const string WheelsFile = @"models\generic\wheels.dff";

    private byte[]? _wheels;

    // Set once the map is read (the vehicles and their wheels).
    public ClassicDefinitions? Definitions { get; set; }

    public IReadOnlyDictionary<uint, ModelData> ReadModels(string path)
    {
        if (files.ReadFile(path) is not { } data)
            return new Dictionary<uint, ModelData>();
        string name = Path.GetFileNameWithoutExtension(path);
        uint hash = Joaat.Hash(name);
        try
        {
            IReadOnlyList<ModelMesh>? wheel = null;
            float scale = 1f;
            if (Definitions?.FindVehicle(name) is { WheelModel: >= 0 } vehicle && Definitions.Find(vehicle.WheelModel) is { } wheelModel)
            {
                _wheels ??= files.ReadFile(WheelsFile);
                if (_wheels is not null)
                    (wheel, scale) = (ClassicModels.ReadPart(_wheels, wheelModel.Name), vehicle.WheelScale);
            }
            return ClassicModels.ReadClump(data, hash, wheel, scale) is { } model
                ? new Dictionary<uint, ModelData> { [hash] = model }
                : new Dictionary<uint, ModelData>();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{path} : modèle illisible ({ex.Message})", ex);
        }
    }
}
