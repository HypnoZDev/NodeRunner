using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;

namespace NodeRunner.Editor.Online;

// Codecs of the editor's edits. Each Apply makes the change through the same calls as the local edit.

internal sealed class PlacementCodec : EditCodec<PlacementEdit>
{
    public override string Tag => "move";

    protected override void Write(PlacementEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Entity);
        w.Write(edit.After);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var entity = r.ReadEntity();
        var after = r.ReadPlacement();
        var before = Placement.Of(entity);
        after.ApplyTo(entity);
        return new PlacementEdit(entity, before, after, description);
    }
}

internal sealed class GroupPlacementCodec : EditCodec<GroupPlacementEdit>
{
    public override string Tag => "move-group";

    protected override void Write(GroupPlacementEdit edit, EditWriter w)
    {
        w.WriteEntities(edit.Selection);
        w.WriteList(edit.After, w.Write);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var entities = r.ReadEntities();
        var after = r.ReadList(r.ReadPlacement);
        if (after.Count != entities.Count)
            throw new InvalidDataException("Group sizes differ.");
        var before = entities.Select(Placement.Of).ToList();
        for (int i = 0; i < entities.Count; i++)
            after[i].ApplyTo(entities[i]);
        return new GroupPlacementEdit(entities, before, after, description);
    }
}

internal sealed class AddCodec : EditCodec<AddEdit>
{
    public override string Tag => "add";

    protected override void Write(AddEdit edit, EditWriter w)
    {
        var entities = edit.Entities.ToList();
        w.WriteMap(entities[0].Map ?? throw new InvalidOperationException("Added object without a map."));
        w.WriteList(entities, w.WriteNewEntity);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var map = r.ReadMap();
        return AddEdit.Apply(r.Context.Library, map, r.ReadList(r.ReadNewEntity), description);
    }
}

internal sealed class InteriorAddCodec : EditCodec<InteriorAddEdit>
{
    public override string Tag => "add-interior";

    protected override void Write(InteriorAddEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Selection[0].Interior ?? throw new InvalidOperationException("Not in an interior."));
        w.WriteList(edit.Selection, p => w.WriteNewEntity(p.Definition ?? throw new InvalidOperationException("No definition.")));
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var instance = r.ReadEntity();
        return InteriorAddEdit.Apply(r.Context.Library, instance, r.ReadList(r.ReadNewEntity), description);
    }
}

internal sealed class EntitySetMoveCodec : EditCodec<EntitySetMoveEdit>
{
    public override string Tag => "entity-set-move";

    protected override void Write(EntitySetMoveEdit edit, EditWriter w)
    {
        w.WriteEntities(edit.OldPlaced);
        w.Write(edit.SetIndex);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var placed = r.ReadEntities();
        return EntitySetMoveEdit.Apply(r.Context.Library, placed, r.ReadInt(), description);
    }
}

internal sealed class EntitySetCodec : EditCodec<EntitySetEdit>
{
    public override string Tag => "entity-set";

    protected override void Write(EntitySetEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Interior);
        w.Write(edit.IsCreation);
        w.Write(edit.Index);
        w.Write(edit.NewName);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var interior = r.ReadEntity();
        bool create = r.ReadBool();
        int index = r.ReadInt();
        uint name = r.ReadUInt();
        return create
            ? EntitySetEdit.Create(r.Context.Library, interior, name, description)
            : EntitySetEdit.Rename(r.Context.Library, interior, index, name, description);
    }
}

internal sealed class DeleteCodec : EditCodec<DeleteEdit>
{
    public override string Tag => "delete";

    // The whole plan is sent (not recomputed): the other computer deletes exactly the same objects.
    protected override void Write(DeleteEdit edit, EditWriter w)
    {
        var plan = edit.Plan;
        w.WriteEntities(plan.Targets);
        w.WriteEntities(plan.Entities);
        w.WriteEntities(plan.Children);
        w.WriteEntities(plan.Parents);
        w.WriteEntities(plan.Copies);
        w.WriteEntities(plan.KeptParents);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var plan = new DeletionPlan
        {
            Targets = r.ReadEntities(),
            Entities = r.ReadEntities(),
            Children = r.ReadEntities(),
            Parents = r.ReadEntities(),
            Copies = r.ReadEntities(),
            KeptParents = r.ReadEntities(),
        };
        return DeleteEdit.Apply(r.Context.Library, plan, description);
    }
}

internal sealed class MergeCodec : EditCodec<MergeEdit>
{
    public override string Tag => "merge";

