using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Collisions;

// Reads collision files (.ybn) and removes polygons from them.
public sealed class YbnReader : ICollisionReader, ICollisionWriter
{
    private readonly IGameFileSystem _files;

    public YbnReader(IGameFileSystem files)
    {
        _files = files;
    }

    public CollisionMesh? Read(string path)
    {
        if (_files.ReadFile(path) is not { } data)
            return null;
        return Read(data);
    }

    public static CollisionMesh? Read(byte[] data)
    {
        var file = new BoundsFile(data);
        var builder = new CollisionMeshBuilder();
        int polygon = 0;
        file.Add(builder, 0, Matrix4x4.Identity, ref polygon);
        return builder.Build();
    }

    public (Vector3 Min, Vector3 Max)? ReadBox(string path)
    {
        if (_files.ReadFile(path) is not { } data)
            return null;
        if (new BoundsFile(data).RootBox() is var (min, max)
            && float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z) && min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z)
            return (min, max);
        return Read(data) is { } mesh ? (mesh.BoundsMin, mesh.BoundsMax) : null;
    }

    public byte[]? RemovePolygons(string path, IReadOnlySet<int> polygons)
    {
        if (_files.ReadFile(path) is not { } data)
            return null;
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        return ResourceFile.Write(header, RemovePolygons(data, polygons));
    }

    // The decompressed file without these polygons (numbered as Read does).
    public static byte[] RemovePolygons(byte[] data, IReadOnlySet<int> polygons)
    {
        var copy = (byte[])data.Clone();
        var file = new BoundsFile(copy);
        int polygon = 0;
        file.Remove(0, polygons, ref polygon);
        return copy;
    }

    public byte[]? Edit(string path, IReadOnlySet<int> removed, IReadOnlyDictionary<int, byte> materials)
    {
        if (_files.ReadFile(path) is not { } data)
            return null;
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        var (content, systemFlags) = Edit(data, header.SystemFlags, removed, materials);
        return ResourceFile.Write(header with { SystemFlags = systemFlags }, content);
    }

    // The decompressed file with new materials, then without the removed polygons; when it grew, its pages become
    // a single page large enough (offsets are unchanged: pages follow each other).
    public static (byte[] Content, uint SystemFlags) Edit(byte[] data, uint systemFlags, IReadOnlySet<int> removed,
        IReadOnlyDictionary<int, byte> materials)
    {
        var copy = (byte[])data.Clone();
        if (materials.Count > 0)
        {
            var file = new BoundsFile(copy);
            int polygon = 0;
            file.SetMaterials(0, materials, ref polygon);
            copy = file.WithAppended();
        }
        if (removed.Count > 0)
        {
            int polygon = 0;
            new BoundsFile(copy).Remove(0, removed, ref polygon);
        }

        int pagesSize = Archives.ResourcePages.Size(systemFlags);
        if (copy.Length <= pagesSize)
        {
            if (copy.Length < pagesSize)
                Array.Resize(ref copy, pagesSize);
            return (copy, systemFlags);
        }
        int pageSize = 0x2000;
        while (pageSize < copy.Length)
            pageSize *= 2;
        Array.Resize(ref copy, pageSize);
        return (copy, (systemFlags & 0xF0000000) | MetaDocument.PageFlags(1, pageSize));
    }
}

// Offsets of the bounds structures (64-bit PC version) and the walk shared by reading and editing.
internal sealed partial class BoundsFile(byte[] data)
{
    // Bounds (every type), 0x70 bytes.
    private const int TypeOffset = 0x10;
    private const int SphereRadiusOffset = 0x14;
    private const int BoxMaxOffset = 0x20;
    private const int BoxMinOffset = 0x30;
    private const int MaterialIndexOffset = 0x4C;
    private const int SphereCenterOffset = 0x50;

    // Geometry (type 4) and geometry with search tree (type 8, + 0x20 bytes).
    private const int PolygonsPointer = 0x88;
    private const int QuantumOffset = 0x90;
    private const int CenterOffset = 0xA0;
    private const int VerticesPointer = 0xB0;
    private const int VerticesCount = 0xD0;
    private const int PolygonsCount = 0xD4;
    private const int MaterialsPointer = 0xF0;
    private const int PolygonMaterialsPointer = 0x118;
    private const int MaterialsCount = 0x120;
    private const int GeometryBvhPointer = 0x130;

    // Composite.
    private const int ChildrenPointer = 0x70;
    private const int Transforms1Pointer = 0x78;
    private const int Transforms2Pointer = 0x80;
    private const int ChildBoxesPointer = 0x88;
    private const int ChildFlags1Pointer = 0x90;
    private const int ChildFlags2Pointer = 0x98;
    private const int ChildrenCount1 = 0xA0;
    private const int ChildrenCount2 = 0xA2;
    private const int CompositeBvhPointer = 0xA8;

    private const byte Sphere = 0, Box = 3, Geometry = 4, GeometryBvh = 8, Composite = 10;

    private readonly byte[] _data = data;

    private Span<byte> At(int offset, int length)
    {
        if (offset < 0 || offset + length > _data.Length)
            throw new InvalidDataException($"Lecture hors du fichier de collision à 0x{offset:X}.");
        return _data.AsSpan(offset, length);
    }

