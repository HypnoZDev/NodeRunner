using System.Globalization;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.Tests;

public class OnlineEditTests
{
    private const uint InteriorHash = 0x500;
    private static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    internal sealed record World(MapLibrary Library, CollisionEdits Collisions, PathNetwork Paths, EditHistory History)
    {
        public EditContext Context => new() { Library = Library, Collisions = Collisions, Paths = Paths };
        public MapData Map => Library.Maps[0];
        public MapEntity Instance(int i) => Map.Entities[4 + i];
    }

    private static MapEntity Def(uint hash, Vector3 position, int room) =>
        new() { ArchetypeHash = hash, Position = position, InteriorRoom = room, InteriorPortal = -1 };

    // A LOD with two buildings, a bench, two instances of an interior (objects, an entity set, two rooms, a portal),
    // two road nodes.
    internal static World Build(EditHistory? history = null)
    {
        var interior = new Archetype
        {
            NameHash = InteriorHash,
            Kind = ArchetypeKind.Interior,
            InteriorEntities = [Def(1, new(1, 0, 0), 1), Def(2, new(2, 0, 0), 1), Def(3, new(0, 3, 0), 0)],
            InteriorEntitySets = [new InteriorEntitySet(0xAA, [Def(4, new(0, 0, 1), 1)])],
            InteriorRooms =
            [
                new InteriorRoom(new Vector3(-100), new Vector3(100)) { Name = "limbo", SourceIndex = 0 },
                new InteriorRoom(new Vector3(-5, -5, 0), new Vector3(5, 5, 3)) { Name = "room", SourceIndex = 1 },
            ],
            InteriorPortals = [new InteriorPortal(1, 0, 0) { Corners = [new(5, 0, 0), new(5, 1, 0), new(5, 1, 2), new(5, 0, 2)], SourceIndex = 0 }],
        };
        var map = new MapData
        {
            NameHash = 0x10,
            Entities =
            [
                new MapEntity { ArchetypeHash = 0x20, Position = new(0, 0, 0), LodLevel = LodLevel.Lod, ChildCount = 2 },
                new MapEntity { ArchetypeHash = 0x21, Position = new(10, 0, 0), LodLevel = LodLevel.Hd, ParentIndex = 0 },
                new MapEntity { ArchetypeHash = 0x22, Position = new(20, 0, 0), LodLevel = LodLevel.Hd, ParentIndex = 0 },
                new MapEntity { ArchetypeHash = 0x23, Position = new(30, 5, 1), LodLevel = LodLevel.OrphanHd },
                new MapEntity { ArchetypeHash = InteriorHash, Position = new(100, 0, 0), StoredRotation = Quarter, IsInterior = true },
                new MapEntity { ArchetypeHash = InteriorHash, Position = new(0, 500, 10), IsInterior = true },
            ],
            CarGenerators = [new CarGenerator { Position = new(40, 0, 0), Orientation = new(0, 5), Width = 2.5f, SourceIndex = 0 }],
        };
        var library = MapLibrary.Create([interior], (map, "test.ymap"));
        var paths = new PathNetwork();
        paths.SetArea(new PathNodeArea
        {
            AreaId = 5,
            Nodes = [new PathNode { Id = new(5, 0), Position = new(1, 2, 3) }, new PathNode { Id = new(5, 1), Position = new(4, 5, 6) }],
            Links = [],
        });
        return new World(library, new CollisionEdits(), paths, history ?? new EditHistory());
    }