    protected override void Write(MergeEdit edit, EditWriter w)
    {
        w.WriteList(edit.Moves, m =>
        {
            w.WriteEntity(m.Entity);
            w.Write(m.After);
        });
        w.WriteEntities(edit.Deleted);
        w.Write(edit.Added.Count > 0);
        if (edit.Added.Count > 0)
        {
            w.WriteMap(edit.Added[0].Map ?? throw new InvalidOperationException("Added object without a map."));
            w.WriteList(edit.Added, w.WriteNewEntity);
        }
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var plan = new MergePlan();
        plan.Moves.AddRange(r.ReadList(() => (r.ReadEntity(), r.ReadPlacement())));
        plan.Deletions.AddRange(r.ReadEntities());
        MapData? addTo = null;
        List<MapEntity> added = [];
        if (r.ReadBool())
        {
            addTo = r.ReadMap();
            added = r.ReadList(r.ReadNewEntity);
        }
        return MergeEdit.Apply(r.Context.Library, plan, addTo, added, description);
    }
}

internal sealed class CollisionCodec : EditCodec<CollisionEdit>
{
    public override string Tag => "collision";

    protected override void Write(CollisionEdit edit, EditWriter w) =>
        w.WriteList(edit.After, a =>
        {
            w.Write(a.Polygon.Path);
            w.Write(a.Polygon.Polygon);
            w.Write(a.Removed);
            w.Write(a.Material.HasValue);
            w.Write(a.Material ?? 0);
        });

    public override IEdit Apply(EditReader r, string description)
    {
        var after = r.ReadList(() =>
        {
            var polygon = new CollisionPolygon(r.ReadString(), r.ReadInt());
            bool removed = r.ReadBool();
            bool hasMaterial = r.ReadBool();
            byte material = r.ReadByte();
            return (polygon, removed, hasMaterial ? material : (byte?)null);
        });
        var edits = r.Context.Collisions ?? throw new InvalidOperationException("No collision edits here.");
        return CollisionEdit.ToStates(edits, description, after);
    }
}

internal sealed class PathNodeMoveCodec : EditCodec<PathNodeMoveEdit>
{
    public override string Tag => "path-node-move";

    protected override void Write(PathNodeMoveEdit edit, EditWriter w)
    {
        w.Write(edit.Node.Id.AreaId);
        w.Write(edit.Node.Id.NodeId);
        w.Write(edit.After);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var id = new PathNodeId(r.ReadUShort(), r.ReadUShort());
        var after = r.ReadVector3();
        var network = r.Context.Paths ?? throw new InvalidOperationException("No road network here.");
        var node = network.GetNode(id) ?? throw new InvalidDataException($"Path node {id.AreaId}:{id.NodeId} not found here.");
        var before = node.Position;
        network.MoveNode(node, after);
        return new PathNodeMoveEdit(network, node, before, after, description);
    }
}

internal sealed class RoomCodec : EditCodec<RoomEdit>
{
    public override string Tag => "room";

    protected override void Write(RoomEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Interior);
        w.Write(edit.Index);
        w.Write(edit.After);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var interior = r.ReadEntity();
        int index = r.ReadInt();
        return RoomEdit.Apply(r.Context.Library, interior, index, r.ReadRoom(), description);
    }
}

internal sealed class PortalCodec : EditCodec<PortalEdit>
{
    public override string Tag => "portal";

    protected override void Write(PortalEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Interior);
        w.Write(edit.Index);
        w.Write(edit.After);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var interior = r.ReadEntity();
        int index = r.ReadInt();
        return PortalEdit.Apply(r.Context.Library, interior, index, r.ReadPortal(), description);
    }
}

internal sealed class InteriorLayoutCodec : EditCodec<InteriorLayoutEdit>
{
    public override string Tag => "interior-layout";

    // Sent as the operation (add or remove a room or portal), not as the whole layout: the other computer renumbers
    // its objects with the same rules.
    protected override void Write(InteriorLayoutEdit edit, EditWriter w)
    {
        w.WriteEntity(edit.Interior);
        w.Write(edit.IsPortal);
        w.Write(edit.Added);
        w.Write(edit.Index);
        if (edit.Added && edit.IsPortal)
            w.Write(edit.After.Portals[edit.Index]);
        else if (edit.Added)
            w.Write(edit.After.Rooms[edit.Index]);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var library = r.Context.Library;
        var interior = r.ReadEntity();
        bool isPortal = r.ReadBool(), added = r.ReadBool();
        int index = r.ReadInt();
        uint hash = interior.ArchetypeHash;
        Func<int> change = (isPortal, added) switch
        {
            (true, true) => AddAt(r.ReadPortal(), p => library.AddPortal(hash, p)),
            (false, true) => AddAt(r.ReadRoom(), room => library.AddRoom(hash, room)),
            (true, false) => () => { library.RemovePortal(hash, index); return index; },
            (false, false) => () => { library.RemoveRoom(hash, index); return index; },
        };
        return InteriorLayoutEdit.Apply(library, interior, change, description, isPortal, added);

        Func<int> AddAt<T>(T item, Func<T, int> add) => () =>
        {
            int at = add(item);
            return at == index ? at : throw new InvalidDataException("Interior rooms differ here.");
        };
    }
}