    private byte U8(int o) => At(o, 1)[0];
    private ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(At(o, 2));
    private short I16(int o) => BinaryPrimitives.ReadInt16LittleEndian(At(o, 2));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(At(o, 4));
    private ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(At(o, 8));
    private float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(At(o, 4));
    private Vector3 Vec3(int o) => new(F32(o), F32(o + 4), F32(o + 8));
    private void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(At(o, 2), v);
    private void W16(int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(At(o, 2), v);
    private void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(At(o, 4), v);

    private int Resolve(ulong pointer, long count, int size)
    {
        if (pointer == 0 || count == 0)
            return -1;
        if ((pointer & 0xF0000000) != 0x50000000)
            throw new InvalidDataException($"Pointeur invalide dans le fichier de collision : 0x{pointer:X}.");
        long offset = (long)(pointer - 0x50000000);
        if (offset + count * size > _data.Length)
            throw new InvalidDataException("Données de collision hors du fichier.");
        return (int)offset;
    }

    // A composite child's placement (4 rows of 3 floats + 1 unused word each: row vectors, translation last).
    private Matrix4x4 Transform(int o) => new(
        F32(o), F32(o + 4), F32(o + 8), 0,
        F32(o + 16), F32(o + 20), F32(o + 24), 0,
        F32(o + 32), F32(o + 36), F32(o + 40), 0,
        F32(o + 48), F32(o + 52), F32(o + 56), 1);

    // The box of the whole file, as written in it.
    public (Vector3 Min, Vector3 Max) RootBox() => (Vec3(BoxMinOffset), Vec3(BoxMaxOffset));

    public void Add(CollisionMeshBuilder builder, int o, Matrix4x4 transform, ref int polygon)
    {
        builder.Polygon = -1;
        switch (U8(o + TypeOffset))
        {
            case Composite:
                int count = U16(o + ChildrenCount1);
                int children = Resolve(U64(o + ChildrenPointer), count, 8);
                ulong transforms = U64(o + Transforms1Pointer);
                if (transforms == 0)
                    transforms = U64(o + Transforms2Pointer);
                int matrices = Resolve(transforms, count, 64);
                for (int i = 0; i < count; i++)
                {
                    int child = Resolve(U64(children + i * 8), 1, 0x70);
                    if (child < 0)
                        continue;
                    // Each child is placed by its own composite only (nested composites do not chain).
                    var placement = matrices >= 0 ? Transform(matrices + i * 64) : Matrix4x4.Identity;
                    Add(builder, child, placement, ref polygon);
                }
                break;

            case Geometry:
            case GeometryBvh:
                AddGeometry(builder, o, transform, ref polygon);
                break;

            case Box:
                builder.AddBox(Vec3(o + BoxMinOffset), Vec3(o + BoxMaxOffset), transform, U8(o + MaterialIndexOffset));
                break;

            case Sphere:
                builder.AddSphere(Vector3.Transform(Vec3(o + SphereCenterOffset), transform), F32(o + SphereRadiusOffset),
                    U8(o + MaterialIndexOffset));
                break;

            // Other lone primitives (capsule, cylinder, disc, cloth) do not occur in map collision files.
        }
    }

    private void AddGeometry(CollisionMeshBuilder builder, int o, Matrix4x4 transform, ref int polygon)
    {
        int polygonCount = (int)U32(o + PolygonsCount);
        int first = polygon;
        polygon += polygonCount;
        int vertexCount = (int)U32(o + VerticesCount);
        int polygons = Resolve(U64(o + PolygonsPointer), polygonCount, 16);
        int vertices = Resolve(U64(o + VerticesPointer), vertexCount, 6);
        if (polygons < 0 || vertices < 0)
            return;

        var quantum = Vec3(o + QuantumOffset);
        var center = Vec3(o + CenterOffset);
        Vector3 At(int index) => Vector3.Transform(
            (index >= 0 && index < vertexCount ? Vertex(vertices, index) * quantum : Vector3.Zero) + center, transform);

        var materials = Materials(o, polygonCount);
        for (int i = 0; i < polygonCount; i++)
        {
            builder.Polygon = first + i;
            int p = polygons + i * 16;
            byte material = materials(i);
            switch (U8(p) & 7)
            {
                case 0: // triangle: area, 3 vertices (high bit = flag), 3 neighbours
                    builder.AddTriangle(At(U16(p + 4) & 0x7FFF), At(U16(p + 6) & 0x7FFF), At(U16(p + 8) & 0x7FFF), material);
                    break;
                case 1: // sphere: vertex, radius
                    builder.AddSphere(At(U16(p + 2)), F32(p + 4), material);
                    break;
                case 2: // capsule: vertex, radius, vertex
                    builder.AddCapsule(At(U16(p + 2)), At(U16(p + 8)), F32(p + 4), material);
                    break;
                case 3: // box: 4 corners
                    builder.AddBoxFromCorners(At(I16(p + 4)), At(I16(p + 6)), At(I16(p + 8)), At(I16(p + 10)), material);
                    break;
                case 4: // cylinder: vertex, radius, vertex
                    builder.AddCylinder(At(U16(p + 2)), At(U16(p + 8)), F32(p + 4), material);
                    break;
            }
        }
        builder.Polygon = -1;
    }

    private Vector3 Vertex(int vertices, int index)
    {
        int v = vertices + index * 6;
        return new Vector3(I16(v), I16(v + 2), I16(v + 4));
    }

    private Func<int, byte> Materials(int o, int polygonCount)
    {
        int materialCount = U8(o + MaterialsCount);
        int materials = Resolve(U64(o + MaterialsPointer), Math.Max(materialCount, 1), 8);
        int indices = Resolve(U64(o + PolygonMaterialsPointer), polygonCount, 1);
        return i =>
        {
            int slot = indices >= 0 ? _data[indices + i] : 0;
            return materials >= 0 && slot < materialCount ? _data[materials + slot * 8] : (byte)0;
        };
    }
}
