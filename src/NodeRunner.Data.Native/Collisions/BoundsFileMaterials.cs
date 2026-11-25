namespace NodeRunner.Data.Native.Collisions;

// Changing the material and the options of polygons. A geometry lists its materials (8 bytes each: the index in
// materials.dat, then procedural id, room and ped density, then the options in bytes 3 and 4) and gives each polygon one of them: a material the geometry does not list yet is added to a new,
// longer list appended at the end of the file (as is a per-polygon array the geometry did not have).
internal sealed partial class BoundsFile
{
    private const int MaterialEntrySize = 8;

    private readonly List<(int PointerField, byte[] Block)> _appended = [];

    public void SetMaterials(int o, IReadOnlyDictionary<int, byte> changes, IReadOnlyDictionary<int, ushort> flags, ref int polygon)
    {
        switch (U8(o + TypeOffset))
        {
            case Composite:
                int count = U16(o + ChildrenCount1);
                int children = Resolve(U64(o + ChildrenPointer), count, 8);
                for (int i = 0; i < count; i++)
                {
                    int child = Resolve(U64(children + i * 8), 1, 0x70);
                    if (child >= 0)
                        SetMaterials(child, changes, flags, ref polygon);
                }
                break;
            case Geometry:
            case GeometryBvh:
                SetGeometryMaterials(o, changes, flags, ref polygon);
                break;
        }
    }

    private void SetGeometryMaterials(int o, IReadOnlyDictionary<int, byte> changes, IReadOnlyDictionary<int, ushort> flags, ref int polygon)
    {
        int count = (int)U32(o + PolygonsCount);
        int first = polygon;
        polygon += count;
        var changed = Enumerable.Range(0, count).Where(i => changes.ContainsKey(first + i) || flags.ContainsKey(first + i)).ToList();
        int materialCount = U8(o + MaterialsCount);
        int materials = Resolve(U64(o + MaterialsPointer), Math.Max(materialCount, 1), MaterialEntrySize);
        if (changed.Count == 0 || materials < 0 || materialCount == 0)
            return;

        var entries = Enumerable.Range(0, materialCount)
            .Select(j => _data.AsSpan(materials + j * MaterialEntrySize, MaterialEntrySize).ToArray()).ToList();
        int indicesAt = Resolve(U64(o + PolygonMaterialsPointer), count, 1);
        byte[] indices = indicesAt >= 0 ? _data.AsSpan(indicesAt, count).ToArray() : new byte[count];

        bool compacted = false;
        foreach (int i in changed)
        {
            // Same entry with another material index and / or other options (its other bytes, room..., are kept).
            var wanted = (byte[])entries[Math.Min(indices[i], entries.Count - 1)].Clone();
            if (changes.TryGetValue(first + i, out byte material))
                wanted[0] = material;
            if (flags.TryGetValue(first + i, out ushort options))
            {
                wanted[3] = (byte)options;
                wanted[4] = (byte)(options >> 8);
            }
            int slot = entries.FindIndex(e => e.AsSpan().SequenceEqual(wanted));
            if (slot < 0)
            {
                if (entries.Count >= 255)
                {
                    Compact(entries, indices);
                    compacted = true;
                }
                if (entries.Count >= 255)
                {
                    // No room left: an entry with this material and these options, other details aside.
                    slot = entries.FindIndex(e => e[0] == wanted[0] && e[3] == wanted[3] && e[4] == wanted[4]);
                    if (slot < 0)
                        throw new InvalidDataException("Trop de matériaux différents dans ce morceau de collision.");
                }
                else
                {
                    entries.Add(wanted);
                    slot = entries.Count - 1;
                }
            }
            indices[i] = (byte)slot;
        }

        if (entries.Count != materialCount || compacted)
        {
            _appended.Add((o + MaterialsPointer, entries.SelectMany(e => e).ToArray()));
            At(o + MaterialsCount, 1)[0] = (byte)entries.Count;
        }
        if (indicesAt >= 0)
            indices.CopyTo(_data, indicesAt);
        else
            _appended.Add((o + PolygonMaterialsPointer, indices));
    }

    // Keeps only the entries some polygon still uses (indices renumbered).
    private static void Compact(List<byte[]> entries, byte[] indices)
    {
        var used = indices.Where(s => s < entries.Count).Distinct().Order().ToList();
        var remap = new Dictionary<byte, byte>();
        var kept = new List<byte[]>();
        foreach (byte s in used)
        {
            remap[s] = (byte)kept.Count;
            kept.Add(entries[s]);
        }
        for (int i = 0; i < indices.Length; i++)
            indices[i] = remap.TryGetValue(indices[i], out byte n) ? n : (byte)0;
        entries.Clear();
        entries.AddRange(kept);
    }

    // The file with the blocks planned by SetMaterials appended (16-byte aligned) and pointed to.
    public byte[] WithAppended()
    {
        if (_appended.Count == 0)
            return _data;
        int length = _data.Length;
        var offsets = new List<int>();
        foreach (var (_, block) in _appended)
        {
            length = (length + 15) & ~15;
            offsets.Add(length);
            length += block.Length;
        }
        var grown = new byte[length];
        _data.CopyTo(grown, 0);
        for (int i = 0; i < _appended.Count; i++)
        {
            var (field, block) = _appended[i];
            block.CopyTo(grown, offsets[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(grown.AsSpan(field), 0x50000000UL + (ulong)offsets[i]);
        }
        return grown;
    }
}
