using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.Data.Native.Models;

// Writes a complete drawable (.ydr, resource version 165) from a drawable read from XML. Everything goes into system
// pages except the pixels of the embedded textures.
internal static class YdrBuilder
{
    private const int Version = 165;

    // Virtual table words: one family found together in the game's files (the game replaces them when loading).
    private const uint DrawableVft = 1079456120;
    private const uint ShaderGroupVft = 1080113472;
    private const uint SkeletonVft = 1080114336;
    private const uint TextureReferenceVft = 1080142072;
    private const uint ModelVft = 1080101560;
    private const uint GeometryVft = 1080133784;
    private const uint VertexBufferVft = 1080153112;
    private const uint IndexBufferVft = 1080111672;

    private const int DrawableLength = 0xD0;
    private const int ShaderLength = 0x30;
    private const int TextureReferenceLength = 0x50;
    private const int GeometryLength = 0x98;
    private const uint NoValue = 0x7F800001;

    public static byte[] Build(XmlDrawable x)
    {
        var builder = new ResourceBuilder();
        var d = builder.Add(DrawableLength);
        d.U32(0, DrawableVft);
        d.U32(0x04, 1);

        Dictionary<uint, ResourceBuilder.Block> textures = [];
        ResourceBuilder.Block? dictionary = null;
        if (x.Textures.Count > 0)
            (dictionary, textures) = YtdBuilder.WriteDictionary(builder, x.Textures);
        builder.Pointer(d, 0x10, WriteShaderGroup(builder, x.Shaders, dictionary, textures));
        if (x.Skeleton is { } skeleton)
            builder.Pointer(d, 0x18, WriteSkeleton(builder, skeleton));

        d.Vec3(0x20, x.SphereCenter);
        d.F32(0x2C, x.SphereRadius);
        d.Vec3(0x30, x.BoxMin);
        d.U32(0x3C, NoValue);
        d.Vec3(0x40, x.BoxMax);
        d.U32(0x4C, NoValue);

        var (models, headers, length) = WriteModels(builder, x);
        for (int level = 0; level < 4; level++)
        {
            if (headers[level] >= 0)
                builder.Pointer(d, 0x50 + level * 8, models, headers[level]);
            d.F32(0x70 + level * 4, x.LodDistances[level]);
            // Low byte: flags of the level; next byte: render mask of all its models.
            byte mask = 0;
            foreach (var model in x.Models[level])
                mask |= model.RenderMask;
            d.U32(0x80 + level * 4, (uint)(x.LodFlags[level] | mask << 8));
        }
        d.U16(0x9A, (length + 15) / 16);
        builder.Pointer(d, 0xA0, models);

        string name = x.Name.EndsWith(".#dr", StringComparison.OrdinalIgnoreCase) ? x.Name : x.Name + ".#dr";
        builder.Pointer(d, 0xA8, builder.String(name));
        if (x.Lights.Count > 0)
        {
            var lights = builder.Add(x.Lights.SelectMany(l => l).ToArray());
            builder.Pointer(d, 0xB0, lights);
            d.U16(0xB8, x.Lights.Count);
            d.U16(0xBA, x.Lights.Count);
        }
        if (x.Bound is { } bound)
        {
            int size = YbnBuilder.BuildEmbedded(bound, ResourceBuilder.SystemBase).Length;
            builder.Pointer(d, 0xC8, builder.AddRelocated(size, address => YbnBuilder.BuildEmbedded(bound, address)));
        }
        return builder.Build(Version);
    }

    // ---- Shaders ----

