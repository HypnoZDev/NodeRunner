using System.Buffers.Binary;

namespace NodeRunner.Data.Native.Resources;

// Descriptions of Meta structures and enums, gathered from game files: what MetaXml needs to place each field of an
// XML export. sources: Meta contents (decompressed); the first one is read whole when first needed (a structure
// described differently by older files: the largest description wins), the next ones only file by file until a
// missing structure is found.
public sealed class MetaSchema(params IEnumerable<byte[]>[] sources)
{
    private readonly Dictionary<uint, MetaDocument.StructureInfo> _structures = new();
    private readonly Dictionary<uint, MetaDocument.EnumInfo> _enums = new();
    private readonly Queue<IEnumerator<byte[]>> _pending = new(sources.Skip(1).Select(s => s.GetEnumerator()));
    private readonly IEnumerable<byte[]>? _main = sources.FirstOrDefault();
    private bool _mainRead;
    private MetaDocument? _template;

    // A document of the game, for the header of written files.
    public MetaDocument Template
    {
        get
        {
            ReadMain();
            while (_template is null && ReadNext())
            {
            }
            return _template ?? throw new InvalidDataException("aucun fichier Meta du jeu n'a pu être lu");
        }
    }

    public MetaDocument.StructureInfo? Structure(uint hash)
    {
        ReadMain();
        while (!_structures.ContainsKey(hash) && ReadNext())
        {
        }
        return _structures.GetValueOrDefault(hash);
    }

    public MetaDocument.EnumInfo? Enum(uint hash)
    {
        ReadMain();
        while (!_enums.ContainsKey(hash) && ReadNext())
        {
        }
        return _enums.GetValueOrDefault(hash);
    }

    public void Add(MetaDocument document)
    {
        _template ??= document;
        foreach (var structure in document.Structures)
        {
            if (!_structures.TryGetValue(structure.NameHash, out var known) || structure.Size > known.Size)
                _structures[structure.NameHash] = structure;
        }
        foreach (var e in document.Enums)
            _enums.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(e.Info), e);
    }

    private void ReadMain()
    {
        if (_mainRead)
            return;
        _mainRead = true;
        if (_main is null)
            return;
        foreach (var content in _main)
            TryAdd(content);
    }

    private bool ReadNext()
    {
        while (_pending.TryPeek(out var source))
        {
            if (source.MoveNext())
            {
                TryAdd(source.Current);
                return true;
            }
            source.Dispose();
            _pending.Dequeue();
        }
        return false;
    }

    private void TryAdd(byte[] content)
    {
        try
        {
            Add(MetaDocument.Read(content));
        }
        catch (InvalidDataException)
        {
            // Not a Meta file (or a damaged one): skipped.
        }
    }
}
