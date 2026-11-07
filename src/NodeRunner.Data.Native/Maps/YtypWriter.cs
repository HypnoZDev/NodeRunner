using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Writes edited interiors (MLO) back to their archetype file (.ytyp).
public sealed class YtypWriter : IMapTypesWriter
{
    private const int PointerSize = 8;
    private const uint StringStructure = 0x10; // Blocks of characters.
    private const uint UIntStructure = 0x15; // Blocks of numbers (lists of objects of the rooms).
    private const uint CornerStructure = 0x33; // Blocks of 4 floats (corners of the portals).

    public MapFileContent Write(ReadOnlySpan<byte> source, IReadOnlyList<Archetype> interiors) => Write(source, interiors, null);

    // borrowed: descriptions of structures taken from other .ytyp files (Structure), needed when an entity set or a
    // portal is created in a file that has none (MissingStructures).
    public MapFileContent Write(ReadOnlySpan<byte> source, IReadOnlyList<Archetype> interiors, IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        var document = MetaDocument.Read(source);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YtypReader.MapTypesStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de types (.ytyp).");

        var items = PointerItems(document, (root, 0), YtypReader.TypesArchetypes);
        foreach (var interior in interiors)
        {
            var item = items.Find(i => document.Blocks[i.Block].Structure == YtypReader.InteriorArchetypeStructure
                                       && ReadUInt32(document, i, YtypReader.ArchName) == interior.NameHash);
            if (item == default)
                throw new InvalidDataException($"L'intérieur 0x{interior.NameHash:X8} n'est pas dans ce fichier.");
            var (setBlock, setOffset, setCount) = Array(document, item, YtypReader.InteriorEntitySets);
            if (setCount > interior.InteriorEntitySets.Count)
                throw new InvalidDataException("Les entity sets de l'intérieur ne sont pas ceux du fichier.");
            if (setCount < interior.InteriorEntitySets.Count)
            {
                (setBlock, setOffset) = AppendEntitySets(document, item, setBlock, setOffset, setCount, interior.InteriorEntitySets.Count, borrowed);
                setCount = interior.InteriorEntitySets.Count;
            }
            for (int s = 0; s < setCount; s++)
                BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[setBlock].Data.AsSpan(setOffset + s * YtypReader.EntitySetSize + YtypReader.EntitySetName),
                    interior.InteriorEntitySets[s].NameHash);

            // File data of every object, before anything is changed: an object moved into or out of a set keeps it.
            var originals = new Dictionary<MapEntity, byte[]>();
            CollectOriginals(document, PointerItems(document, item, YtypReader.InteriorEntities), interior.InteriorEntities, originals);
            for (int s = 0; s < setCount; s++)
                CollectOriginals(document, PointerItems(document, (setBlock, setOffset + s * YtypReader.EntitySetSize), YtypReader.EntitySetEntities),
                    interior.InteriorEntitySets[s].Entities, originals);

            // Rooms or portals added or removed: their lists are rebuilt. Then, or when an object changed room, the
            // objects of each room or portal are listed again from the objects' own room and portal.
            int itemCount = Array(document, item, YtypReader.InteriorEntities).Count;
            var layout = new Layout(Owners(document, item, YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomAttachedObjects, itemCount),
                Owners(document, item, YtypReader.InteriorPortals, YtypReader.PortalSize, YtypReader.PortalAttachedObjects, itemCount),
                [.. interior.InteriorRooms.Select(r => r.SourceIndex)], [.. interior.InteriorPortals.Select(p => p.SourceIndex)],
                LayoutChanged(document, item, interior));
            WriteRooms(document, item, interior, layout.Rebuilt, borrowed);
            WritePortals(document, item, interior.InteriorPortals, layout.Rebuilt, borrowed);
            WriteInteriorEntities(document, item, interior.InteriorEntities, originals, layout);
            for (int s = 0; s < setCount; s++)
                WriteEntitySet(document, item, (setBlock, setOffset + s * YtypReader.EntitySetSize), interior.InteriorEntitySets[s].Entities, originals,
                    interior.InteriorRooms.Count);
        }

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    // The description of a structure in a .ytyp, null if it has none.
    public static MetaDocument.StructureInfo? Structure(ReadOnlySpan<byte> source, uint structure) =>
        MetaDocument.Read(source).FindStructure(structure);