    // Everything an edit can change, in a fixed order, numbers written exactly.
    internal static string Snapshot(World w)
    {
        var text = new StringBuilder();
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";
        void Entity(MapEntity e) => text.Append(CultureInfo.InvariantCulture,
            $"  {e.ArchetypeHash:X} {V(e.Position)} {F(e.StoredRotation.X)},{F(e.StoredRotation.Y)},{F(e.StoredRotation.Z)},{F(e.StoredRotation.W)} {V(e.Scale)} del={e.IsDeleted} room={e.InteriorRoom} portal={e.InteriorPortal} guid={e.Guid}\n");

        foreach (var map in w.Library.Maps)
        {
            text.Append(CultureInfo.InvariantCulture, $"map {map.FilePath} {map.NameHash:X}\n");
            foreach (var e in map.Entities)
            {
                Entity(e);
                foreach (var inside in e.InteriorEntities)
                    Entity(inside);
                foreach (var set in e.EntitySets)
                {
                    text.Append(CultureInfo.InvariantCulture, $"  set {set.NameHash:X}\n");
                    foreach (var inside in set.Entities)
                        Entity(inside);
                }
            }
        }
        foreach (var spot in w.Map.CarGenerators)
            text.Append(CultureInfo.InvariantCulture, $"spot {V(spot.Position)} {F(spot.Orientation.X)},{F(spot.Orientation.Y)} {F(spot.Width)} {spot.CarModel:X} {spot.Flags:X} {spot.Color1} {spot.Color4} {spot.PopGroup:X} {spot.Livery}\n");
        var archetype = w.Library.GetArchetype(InteriorHash)!;
        foreach (var room in archetype.InteriorRooms)
            text.Append(CultureInfo.InvariantCulture, $"room {room.Name} {V(room.BoundsMin)} {V(room.BoundsMax)} {room.Flags}\n");
        foreach (var portal in archetype.InteriorPortals)
            text.Append(CultureInfo.InvariantCulture, $"portal {portal.RoomFrom}-{portal.RoomTo} {string.Join(" ", portal.Corners.Select(V))} {portal.Opacity}\n");
        text.Append(CultureInfo.InvariantCulture, $"deleted {w.Library.DeletedCount}\n");
        text.Append(w.Collisions.ToJson()).Append('\n');
        foreach (var node in w.Paths.GetArea(5)!.Nodes)
            text.Append(CultureInfo.InvariantCulture, $"node {V(node.Position)}\n");
        return text.ToString();
    }

