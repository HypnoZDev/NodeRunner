using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Native.Resources;

// Assembles a resource file from blocks that point at each other: places them in system or graphics pages (a block
// never crosses the end of a page), then writes the pointers. The first system block is the root; its word at 0x08
// receives the page list, as in every resource file.
internal sealed class ResourceBuilder
{
    public const ulong SystemBase = 0x50000000;
    public const ulong GraphicsBase = 0x60000000;
    private const int MinPageSize = 0x2000;
    private const int MaxUnitSize = 0x200 << 15;
    private const int MaxUnits = 0x7FF;
    private const int PageMargin = 16;

    internal sealed class Block
    {
        public byte[] Data;
        public readonly bool Graphics;
        internal int Position;
        internal readonly List<(int At, Block Target, int Offset)> Pointers = [];
        // Content that depends on the block's address (embedded collision): rebuilt once placed, same length.
        internal Func<ulong, byte[]>? Relocate;

        internal Block(byte[] data, bool graphics)
        {
            Data = data;
            Graphics = graphics;
        }

        public void U8(int o, int v) => Data[o] = (byte)v;
        public void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(o), (ushort)v);
        public void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(o), v);
        public void U64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(o), v);
        public void F32(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(Data.AsSpan(o), v);
        public void Vec3(int o, Vector3 v) { F32(o, v.X); F32(o + 4, v.Y); F32(o + 8, v.Z); }
        public void Vec4(int o, Vector4 v) { F32(o, v.X); F32(o + 4, v.Y); F32(o + 8, v.Z); F32(o + 12, v.W); }
    }

    private readonly List<Block> _blocks = [];

    public Block Add(int size, bool graphics = false) => Add(new byte[size], graphics);

    public Block Add(byte[] data, bool graphics = false)
    {
        var block = new Block(data, graphics);
        _blocks.Add(block);
        return block;
    }

    public Block AddRelocated(int size, Func<ulong, byte[]> build)
    {
        var block = Add(size);
        block.Relocate = build;
        return block;
    }

    public Block String(string text)
    {
        var data = new byte[Encoding.ASCII.GetByteCount(text) + 1];
        Encoding.ASCII.GetBytes(text, data);
        return Add(data);
    }

    // The word at "at" in "from" will hold the address of "to" (plus offset); nothing for a null target.
    public void Pointer(Block from, int at, Block? to, int offset = 0)
    {
        if (to is not null)
            from.Pointers.Add((at, to, offset));
    }

    public byte[] Build(int version)
    {
        var root = _blocks.FirstOrDefault(b => !b.Graphics) ?? throw new InvalidOperationException("Ressource sans bloc racine.");
        var system = _blocks.Where(b => !b.Graphics).ToList();
        var graphics = _blocks.Where(b => b.Graphics).ToList();

        // The page list's size depends on the number of pages, which is not monotonic (15 pages are 4 entries, 16 only
        // one): the list only grows, so the passes always end; unused entries stay zero.
        var pages = new Block(new byte[16], false);
        system.Add(pages);
        (int Size, int Count) systemPages, graphicsPages;
        while (true)
        {
            systemPages = Layout(system, root, PageMargin);
            graphicsPages = graphics.Count == 0 ? (0, 0) : Layout(graphics, null, 0);
            int length = 16 + 8 * (Chunks(systemPages) + Chunks(graphicsPages));
            if (length <= pages.Data.Length)
                break;
            pages.Data = new byte[length];
        }
        pages.U8(8, Chunks(systemPages));
        pages.U8(9, Chunks(graphicsPages));
        Pointer(root, 0x08, pages);

        int systemLength = systemPages.Size * systemPages.Count;
        var content = new byte[systemLength + graphicsPages.Size * graphicsPages.Count];
        foreach (var block in _blocks.Append(pages))
        {
            ulong address = Address(block);
            if (block.Relocate is not null)
            {
                var data = block.Relocate(address);
                if (data.Length != block.Data.Length)
                    throw new InvalidOperationException("La taille d'un bloc a changé une fois placé.");
                block.Data = data;
            }
            foreach (var (at, target, offset) in block.Pointers)
                block.U64(at, Address(target) + (ulong)offset);
            block.Data.CopyTo(content.AsSpan((block.Graphics ? systemLength : 0) + block.Position));
        }

        uint systemFlags = ((uint)(version >> 4) & 0xF) << 28 | Flags(systemPages);
        uint graphicsFlags = ((uint)version & 0xF) << 28 | (graphicsPages.Count == 0 ? 0 : Flags(graphicsPages));
        return ResourceFile.Write(new ResourceHeader(version, systemFlags, graphicsFlags), content);
    }

    private static ulong Address(Block block) => (block.Graphics ? GraphicsBase : SystemBase) + (ulong)block.Position;

    // Pages of one size (a power of two), the root first at 0, then the others biggest first, each in the first page
    // with room left.
    private static (int Size, int Count) Layout(List<Block> blocks, Block? root, int margin)
    {
        int largest = blocks.Max(b => b.Data.Length) + margin;
        int pageSize = MinPageSize;
        while (pageSize < largest)
            pageSize *= 2;
        var order = blocks.Where(b => b != root).OrderByDescending(b => b.Data.Length).ToList();
        if (root is not null)
            order.Insert(0, root);
        while (true)
        {
            var used = new List<int>();
            foreach (var block in order)
            {
                int length = (block.Data.Length + 15) & ~15;
                int page = used.FindIndex(u => pageSize - u >= length + margin);
                if (page < 0)
                {
                    page = used.Count;
                    used.Add(0);
                }
                block.Position = page * pageSize + used[page];
                used[page] += length;
            }
            if (Units((pageSize, used.Count)) <= MaxUnits)
                return (pageSize, used.Count);
            pageSize *= 2;
            if (pageSize > MaxUnitSize * 16)
                throw new InvalidDataException("Ressource trop grande.");
        }
    }

    // A page bigger than the largest unit is written as several units; the flag word stores the count in units.
    private static int UnitSize((int Size, int Count) pages) => Math.Min(pages.Size, MaxUnitSize);

    private static int Units((int Size, int Count) pages) => pages.Count * (pages.Size / UnitSize(pages));

    private static uint Flags((int Size, int Count) pages) => MetaDocument.PageFlags(Units(pages), UnitSize(pages));

    // Pages actually described by the flag word (pages of 16, 8, 4, 2 and 1 units).
    private static int Chunks((int Size, int Count) pages)
    {
        if (pages.Count == 0)
            return 0;
        int units = Units(pages);
        return (units >> 4) + BitOperations.PopCount((uint)units & 0xF);
    }
}