    // The structures the interiors need that the file does not describe (to borrow from another .ytyp).
    public static IReadOnlyList<uint> MissingStructures(ReadOnlySpan<byte> source, IReadOnlyList<Archetype> interiors)
    {
        var document = MetaDocument.Read(source);
        var needed = new List<uint>();
        if (interiors.Any(i => i.InteriorEntitySets.Count > 0))
            needed.Add(YtypReader.EntitySetStructure);
        if (interiors.Any(i => i.InteriorRooms.Count > 0))
            needed.Add(YtypReader.RoomStructure);
        if (interiors.Any(i => i.InteriorPortals.Count > 0))
            needed.Add(YtypReader.PortalStructure);
        return [.. needed.Where(n => document.FindStructure(n) is null)];
    }

    private static void RequireStructure(MetaDocument document, uint structure, IReadOnlyList<MetaDocument.StructureInfo>? borrowed, string what)
    {
        if (document.FindStructure(structure) is null)
            document.Structures.Add(borrowed?.FirstOrDefault(b => b.NameHash == structure)
                                    ?? throw new InvalidDataException($"description des {what} introuvable (aucun autre .ytyp n'en a)"));
    }

    // The file's room (or portal) of each object of the interior, by index in its list of objects (-1: none); Rebuilt:
    // rooms or portals added or removed.
    private sealed record Layout(int[] FileRooms, int[] FilePortals, int[] RoomSources, int[] PortalSources, bool Rebuilt)
    {
        // The item's file room and portal, as numbered now.
        public (int Room, int Portal) FilePlace(int item) =>
            (FileRooms[item] >= 0 ? System.Array.IndexOf(RoomSources, FileRooms[item]) : -1,
             FilePortals[item] >= 0 ? System.Array.IndexOf(PortalSources, FilePortals[item]) : -1);
    }

    private static bool LayoutChanged(MetaDocument document, (int Block, int Offset) interior, Archetype archetype) =>
        Array(document, interior, YtypReader.InteriorRooms).Count != archetype.InteriorRooms.Count
        || Array(document, interior, YtypReader.InteriorPortals).Count != archetype.InteriorPortals.Count
        || archetype.InteriorRooms.Select((r, i) => r.SourceIndex != i).Any(c => c)
        || archetype.InteriorPortals.Select((p, i) => p.SourceIndex != i).Any(c => c);