    [Fact]
    public void Every_kind_of_edit_is_replayed_identically_on_another_computer()
    {
        var codecs = EditCodecs.ForEditor();
        var a = Build();
        var b = Build();
        Assert.Equal(Snapshot(a), Snapshot(b));

        void Share(IEdit edit)
        {
            a.History.Add(edit);
            var message = codecs.Encode(edit, a.Library);
            var replayed = codecs.Apply(message, b.Context);
            Assert.Equal(edit.GetType(), replayed.GetType());
            Assert.Equal(edit.Description, replayed.Description);
            b.History.Add(replayed);
            Assert.Equal(Snapshot(a), Snapshot(b));
        }

        // Objects of the map: one moved, a group moved, objects added to the project's map then moved.
        var bench = a.Map.Entities[3];
        var before = Placement.Of(bench);
        var after = new Placement(new(31, 6, 2), Quarter, new(2, 2, 1));
        after.ApplyTo(bench);
        // Its map collision moved with it.
        var follow = new CollisionMoveEdit(a.Collisions, "Déplacer le banc", [("z.ybn", CollisionMove.Of([4, 5], Matrix4x4.CreateTranslation(1, 0, 0)))]);
        follow.Redo();
        Share(new PlacementEdit(bench, before, after, "Déplacer le banc") { Collision = follow });

        var group = new[] { a.Map.Entities[1], a.Map.Entities[2] };
        var groupBefore = group.Select(Placement.Of).ToList();
        var groupAfter = groupBefore.Select(p => GroupTransform.Move(p, new Vector3(0, 0, 3))).ToList();
        for (int i = 0; i < 2; i++)
            groupAfter[i].ApplyTo(group[i]);
        Share(new GroupPlacementEdit(group, groupBefore, groupAfter, "Déplacer 2 objets"));

        var props = a.Library.CreateCustomMap("projet_props.ymap");
        Share(AddEdit.Apply(a.Library, props,
            [Adding.NewEntity(0x30, null, new(1, 1, 1), Quarter), Adding.NewEntity(0x31, null, new(2, 2, 2), Quaternion.Identity)], "Ajouter"));
        var added = props.Entities[1];
        var addedBefore = Placement.Of(added);
        var addedAfter = addedBefore with { Position = new(9, 9, 9) };
        addedAfter.ApplyTo(added);
        Share(new PlacementEdit(added, addedBefore, addedAfter, "Déplacer l'ajout"));

        // Both buildings deleted: their LOD goes with them.
        Share(DeleteEdit.Apply(a.Library, DeletionPlan.For([a.Map.Entities[1], a.Map.Entities[2]]), "Supprimer"));

        // Interior: object added to every instance, one moved, entity sets made, renamed, filled, an object deleted.
        var instance = a.Instance(0);
        var placed = Adding.NewEntity(0x40, null, instance.Position + new Vector3(0, 1, 1), Quaternion.Identity);
        Share(InteriorAddEdit.Apply(a.Library, instance, [Adding.InteriorDefinition(placed, instance, 1)], "Ajouter dans l'intérieur"));

        var inside = a.Instance(1).InteriorEntities[0];
        var insideBefore = Placement.Of(inside);
        var insideAfter = insideBefore with { Position = inside.Position + new Vector3(0.5f, 0, 0) };
        insideAfter.ApplyTo(inside);
        Share(new PlacementEdit(inside, insideBefore, insideAfter, "Déplacer dans l'intérieur"));

        Share(EntitySetEdit.Create(a.Library, instance, 0xBB, "Créer un entity set"));
        Share(EntitySetEdit.Rename(a.Library, instance, 0, 0xCC, "Renommer"));
        Share(EntitySetMoveEdit.Apply(a.Library, [instance.InteriorEntities[1]], 1, "Ranger dans le set"));
        Share(EntitySetMoveEdit.Apply(a.Library, [instance.EntitySets[0].Entities[0]], -1, "Sortir du set"));
        Share(DeleteEdit.Apply(a.Library, DeletionPlan.For(a.Instance(1).InteriorEntities[2]), "Supprimer dans l'intérieur"));

        Share(EntityRoomEdit.Apply(a.Library, [instance.InteriorEntities[0]], [0], "Changer de salle"));

        // Rooms and portals.
        var room =a.Library.GetArchetype(InteriorHash)!.InteriorRooms[1];
        Share(RoomEdit.Apply(a.Library, instance, 1, room with { Name = "salon", BoundsMax = new(6, 6, 3), Flags = 0x40 }, "Salle"));
        var portal = a.Library.GetArchetype(InteriorHash)!.InteriorPortals[0];
        Share(PortalEdit.Apply(a.Library, instance, 0, portal with { Opacity = 50, Corners = [new(5, 0, 0), new(5, 2, 0), new(5, 2, 2), new(5, 0, 2)] }, "Portail"));
        Share(InteriorLayoutEdit.Apply(a.Library, instance,
            () => a.Library.AddRoom(InteriorHash, new InteriorRoom(new(-2, -2, 0), new(2, 2, 3)) { Name = "cuisine" }), "Ajouter une salle", isPortal: false, added: true));
        Share(InteriorLayoutEdit.Apply(a.Library, instance,
            () => a.Library.AddPortal(InteriorHash, new InteriorPortal(2, 1, 0) { Corners = [new(2, 0, 0), new(2, 1, 0), new(2, 1, 2), new(2, 0, 2)] }), "Ajouter un portail", isPortal: true, added: true));
        Share(InteriorLayoutEdit.Apply(a.Library, instance,
            () => { a.Library.RemovePortal(InteriorHash, 0); return 0; }, "Supprimer un portail", isPortal: true, added: false));
        Share(InteriorLayoutEdit.Apply(a.Library, instance,
            () => { a.Library.RemoveRoom(InteriorHash, 1); return 1; }, "Supprimer une salle", isPortal: false, added: false));

        // Changes of another project taken in, collision, road node.
        var plan = new MergePlan();
        plan.Moves.Add((a.Map.Entities[3], new Placement(new(40, 0, 0), Quaternion.Identity, Vector3.One)));
        plan.Deletions.Add(props.Entities[0]);
        Share(MergeEdit.Apply(a.Library, plan, props, [Adding.NewEntity(0x50, null, new(7, 7, 7), Quaternion.Identity)], "Reprendre"));

        Share(Apply(CollisionEdit.Remove(a.Collisions, [new("x.ybn", 3), new("x.ybn", 4)])));
        Share(Apply(CollisionEdit.SetMaterial(a.Collisions, [new("y.ybn", 1)], 7, "herbe")));
        var collisionMove = new CollisionMoveEdit(a.Collisions, "déplacer des surfaces de collision",
            [("y.ybn", CollisionMove.Of([1, 2], Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(1, 2, 3)))]);
        collisionMove.Redo();
        Share(collisionMove);

        var node = a.Paths.GetNode(new(5, 1))!;
        var nodeBefore = node.Position;
        a.Paths.MoveNode(node, new Vector3(8, 8, 8));
        Share(new PathNodeMoveEdit(a.Paths, node, nodeBefore, node.Position, "Déplacer un nœud"));

        // A parking spot moved, turned and given a model.
        var spotBefore = a.Map.CarGenerators[0];
        var spotAfter = spotBefore with { Position = new(41, 2, 0.5f), Orientation = new(-4, 0), Width = 3f, CarModel = 0xB779A091, Flags = 0x1, Color1 = 5, Color4 = 7, PopGroup = 0x99, Livery = 2 };
        a.Map.SetCarGenerator(0, spotAfter);
        Share(new CarGeneratorEdit(a.Map, 0, spotBefore, spotAfter, "Modifier la place"));

        // Undone and redone the same way on both computers.
        while (a.History.CanUndo)
        {
            a.History.Undo();
            b.History.Undo();
            Assert.Equal(Snapshot(a), Snapshot(b));
        }
        while (a.History.CanRedo)
        {
            a.History.Redo();
            b.History.Redo();
            Assert.Equal(Snapshot(a), Snapshot(b));
        }
    }