    // The group, its pointer list and its shaders in one block, as in the game's files (whose size it records).
    private static ResourceBuilder.Block WriteShaderGroup(ResourceBuilder builder, List<XmlShader> shaders,
        ResourceBuilder.Block? dictionary, Dictionary<uint, ResourceBuilder.Block> textures)
    {
        int count = shaders.Count;
        int first = 0x40 + ((8 * count + 15) & ~15);
        int length = first + ShaderLength * count;
        var group = builder.Add(length);
        group.U32(0, ShaderGroupVft);
        group.U32(0x04, 1);
        builder.Pointer(group, 0x08, dictionary);
        if (count > 0)
            builder.Pointer(group, 0x10, group, 0x40);
        group.U16(0x18, count);
        group.U16(0x1A, count);
        group.U32(0x30, (uint)(length / 16));

        for (int i = 0; i < count; i++)
        {
            int s = first + ShaderLength * i;
            builder.Pointer(group, 0x40 + 8 * i, group, s);
            var shader = shaders[i];
            var parameters = shader.Parameters;
            int vectors = parameters.Sum(p => p.Values.Length);
            int parameterSize = 16 * parameters.Count + 16 * vectors;
            int dataSize = (32 + parameterSize + 4 * parameters.Count + 15) & ~15;

            group.U32(s + 0x08, shader.Name);
            group.U8(s + 0x10, parameters.Count);
            group.U8(s + 0x11, shader.RenderBucket);
            group.U16(s + 0x12, 0x8000);
            group.U16(s + 0x14, parameterSize);
            group.U16(s + 0x16, dataSize);
            group.U32(s + 0x18, shader.FileName);
            group.U32(s + 0x20, (1u << shader.RenderBucket) | 0xFF00);
            group.U8(s + 0x27, parameters.Count(p => p.Values.Length == 0));
            if (parameters.Count > 0)
                builder.Pointer(group, s, WriteParameters(builder, parameters, parameterSize, dataSize, textures));
        }
        return group;
    }

    // Parameter block: 16 bytes per parameter (type: 0 texture, n: n vectors; a slot number; a pointer), the vectors,
    // the name hashes, then room the game uses at run time.
    private static ResourceBuilder.Block WriteParameters(ResourceBuilder builder, List<XmlShaderParameter> parameters,
        int parameterSize, int dataSize, Dictionary<uint, ResourceBuilder.Block> textures)
    {
        int count = parameters.Count;
        var block = builder.Add(parameterSize + 4 * count + 32 + 4 * dataSize);
        // Slot numbers as written by the game's tools: textures by position, vectors counted from the end.
        var slots = new int[count];
        int slot = 160;
        for (int i = count - 1; i >= 0; i--)
        {
            if (parameters[i].Values.Length > 0)
            {
                slots[i] = slot;
                slot += parameters[i].Values.Length;
            }
            else
            {
                slots[i] = i + 2;
            }
        }

        int vector = 16 * count;
        for (int i = 0; i < count; i++)
        {
            var p = parameters[i];
            int at = 16 * i;
            block.U8(at, p.Values.Length);
            block.U8(at + 1, slots[i]);
            block.U32(parameterSize + 4 * i, p.Name);
            if (p.Values.Length > 0)
            {
                builder.Pointer(block, at + 8, block, vector);
                foreach (var v in p.Values)
                {
                    block.Vec4(vector, v);
                    vector += 16;
                }
            }
            else if (p.Texture is { } texture)
            {
                builder.Pointer(block, at + 8, textures.TryGetValue(Joaat.Hash(texture), out var embedded)
                    ? embedded : TextureReference(builder, texture));
            }
        }
        return block;
    }

    // A texture found in a .ytd: only its name.
    private static ResourceBuilder.Block TextureReference(ResourceBuilder builder, string name)
    {
        var t = builder.Add(TextureReferenceLength);
        t.U32(0, TextureReferenceVft);
        t.U32(0x04, 1);
        builder.Pointer(t, 0x28, builder.String(name));
        t.U16(0x30, 1);
        t.U16(0x32, 2);
        return t;
    }

    // ---- Skeleton ----