    private static int[] Owners(MetaDocument document, (int Block, int Offset) interior, int field, int size, int objectsField, int itemCount)
    {
        var owners = new int[itemCount];
        System.Array.Fill(owners, -1);
        var (block, offset, count) = Array(document, interior, field);
        for (int r = 0; r < count; r++)
        {
            var (listBlock, listOffset, listCount) = Array(document, (block, offset + r * size), objectsField);
            for (int i = 0; i < listCount; i++)
            {
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4));
                if (index < owners.Length)
                    owners[index] = r;
            }
        }
        return owners;
    }

    // A new array of rooms or portals: the file's ones (by SourceIndex) and new ones made from a template (an item of
    // the file, its name and lists cleared). Returns its place.
    private static (int Block, int Offset) Rebuild(MetaDocument document, (int Block, int Offset) interior, int field, int size, uint structure,
        IReadOnlyList<int> sources, int[] cleared)
    {
        var (block, offset, count) = Array(document, interior, field);
        var template = new byte[size];
        if (count > 0)
            document.Blocks[block].Data.AsSpan(offset + (count > 1 ? size : 0), size).CopyTo(template);
        foreach (int c in cleared)
            template.AsSpan(c, 12).Clear();
        var data = new byte[sources.Count * size];
        for (int i = 0; i < sources.Count; i++)
        {
            int source = sources[i];
            if (source >= count)
                throw new InvalidDataException("Les salles ou portails de l'intérieur ne sont pas ceux du fichier.");
            (source >= 0 ? document.Blocks[block].Data.AsSpan(offset + source * size, size) : template).CopyTo(data.AsSpan(i * size));
        }
        int newBlock = document.Blocks.Count;
        document.Blocks.Add(new MetaDocument.Block(structure, data));
        SetArray(document, interior, field, newBlock, 0, sources.Count);
        return (newBlock, 0);
    }

    // The interior's sets in a new block: the file's ones, then empty new ones (named by the caller).
    private static (int Block, int Offset) AppendEntitySets(MetaDocument document, (int Block, int Offset) interior, int block, int offset,
        int fileCount, int count, IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        RequireStructure(document, YtypReader.EntitySetStructure, borrowed, "entity sets");
        var data = new byte[count * YtypReader.EntitySetSize];
        if (fileCount > 0)
            document.Blocks[block].Data.AsSpan(offset, fileCount * YtypReader.EntitySetSize).CopyTo(data);
        int newBlock = document.Blocks.Count;
        document.Blocks.Add(new MetaDocument.Block(YtypReader.EntitySetStructure, data));
        SetArray(document, interior, YtypReader.InteriorEntitySets, newBlock, 0, count);
        return (newBlock, 0);
    }

    private static void CollectOriginals(MetaDocument document, List<(int Block, int Offset)> items, IReadOnlyList<MapEntity> entities,
        Dictionary<MapEntity, byte[]> into)
    {
        int entity = 0;
        foreach (var (block, offset) in items)
        {
            if (document.Blocks[block].Structure != YmapReader.EntityStructure || entity >= entities.Count)
                continue;
            into[entities[entity++]] = document.Blocks[block].Data.AsSpan(offset, YmapReader.EntitySize).ToArray();
        }
    }

    // Appends the added objects to a new block of entities; returns where each one is.
    private static List<(int Block, int Offset)> AddEntities(MetaDocument document, List<MapEntity> added, Dictionary<MapEntity, byte[]> originals)
    {
        var placed = new List<(int, int)>();
        if (added.Count == 0)
            return placed;
        var data = new byte[added.Count * YmapReader.EntitySize];
        int block = document.Blocks.Count;
        document.Blocks.Add(new MetaDocument.Block(YmapReader.EntityStructure, data));
        byte[]? template = null;
        for (int i = 0; i < added.Count; i++)
        {
            int o = i * YmapReader.EntitySize;
            var source = added[i].MovedFrom;
            while (source is not null && !originals.ContainsKey(source))
                source = source.MovedFrom;
            if (source is not null)
                originals[source].CopyTo(data, o);
            else
            {
                template ??= AnyEntity(document) ?? throw new InvalidDataException("aucun objet modèle dans ce fichier");
                template.CopyTo(data, o);
                YmapWriter.WriteNewEntityFields(data, o, added[i]);
            }
            WritePlacement(data, o, added[i]);
            placed.Add((block, o));
        }
        return placed;
    }

    // Box, name and settings of each room; their objects are written with the objects.
    private static void WriteRooms(MetaDocument document, (int Block, int Offset) interior, Archetype archetype, bool rebuild,
        IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        var rooms = archetype.InteriorRooms;
        var (block, offset, count) = Array(document, interior, YtypReader.InteriorRooms);
        if (rebuild)
        {
            RequireStructure(document, YtypReader.RoomStructure, borrowed, "salles");
            (block, offset) = Rebuild(document, interior, YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomStructure,
                [.. rooms.Select(r => r.SourceIndex)], [YtypReader.RoomName, YtypReader.RoomAttachedObjects]);
            count = rooms.Count;
        }
        if (count != rooms.Count)
            throw new InvalidDataException("Les salles de l'intérieur ne sont pas celles du fichier.");
        for (int r = 0; r < count; r++)
        {
            var room = rooms[r];
            int o = offset + r * YtypReader.RoomSize;
            var data = document.Blocks[block].Data;
            WriteVector3(data, o + YtypReader.RoomBoundsMin, room.BoundsMin);
            WriteVector3(data, o + YtypReader.RoomBoundsMax, room.BoundsMax);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.RoomTimecycle), room.TimecycleHash);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.RoomSecondaryTimecycle), room.SecondaryTimecycleHash);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.RoomFlags), room.Flags);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YtypReader.RoomFloorId), room.FloorId);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YtypReader.RoomExteriorVisibilityDepth), room.ExteriorVisibilityDepth);
            int index = r;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o + YtypReader.RoomPortalCount),
                archetype.InteriorPortals.Count(p => p.RoomFrom == index || p.RoomTo == index));
            if (ReadString(document, (block, o + YtypReader.RoomName)) != room.Name)
                WriteString(document, (block, o + YtypReader.RoomName), room.Name);
        }
    }

    // Corners, rooms and settings of each portal; their objects are written with the objects.
    private static void WritePortals(MetaDocument document, (int Block, int Offset) interior, IReadOnlyList<InteriorPortal> portals, bool rebuild,
        IReadOnlyList<MetaDocument.StructureInfo>? borrowed)
    {
        var (block, offset, count) = Array(document, interior, YtypReader.InteriorPortals);
        if (rebuild && portals.Count > 0)
        {
            uint cornerStructure = CornerStructure;
            if (count > 0 && Array(document, (block, offset), YtypReader.PortalCorners) is { Count: > 0 } first)
                cornerStructure = document.Blocks[first.Block].Structure;
            RequireStructure(document, YtypReader.PortalStructure, borrowed, "portails");
            (block, offset) = Rebuild(document, interior, YtypReader.InteriorPortals, YtypReader.PortalSize, YtypReader.PortalStructure,
                [.. portals.Select(p => p.SourceIndex)], [YtypReader.PortalCorners, YtypReader.PortalAttachedObjects]);
            count = portals.Count;
            // New portals: their own block of corners (the 4th float is padding).
            for (int p = 0; p < count; p++)
            {
                if (portals[p].SourceIndex >= 0)
                    continue;
                var corners = new byte[portals[p].Corners.Count * 16];
                for (int c = 0; c < portals[p].Corners.Count; c++)
                    WriteFloat(corners, c * 16 + 12, float.NaN);
                int cornerBlock = document.Blocks.Count;
                document.Blocks.Add(new MetaDocument.Block(cornerStructure, corners));
                SetArray(document, (block, offset + p * YtypReader.PortalSize), YtypReader.PortalCorners, cornerBlock, 0, portals[p].Corners.Count);
            }
        }
        else if (rebuild)
        {
            SetArray(document, interior, YtypReader.InteriorPortals, -1, 0, 0);
            count = 0;
        }
        if (count != portals.Count)
            throw new InvalidDataException("Les portails de l'intérieur ne sont pas ceux du fichier.");
        for (int p = 0; p < count; p++)
        {
            var portal = portals[p];
            int o = offset + p * YtypReader.PortalSize;
            var data = document.Blocks[block].Data;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalRoomFrom), (uint)portal.RoomFrom);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalRoomTo), (uint)portal.RoomTo);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalFlags), portal.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalMirrorPriority), (uint)portal.MirrorPriority);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalOpacity), (uint)portal.Opacity);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o + YtypReader.PortalAudioOcclusion), portal.AudioOcclusionHash);
            // Corners: 16 bytes each (the 4th float is padding, kept).
            var (cornerBlock, cornerOffset, cornerCount) = Array(document, (block, o), YtypReader.PortalCorners);
            if (cornerCount != portal.Corners.Count)
                throw new InvalidDataException("Les coins du portail ne sont pas ceux du fichier.");
            for (int c = 0; c < cornerCount; c++)
                WriteVector3(document.Blocks[cornerBlock].Data, cornerOffset + c * 16, portal.Corners[c]);
        }
    }

    private static string ReadString(MetaDocument document, (int Block, int Offset) field)
    {
        var (block, offset, count) = Array(document, field, 0);
        if (count == 0)
            return "";
        var data = document.Blocks[block].Data.AsSpan(offset, count);
        int end = data.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end >= 0 ? data[..end] : data);
    }

    // A new block of characters, with its terminating zero (not counted, but in the capacity, as in the game's files).
    private static void WriteString(MetaDocument document, (int Block, int Offset) field, string text)
    {
        var span = document.Blocks[field.Block].Data.AsSpan(field.Offset);
        if (text.Length == 0)
        {
            span[..12].Clear();
            return;
        }
        var bytes = new byte[text.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(text, bytes);
        int block = document.Blocks.Count;
        document.Blocks.Add(new MetaDocument.Block(StringStructure, bytes));
        BinaryPrimitives.WriteUInt64LittleEndian(span, MetaDocument.Pointer(block, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)bytes.Length);
    }

    // The interior's objects: those deleted left out, those added (after the file's ones) appended, each in its room.
    // The rooms and portals list their objects by index, renumbered.
    private static void WriteInteriorEntities(MetaDocument document, (int Block, int Offset) interior, IReadOnlyList<MapEntity> entities,
        Dictionary<MapEntity, byte[]> originals, Layout layout)
    {
        var items = PointerItems(document, interior, YtypReader.InteriorEntities);
        int fileCount = WriteEntities(document, items, entities);

        var newIndex = new int[items.Count];
        var kept = new List<(int Block, int Offset)>();
        // Room and portal of each kept item: the object's own, or the file's one for other items.
        var places = new List<(int Room, int Portal)>();
        bool relist = layout.Rebuilt;
        int entity = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bool isEntity = document.Blocks[items[i].Block].Structure == YmapReader.EntityStructure;
            bool keep = !isEntity || !entities[entity].IsDeleted;
            if (keep)
            {
                var file = i < layout.FileRooms.Length ? layout.FilePlace(i) : (-1, -1);
                var own = isEntity ? (entities[entity].InteriorRoom, entities[entity].InteriorPortal) : file;
                relist |= own != file;
                places.Add(own);
            }
            if (isEntity)
                entity++;
            newIndex[i] = keep ? kept.Count : -1;
            if (keep)
                kept.Add(items[i]);
        }
        var added = entities.Skip(fileCount).Where(e => !e.IsDeleted).ToList();
        if (kept.Count == items.Count && added.Count == 0 && !relist)
            return;

        var additions = new List<(int Index, int Room)>();
        var placed = AddEntities(document, added, originals);
        for (int i = 0; i < placed.Count; i++)
        {
            additions.Add((kept.Count, added[i].InteriorRoom));
            kept.Add(placed[i]);
        }

        WritePointers(document, interior, YtypReader.InteriorEntities, kept);
        if (!relist)
        {
            Renumber(document, interior, YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomAttachedObjects, newIndex, additions);
            Renumber(document, interior, YtypReader.InteriorPortals, YtypReader.PortalSize, YtypReader.PortalAttachedObjects, newIndex, []);
            return;
        }
        places.AddRange(added.Select(e => (e.InteriorRoom, e.InteriorPortal)));
        WriteOwnerLists(document, interior, YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomAttachedObjects, [.. places.Select(p => p.Room)]);
        WriteOwnerLists(document, interior, YtypReader.InteriorPortals, YtypReader.PortalSize, YtypReader.PortalAttachedObjects, [.. places.Select(p => p.Portal)]);
    }

    // The objects of each room (or portal), listed again from each object's own room (or portal).
    private static void WriteOwnerLists(MetaDocument document, (int Block, int Offset) interior, int field, int size, int objectsField, int[] ownerOf)
    {
        var (block, offset, count) = Array(document, interior, field);
        for (int r = 0; r < count; r++)
        {
            var list = new List<uint>();
            for (int i = 0; i < ownerOf.Length; i++)
            {
                if (ownerOf[i] == r)
                    list.Add((uint)i);
            }
            WriteList(document, (block, offset + r * size), objectsField, list);
        }
    }

    // Replaces a list of numbers, in place when it does not grow, otherwise in a new block.
    private static void WriteList(MetaDocument document, (int Block, int Offset) owner, int field, List<uint> list)
    {
        var (listBlock, listOffset, listCount) = Array(document, owner, field);
        if (list.Count > listCount)
        {
            listBlock = document.Blocks.Count;
            listOffset = 0;
            document.Blocks.Add(new MetaDocument.Block(UIntStructure, new byte[list.Count * 4]));
        }
        for (int i = 0; i < list.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4), list[i]);
        SetArray(document, owner, field, listBlock, listOffset, list.Count);
    }

    // The objects of an entity set, those deleted left out along with their room in the set's list of rooms (same
    // index as the pointer).
    // Objects added to the set are appended, with their room.
    // roomCount: the interior's rooms; the objects' own rooms in range replace the file's ones (odd values kept).
    private static void WriteEntitySet(MetaDocument document, (int Block, int Offset) interior, (int Block, int Offset) set,
        IReadOnlyList<MapEntity> entities, Dictionary<MapEntity, byte[]> originals, int roomCount)
    {
        var items = PointerItems(document, set, YtypReader.EntitySetEntities);
        int fileCount = WriteEntities(document, items, entities);

        var (locationBlock, locationOffset, locationCount) = Array(document, set, YtypReader.EntitySetLocations);
        var kept = new List<(int Block, int Offset)>();
        var rooms = new List<uint>();
        bool roomChanged = false;
        int entity = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bool isEntity = document.Blocks[items[i].Block].Structure == YmapReader.EntityStructure;
            bool keep = !isEntity || !entities[entity].IsDeleted;
            int own = isEntity ? entities[entity].InteriorRoom : -1;
            if (isEntity)
                entity++;
            if (!keep)
                continue;
            kept.Add(items[i]);
            if (i < locationCount)
            {
                uint file = BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[locationBlock].Data.AsSpan(locationOffset + i * 4));
                uint room = own >= 0 && own < roomCount ? (uint)own : file;
                roomChanged |= room != file;
                rooms.Add(room);
            }
        }
        var added = entities.Skip(fileCount).Where(e => !e.IsDeleted).ToList();
        if (kept.Count == items.Count && added.Count == 0 && !roomChanged)
            return;

        if (added.Count > 0)
        {
            while (rooms.Count < kept.Count)
                rooms.Add(0);
            kept.AddRange(AddEntities(document, added, originals));
            // A few game sets hold odd values (0x80000001): kept as they are; no room = limbo.
            rooms.AddRange(added.Select(e => e.InteriorRoom == -1 ? 0u : (uint)e.InteriorRoom));
        }
        WritePointers(document, set, YtypReader.EntitySetEntities, kept);
        if (rooms.Count > locationCount)
        {
            locationBlock = document.Blocks.Count;
            locationOffset = 0;
            document.Blocks.Add(new MetaDocument.Block(UIntArrayStructure(document, interior), new byte[rooms.Count * 4]));
        }
        for (int i = 0; i < rooms.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[locationBlock].Data.AsSpan(locationOffset + i * 4), rooms[i]);
        SetArray(document, set, YtypReader.EntitySetLocations, locationBlock, locationOffset, rooms.Count);
    }

    // The structure of the blocks holding arrays of numbers: that of a set's list of rooms, or of a room's list of
    // objects.
    private static uint UIntArrayStructure(MetaDocument document, (int Block, int Offset) interior)
    {
        foreach (var (field, size, list) in new[]
                 {
                     (YtypReader.InteriorEntitySets, YtypReader.EntitySetSize, YtypReader.EntitySetLocations),
                     (YtypReader.InteriorRooms, YtypReader.RoomSize, YtypReader.RoomAttachedObjects),
                 })
        {
            var (block, offset, count) = Array(document, interior, field);
            for (int i = 0; i < count; i++)
            {
                var (listBlock, _, listCount) = Array(document, (block, offset + i * size), list);
                if (listCount > 0)
                    return document.Blocks[listBlock].Structure;
            }
        }
        throw new InvalidDataException("aucune liste de salles dans ce fichier");
    }

    private static byte[]? AnyEntity(MetaDocument document)
    {
        var block = document.Blocks.Find(b => b.Structure == YmapReader.EntityStructure && b.Data.Length >= YmapReader.EntitySize);
        return block?.Data.AsSpan(0, YmapReader.EntitySize).ToArray();
    }

    // Replaces a pointer array, in place when it does not grow, otherwise in a new block.
    private static void WritePointers(MetaDocument document, (int Block, int Offset) item, int field, List<(int Block, int Offset)> targets)
    {
        var (block, offset, count) = Array(document, item, field);
        if (targets.Count > count)
        {
            uint structure = block >= 0 ? document.Blocks[block].Structure : PointerArrayStructure(document);
            block = document.Blocks.Count;
            offset = 0;
            document.Blocks.Add(new MetaDocument.Block(structure, new byte[targets.Count * PointerSize]));
        }
        if (block >= 0)
        {
            var data = document.Blocks[block].Data;
            for (int i = 0; i < targets.Count; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + i * PointerSize), MetaDocument.Pointer(targets[i].Block, targets[i].Offset));
        }
        SetArray(document, item, field, block, offset, targets.Count);
    }

    // The structure of the blocks holding arrays of pointers: that of the file's list of archetypes.
    private static uint PointerArrayStructure(MetaDocument document)
    {
        var (block, _, _) = Array(document, (document.RootBlock, 0), YtypReader.TypesArchetypes);
        return document.Blocks[block].Structure;
    }

    // The object lists (indices) of the rooms or portals of an interior: renumbered, with the added objects appended
    // to their room.
    private static void Renumber(MetaDocument document, (int Block, int Offset) interior, int field, int size, int objectsField,
        int[] newIndex, List<(int Index, int Room)> additions)
    {
        var (block, offset, count) = Array(document, interior, field);
        uint? listStructure = null;
        for (int r = 0; r < count; r++)
        {
            var (listBlock, _, listCount) = Array(document, (block, offset + r * size), objectsField);
            if (listCount > 0)
                listStructure ??= document.Blocks[listBlock].Structure;
        }
        for (int r = 0; r < count; r++)
        {
            var owner = (block, offset + r * size);
            var (listBlock, listOffset, listCount) = Array(document, owner, objectsField);
            var list = new List<uint>();
            for (int i = 0; i < listCount; i++)
            {
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4));
                if (index >= newIndex.Length)
                    list.Add(index);
                else if (newIndex[index] >= 0)
                    list.Add((uint)newIndex[index]);
            }
            list.AddRange(additions.Where(a => a.Room == r).Select(a => (uint)a.Index));
            if (list.Count > listCount)
            {
                listBlock = document.Blocks.Count;
                listOffset = 0;
                document.Blocks.Add(new MetaDocument.Block(listStructure ?? throw new InvalidDataException("aucune liste d'objets de salle dans ce fichier"),
                    new byte[list.Count * 4]));
            }
            for (int i = 0; i < list.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(document.Blocks[listBlock].Data.AsSpan(listOffset + i * 4), list[i]);
            SetArray(document, owner, objectsField, listBlock, listOffset, list.Count);
        }
    }

    // Pointer, count and capacity of an array field; an empty array points nowhere.
    private static void SetArray(MetaDocument document, (int Block, int Offset) item, int field, int block, int offset, int count)
    {
        var span = document.Blocks[item.Block].Data.AsSpan(item.Offset + field);
        BinaryPrimitives.WriteUInt64LittleEndian(span, count == 0 ? 0 : MetaDocument.Pointer(block, offset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)count);
    }

    // The objects of the file are the first ones of the archetype read from it, in the same order (the reader skips
    // the pointers to other structures); returns how many.
    private static int WriteEntities(MetaDocument document, List<(int Block, int Offset)> items, IReadOnlyList<MapEntity> entities)
    {
        var objects = items.Where(i => document.Blocks[i.Block].Structure == YmapReader.EntityStructure).ToList();
        if (objects.Count > entities.Count)
            throw new InvalidDataException("Les objets de l'intérieur ne sont pas ceux du fichier.");
        for (int i = 0; i < objects.Count; i++)
        {
            if (ReadUInt32(document, objects[i], YmapReader.EntityArchetype) != entities[i].ArchetypeHash)
                throw new InvalidDataException($"L'objet n° {i + 1} de l'intérieur n'est pas celui du fichier.");
            WritePlacement(document.Blocks[objects[i].Block].Data, objects[i].Offset, entities[i]);
        }
        return objects.Count;
    }

    private static void WritePlacement(byte[] data, int o, MapEntity entity)
    {
        WriteVector3(data, o + YmapReader.EntityPosition, entity.Position);
        var r = entity.StoredRotation;
        WriteVector3(data, o + YmapReader.EntityRotation, new Vector3(r.X, r.Y, r.Z));
        WriteFloat(data, o + YmapReader.EntityRotation + 12, r.W);
        WriteFloat(data, o + YmapReader.EntityScaleXy, entity.Scale.X);
        WriteFloat(data, o + YmapReader.EntityScaleZ, entity.Scale.Z);
    }

    private static (int Block, int Offset, int Count) Array(MetaDocument document, (int Block, int Offset) item, int field)
    {
        var span = document.Blocks[item.Block].Data.AsSpan(item.Offset + field);
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(span));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        return block < 0 ? (-1, 0, 0) : (block, offset, count);
    }

    private static List<(int Block, int Offset)> PointerItems(MetaDocument document, (int Block, int Offset) item, int field)
    {
        var items = new List<(int, int)>();
        var (block, offset, count) = Array(document, item, field);
        if (count == 0)
            return items;
        var array = document.Blocks[block].Data;
        for (int i = 0; i < count; i++)
        {
            var target = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(offset + i * PointerSize)));
            if (target.Block >= 0)
                items.Add(target);
        }
        return items;
    }

    private static uint ReadUInt32(MetaDocument document, (int Block, int Offset) item, int field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(document.Blocks[item.Block].Data.AsSpan(item.Offset + field));

    private static void WriteVector3(byte[] data, int offset, Vector3 v)
    {
        WriteFloat(data, offset, v.X);
        WriteFloat(data, offset + 4, v.Y);
        WriteFloat(data, offset + 8, v.Z);
    }

    private static void WriteFloat(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
}
