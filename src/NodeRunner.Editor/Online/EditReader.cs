using System.Numerics;
using System.Text;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor.Online;

// Reads what EditWriter wrote, finding the referenced objects in this computer's map.
public sealed class EditReader : IDisposable
{
    private readonly BinaryReader _reader;

    public EditReader(byte[] data, int offset = 0) =>
        _reader = new BinaryReader(new MemoryStream(data, offset, data.Length - offset, writable: false), Encoding.UTF8);

    public required EditContext Context { get; init; }

    private MapLibrary Library => Context.Library;

    public bool AtEnd => _reader.BaseStream.Position == _reader.BaseStream.Length;

    public void Dispose() => _reader.Dispose();

    public bool ReadBool() => _reader.ReadBoolean();
    public byte ReadByte() => _reader.ReadByte();
    public int ReadInt() => _reader.ReadInt32();
    public uint ReadUInt() => _reader.ReadUInt32();
    public ushort ReadUShort() => _reader.ReadUInt16();
    public float ReadFloat() => _reader.ReadSingle();
    public string ReadString() => _reader.ReadString();

    public Vector3 ReadVector3() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

    public Quaternion ReadQuaternion() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

    public Placement ReadPlacement() => new(ReadVector3(), ReadQuaternion(), ReadVector3());

    public List<T> ReadList<T>(Func<T> read)
    {
        int count = _reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
            throw new InvalidDataException("Invalid list size.");
        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
            list.Add(read());
        return list;
    }

    public MapData ReadMap()
    {
        if (_reader.ReadBoolean())
        {
            string fileName = _reader.ReadString();
            return Library.FindCustomMap(fileName) ?? Library.CreateCustomMap(fileName);
        }
        uint nameHash = _reader.ReadUInt32();
        int ordinal = _reader.ReadInt32();
        foreach (var map in Library.Maps)
        {
            if (map.NameHash == nameHash && ordinal-- == 0)
                return map;
        }
        throw new InvalidDataException($"Map 0x{nameHash:X8} not loaded here.");
    }

    public MapEntity ReadEntity()
    {
        switch (_reader.ReadByte())
        {
            case 0:
                return At(ReadMap().Entities, _reader.ReadInt32());
            case 1:
                return At(ReadEntity().InteriorEntities, _reader.ReadInt32());
            case 2:
                var instance = ReadEntity();
                var set = At(instance.EntitySets, _reader.ReadInt32());
                return At(set.Entities, _reader.ReadInt32());
            default:
                throw new InvalidDataException("Unknown object reference.");
        }
    }

    public List<MapEntity> ReadEntities() => ReadList(ReadEntity);

    private static T At<T>(IReadOnlyList<T> list, int index) =>
        index >= 0 && index < list.Count ? list[index] : throw new InvalidDataException("Object not found here (the maps differ).");

    public MapEntity ReadNewEntity() => new()
    {
        ArchetypeHash = _reader.ReadUInt32(),
        Position = ReadVector3(),
        StoredRotation = ReadQuaternion(),
        Scale = ReadVector3(),
        Flags = _reader.ReadUInt32(),
        Guid = _reader.ReadUInt32(),
        LodDistance = _reader.ReadSingle(),
        ChildLodDistance = _reader.ReadSingle(),
        LodLevel = (LodLevel)_reader.ReadInt32(),
        ParentIndex = _reader.ReadInt32(),
        InteriorRoom = _reader.ReadInt32(),
        InteriorPortal = _reader.ReadInt32(),
    };

    public InteriorRoom ReadRoom() => new(ReadVector3(), ReadVector3())
    {
        Name = _reader.ReadString(),
        Flags = _reader.ReadUInt32(),
        ExteriorVisibilityDepth = _reader.ReadInt32(),
        TimecycleHash = _reader.ReadUInt32(),
        SecondaryTimecycleHash = _reader.ReadUInt32(),
        FloorId = _reader.ReadInt32(),
        SourceIndex = _reader.ReadInt32(),
    };

    public InteriorPortal ReadPortal()
    {
        int from = _reader.ReadInt32(), to = _reader.ReadInt32();
        uint flags = _reader.ReadUInt32();
        return new InteriorPortal(from, to, flags)
        {
            Corners = ReadList(ReadVector3),
            MirrorPriority = _reader.ReadInt32(),
            Opacity = _reader.ReadInt32(),
            AudioOcclusionHash = _reader.ReadUInt32(),
            SourceIndex = _reader.ReadInt32(),
        };
    }
}