    private static CollisionEdit Apply(CollisionEdit edit)
    {
        edit.Redo();
        return edit;
    }

    [Fact]
    public void Every_editor_edit_has_a_codec()
    {
        // A new kind of edit needs its codec (EditCodecs.ForEditor) to be shared in an online session.
        Assert.Empty(EditCodecs.ForEditor().Missing(typeof(IEdit).Assembly).Select(t => t.Name));
    }

    // Same check on the source of every project (the application's own edits, e.g. occluders, are not reachable by
    // reflection from here): each class implementing IEdit has an EditCodec<ThatClass>, or is marked [LocalEdit].
    [Fact]
    public void Every_edit_of_the_application_has_a_codec()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NodeRunner.sln")))
            dir = dir.Parent;
        var sources = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        string all = string.Join("\n", sources);
        var edits = sources.SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s,
                @"(\[LocalEdit[^\]]*\]\s*)?(?:public|internal)\s+sealed\s+class\s+(\w+)[^{;]*?:[^{;]*IEdit"))
            .Where(m => !m.Groups[1].Success)
            .Select(m => m.Groups[2].Value)
            .ToList();
        Assert.Contains("OccluderBoxEdit", edits);
        Assert.Contains("PlacementEdit", edits);
        Assert.DoesNotContain(edits, e => !all.Contains($"Codec<{e}>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_message_from_a_different_map_is_refused()
    {
        var codecs = EditCodecs.ForEditor();
        var a = Build();
        var bench = a.Map.Entities[3];
        var before = Placement.Of(bench);
        var message = codecs.Encode(new PlacementEdit(bench, before, before with { Position = Vector3.One }, "Déplacer"), a.Library);

        var other = new MapLibrary();
        Assert.Throws<InvalidDataException>(() => codecs.Apply(message, new EditContext { Library = other }));
        Assert.Throws<InvalidDataException>(() => codecs.Apply([1, (byte)'?', 0], new EditContext { Library = other }));
    }

    [Fact]
    public void Signature_depends_on_the_kinds_of_edits()
    {
        var editor = EditCodecs.ForEditor();
        Assert.Equal(editor.Signature, EditCodecs.ForEditor().Signature);
        Assert.Equal(16, editor.Signature.Length);
    }
}
