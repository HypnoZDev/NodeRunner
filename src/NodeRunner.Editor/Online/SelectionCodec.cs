using NodeRunner.Data.Maps;

namespace NodeRunner.Editor.Online;

// A participant's selected objects, shared so the others see them and leave them alone. Each object is written on its
// own: one that is not on the other computer yet (a change still on its way) is skipped there, not the whole list.
// Then one other selected element (a road node, a room...), as bytes the application chose. Nothing selected = empty.
public static class SelectionCodec
{
    public const int MaxObjects = 2000;

    public static byte[] Encode(MapLibrary library, IReadOnlyList<MapEntity> entities, byte[]? item = null)
    {
        if (entities.Count == 0 && item is not { Length: > 0 })
            return [];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var parts = new List<byte[]>();
        foreach (var entity in entities.TakeLast(MaxObjects))
        {
            using var part = new EditWriter(library);
            try
            {
                part.WriteEntity(entity);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            parts.Add(part.ToArray());
        }
        writer.Write(parts.Count);
        foreach (var part in parts)
        {
            writer.Write(part.Length);
            writer.Write(part);
        }
        writer.Write(item?.Length ?? 0);
        writer.Write(item ?? []);
        writer.Flush();
        return stream.ToArray();
    }

    // The other element (null if none).
    public static byte[]? DecodeItem(byte[] data)
    {
        if (data.Length < 4)
            return null;
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        try
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
                reader.BaseStream.Position += reader.ReadInt32();
            int length = reader.ReadInt32();
            return length is > 0 and <= 4096 ? reader.ReadBytes(length) : null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    // The objects found on this computer, main one last.
    public static List<MapEntity> Decode(byte[] data, EditContext context)
    {
        var found = new List<MapEntity>();
        if (data.Length < 4)
            return found;
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        try
        {
            int count = Math.Min(reader.ReadInt32(), MaxObjects);
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 0 || length > 4096)
                    break;
                var bytes = reader.ReadBytes(length);
                try
                {
                    using var part = new EditReader(bytes) { Context = context, FindOnly = true };
                    if (part.ReadEntity() is { IsDeleted: false } entity)
                        found.Add(entity);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                }
            }
        }
        catch (EndOfStreamException)
        {
        }
        return found;
    }
}
