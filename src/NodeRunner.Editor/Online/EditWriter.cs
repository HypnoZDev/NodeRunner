using System.Numerics;
using System.Text;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor.Online;

// Writes an edit as bytes for the other computers of a session. Objects are written as stable references (map, then
// position in its list): every computer starts from the same map and applies the same edits in the same order, and
// lists only grow (a deleted object is marked, never removed), so a reference points to the same object everywhere.
public sealed class EditWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    private readonly MapLibrary _library;

    public EditWriter(MapLibrary library)
    {
        _library = library;
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public byte[] ToArray()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }

    public void Write(bool value) => _writer.Write(value);
    public void Write(byte value) => _writer.Write(value);
    public void Write(int value) => _writer.Write(value);
    public void Write(uint value) => _writer.Write(value);
    public void Write(ushort value) => _writer.Write(value);
    public void Write(float value) => _writer.Write(value);
    public void Write(string value) => _writer.Write(value);

    public void Write(Vector3 v)
    {
        _writer.Write(v.X);
        _writer.Write(v.Y);
        _writer.Write(v.Z);
    }

    public void Write(Quaternion q)
    {
        _writer.Write(q.X);
        _writer.Write(q.Y);
        _writer.Write(q.Z);
        _writer.Write(q.W);
    }

    public void Write(Placement p)
    {
        Write(p.Position);
        Write(p.Orientation);
        Write(p.Scale);
    }

    public void WriteList<T>(IReadOnlyCollection<T> items, Action<T> write)
    {
        _writer.Write(items.Count);
        foreach (var item in items)
            write(item);
    }

    public void WriteMap(MapData map)
    {
        // Maps made by the user are found by file name (the other computer may not have made it yet).
        _writer.Write(map.IsCustom);
        if (map.IsCustom)
        {
            _writer.Write(map.FilePath ?? throw new InvalidOperationException("Custom map without a file name."));
            return;
        }
        _writer.Write(map.NameHash);
        _writer.Write(Ordinal(map));
    }

    // Maps sharing a name are told apart by their order in the library.
    private int Ordinal(MapData map)
    {
        int ordinal = 0;
        foreach (var other in _library.Maps)
        {
            if (other == map)
                return ordinal;
            if (other.NameHash == map.NameHash)
                ordinal++;
        }
        throw new InvalidOperationException("Map not in the library.");
    }

    // A placed object: in a map, in an interior instance, or in an entity set of an interior instance.
    public void WriteEntity(MapEntity entity)
    {
        if (entity.Interior is not { } instance)
        {
            _writer.Write((byte)0);
            WriteMap(entity.Map ?? throw new InvalidOperationException("Object without a map."));
            _writer.Write(IndexOf(entity.Map.Entities, entity));
        }
        else if (entity.EntitySet is { } set)
        {
            _writer.Write((byte)2);
            WriteEntity(instance);
            _writer.Write(IndexOf(instance.EntitySets, set));
            _writer.Write(IndexOf(set.Entities, entity));
        }
        else
        {
            _writer.Write((byte)1);
            WriteEntity(instance);
            _writer.Write(IndexOf(instance.InteriorEntities, entity));
        }
    }

    public void WriteEntities(IReadOnlyCollection<MapEntity> entities) => WriteList(entities, WriteEntity);

    private static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : class
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
                return i;
        }
        throw new InvalidOperationException("Object not found in its list.");
    }

    // A new object (not yet in any list), with everything the editor sets on it.
    public void WriteNewEntity(MapEntity e)
    {
        _writer.Write(e.ArchetypeHash);
        Write(e.Position);
        Write(e.StoredRotation);
        Write(e.Scale);
        _writer.Write(e.Flags);
        _writer.Write(e.Guid);
        _writer.Write(e.LodDistance);
        _writer.Write(e.ChildLodDistance);
        _writer.Write((int)e.LodLevel);
        _writer.Write(e.ParentIndex);
        _writer.Write(e.InteriorRoom);
        _writer.Write(e.InteriorPortal);
    }

    public void Write(InteriorRoom room)
    {
        Write(room.BoundsMin);
        Write(room.BoundsMax);
        _writer.Write(room.Name);
        _writer.Write(room.Flags);
        _writer.Write(room.ExteriorVisibilityDepth);
        _writer.Write(room.TimecycleHash);
        _writer.Write(room.SecondaryTimecycleHash);
        _writer.Write(room.FloorId);
        _writer.Write(room.SourceIndex);
    }

    public void Write(InteriorPortal portal)
    {
        _writer.Write(portal.RoomFrom);
        _writer.Write(portal.RoomTo);
        _writer.Write(portal.Flags);
        WriteList(portal.Corners, Write);
        _writer.Write(portal.MirrorPriority);
        _writer.Write(portal.Opacity);
        _writer.Write(portal.AudioOcclusionHash);
        _writer.Write(portal.SourceIndex);
    }
}