    // Skeleton (0x70): bone tag table, bones, inverse bind poses, local transforms, parent indices, child pairs.
    private static ResourceBuilder.Block WriteSkeleton(ResourceBuilder builder, XmlSkeleton s)
    {
        var bones = s.Bones;
        int count = bones.Count;
        var k = builder.Add(0x70);
        k.U32(0, SkeletonVft);
        k.U32(0x04, 1);

        // Bone tags in a hash table (tag modulo the number of buckets), each bucket a chain, last bone first.
        if (count >= 2)
        {
            int buckets = BucketCount(count);
            var table = builder.Add(8 * buckets);
            for (int b = 0; b < buckets; b++)
            {
                ResourceBuilder.Block? next = null;
                for (int i = 0; i < count; i++)
                {
                    if (bones[i].Tag % buckets != b)
                        continue;
                    var tag = builder.Add(16);
                    tag.U32(0, bones[i].Tag);
                    tag.U32(4, (uint)i);
                    builder.Pointer(tag, 8, next);
                    next = tag;
                }
                builder.Pointer(table, 8 * b, next);
            }
            builder.Pointer(k, 0x10, table);
            k.U16(0x18, buckets);
            k.U16(0x1A, Math.Min(count, buckets));
        }
        k.U32(0x1C, s.Unknown1C);

        var block = builder.Add(16 + 80 * count);
        block.U32(0, (uint)count);
        var local = builder.Add(64 * count);
        var inverse = builder.Add(64 * count);
        var parents = builder.Add(2 * count);
        for (int i = 0; i < count; i++)
        {
            var bone = bones[i];
            int o = 16 + 80 * i;
            block.Vec4(o, new Vector4(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W));
            block.Vec3(o + 0x10, bone.Translation);
            block.Vec3(o + 0x20, bone.Scale);
            block.F32(o + 0x2C, 1f);
            block.U16(o + 0x30, bone.Sibling);
            block.U16(o + 0x32, bone.Parent);
            builder.Pointer(block, o + 0x38, builder.String(bone.Name));
            block.U16(o + 0x40, bone.Flags);
            block.U16(o + 0x42, bone.Index);
            block.U16(o + 0x44, bone.Tag);
            block.U16(o + 0x46, bone.Index);
            parents.U16(2 * i, bone.Parent);

            // Local transform: rotation and translation, scale on the diagonal, 4th column kept from the tools.
            var m = Matrix4x4.CreateFromQuaternion(bone.Rotation);
            m.Translation = bone.Translation;
            m.M11 *= bone.Scale.X;
            m.M22 *= bone.Scale.Y;
            m.M33 *= bone.Scale.Z;
            (m.M14, m.M24, m.M34, m.M44) = (bone.TransformUnknown.X, bone.TransformUnknown.Y, bone.TransformUnknown.Z, bone.TransformUnknown.W);
            WriteMatrix(local, 64 * i, m);

            // Inverse of the bone's place in the model (parents' rotations and translations, no scale).
            var position = bone.Translation;
            var rotation = bone.Rotation;
            for (int p = bone.Parent, depth = 0; p >= 0 && depth < count; p = bones[p].Parent, depth++)
            {
                position = Vector3.Transform(position, bones[p].Rotation) + bones[p].Translation;
                rotation = bones[p].Rotation * rotation;
            }
            var global = Matrix4x4.CreateFromQuaternion(rotation);
            global.Translation = position;
            Matrix4x4.Invert(global, out var inv);
            (inv.M14, inv.M24, inv.M34, inv.M44) = (0, 0, 0, 0);
            WriteMatrix(inverse, 64 * i, inv);
        }
        var pairs = ChildPairs(bones);
        builder.Pointer(k, 0x20, block, 16);
        builder.Pointer(k, 0x28, inverse);
        builder.Pointer(k, 0x30, local);
        builder.Pointer(k, 0x38, parents);
        if (pairs.Count > 0)
        {
            var children = builder.Add(2 * pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
                children.U16(2 * i, pairs[i]);
            builder.Pointer(k, 0x40, children);
        }
        k.U32(0x50, s.Unknown50);
        k.U32(0x54, s.Unknown54);
        k.U32(0x58, s.Unknown58);
        k.U16(0x5C, 1);
        k.U16(0x5E, count);
        k.U16(0x60, pairs.Count);
        return k;
    }

    // (child, parent) pairs, walked down the hierarchy at most 4 bones at a time; each group padded to a multiple of 4
    // pairs by repeating its last pair.
    private static List<short> ChildPairs(List<XmlBone> bones)
    {
        List<XmlBone> Children(IEnumerable<XmlBone> parents) =>
            [.. parents.SelectMany(p => bones.Where(b => b.Parent == p.Index))];

        var pairs = new List<short>();
        var pending = Children(bones.Where(b => b.Parent < 0));
        for (int guard = 0; pending.Count > 0 && guard < 0x10000; guard++)
        {
            var group = pending.GetRange(0, Math.Min(pending.Count, 4));
            pending.RemoveRange(0, group.Count);
            pending.InsertRange(0, Children(group));
            foreach (var bone in group)
            {
                pairs.Add(bone.Index);
                pairs.Add(bone.Parent);
            }
            var last = group[^1];
            while (pairs.Count % 8 != 0)
            {
                pairs.Add(last.Index);
                pairs.Add(last.Parent);
            }
        }
        return pairs;
    }

    private static int BucketCount(int tags)
    {
        int[] sizes = [11, 29, 59, 107, 191, 331, 563, 953, 1609, 2729, 4621, 7841, 13297, 22571, 38351, 65167];
        foreach (int size in sizes)
        {
            if (tags < size)
                return size;
        }
        return 65167;
    }

    private static void WriteMatrix(ResourceBuilder.Block block, int o, Matrix4x4 m)
    {
        block.Vec4(o, new Vector4(m.M11, m.M12, m.M13, m.M14));
        block.Vec4(o + 16, new Vector4(m.M21, m.M22, m.M23, m.M24));
        block.Vec4(o + 32, new Vector4(m.M31, m.M32, m.M33, m.M34));
        block.Vec4(o + 48, new Vector4(m.M41, m.M42, m.M43, m.M44));
    }

    // ---- Models ----

    // All the levels in one block, as in the game's files (whose size the drawable records): for each level, a list
    // header, its pointers, then each model followed by its own tables and geometries.
    private static (ResourceBuilder.Block Block, int[] Headers, int Length) WriteModels(ResourceBuilder builder, XmlDrawable x)
    {
        static int Align(int o) => (o + 15) & ~15;

        // Positions first.
        int position = 0;
        var headers = new int[4];
        var modelAt = new List<int>[4];
        for (int level = 0; level < 4; level++)
        {
            modelAt[level] = [];
            var list = x.Models[level];
            if (list.Count == 0)
            {
                headers[level] = -1;
                continue;
            }
            position = Align(position);
            headers[level] = position;
            position += 16 + 8 * list.Count;
            foreach (var model in list)
            {
                position = Align(position);
                modelAt[level].Add(position);
                position += ModelLength(model, position);
            }
        }

        var block = builder.Add(position);
        for (int level = 0; level < 4; level++)
        {
            if (headers[level] < 0)
                continue;
            var list = x.Models[level];
            int h = headers[level];
            builder.Pointer(block, h, block, h + 16);
            block.U16(h + 8, list.Count);
            block.U16(h + 10, list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                builder.Pointer(block, h + 16 + 8 * i, block, modelAt[level][i]);
                WriteModel(builder, block, modelAt[level][i], list[i]);
            }
        }
        return (block, headers, position);
    }

    // Model (0x30), then the shader of each geometry (2 bytes each), the geometry pointers, the boxes (one per
    // geometry, plus one around all of them when there are several) and the geometries.
    private static (int Mapping, int Pointers, int Boxes, int[] Geometries, int End) ModelParts(XmlModel model, int at)
    {
        static int Pad(int o) => (o + 15) & ~15;
        int count = model.Geometries.Count;
        int o = at + 0x30;
        int mapping = o;
        o += 2 * count;
        o = count == 1 ? o + 6 : Pad(o);
        int pointers = o;
        o = Pad(o + 8 * count);
        int boxes = o;
        o += 32 * (count + (count > 1 ? 1 : 0));
        var geometries = new int[count];
        for (int i = 0; i < count; i++)
        {
            o = Pad(o);
            geometries[i] = o;
            o += GeometryLength;
        }
        return (mapping, pointers, boxes, geometries, o);
    }

    private static int ModelLength(XmlModel model, int at) => ModelParts(model, at).End - at;

    private static void WriteModel(ResourceBuilder builder, ResourceBuilder.Block block, int at, XmlModel model)
    {
        var (mapping, pointers, boxes, geometries, _) = ModelParts(model, at);
        int count = model.Geometries.Count;
        block.U32(at, ModelVft);
        block.U32(at + 0x04, 1);
        builder.Pointer(block, at + 0x08, block, pointers);
        block.U16(at + 0x10, count);
        block.U16(at + 0x12, count);
        builder.Pointer(block, at + 0x18, block, boxes);
        builder.Pointer(block, at + 0x20, block, mapping);
        block.U32(at + 0x28, (uint)(model.Unknown1 | model.BoneIndex << 24));
        block.U16(at + 0x2C, model.RenderMask | model.Flags << 8);
        block.U16(at + 0x2E, count);

        int box = boxes;
        if (count > 1)
        {
            var min = model.Geometries.Aggregate(new Vector4(float.MaxValue), (m, g) => Vector4.Min(m, g.BoxMin));
            var max = model.Geometries.Aggregate(new Vector4(float.MinValue), (m, g) => Vector4.Max(m, g.BoxMax));
            block.Vec4(box, min);
            block.Vec4(box + 16, max);
            box += 32;
        }
        for (int i = 0; i < count; i++)
        {
            var g = model.Geometries[i];
            block.U16(mapping + 2 * i, g.Shader);
            builder.Pointer(block, pointers + 8 * i, block, geometries[i]);
            block.Vec4(box + 32 * i, g.BoxMin);
            block.Vec4(box + 32 * i + 16, g.BoxMax);
            WriteGeometry(builder, block, geometries[i], g);
        }
    }

    private static void WriteGeometry(ResourceBuilder builder, ResourceBuilder.Block block, int at, XmlGeometry g)
    {
        int stride = g.Declaration.Stride;
        var vertices = builder.Add(g.Vertices);

        var declaration = builder.Add(16);
        declaration.U32(0, g.Declaration.Flags);
        declaration.U16(4, stride);
        declaration.U8(7, g.Declaration.Count);
        declaration.U64(8, g.Declaration.Types);

        var vertexBuffer = builder.Add(0x80);
        vertexBuffer.U32(0, VertexBufferVft);
        vertexBuffer.U32(0x04, 1);
        vertexBuffer.U16(0x08, stride);
        vertexBuffer.U16(0x0A, g.VertexFlags);
        builder.Pointer(vertexBuffer, 0x10, vertices);
        vertexBuffer.U32(0x18, (uint)g.VertexCount);
        builder.Pointer(vertexBuffer, 0x20, vertices);
        builder.Pointer(vertexBuffer, 0x30, declaration);

        var indexData = new byte[g.Indices.Length * 2];
        for (int i = 0; i < g.Indices.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(indexData.AsSpan(i * 2), g.Indices[i]);
        var indexBuffer = builder.Add(0x60);
        indexBuffer.U32(0, IndexBufferVft);
        indexBuffer.U32(0x04, 1);
        indexBuffer.U32(0x08, (uint)g.Indices.Length);
        builder.Pointer(indexBuffer, 0x10, builder.Add(indexData));

        block.U32(at, GeometryVft);
        block.U32(at + 0x04, 1);
        builder.Pointer(block, at + 0x18, vertexBuffer);
        builder.Pointer(block, at + 0x38, indexBuffer);
        block.U32(at + 0x58, (uint)g.Indices.Length);
        block.U32(at + 0x5C, (uint)(g.Indices.Length / 3));
        block.U16(at + 0x60, g.VertexCount);
        block.U16(at + 0x62, 3);
        block.U16(at + 0x70, stride);
        builder.Pointer(block, at + 0x78, vertices);
    }
}
