using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.Tests;

public class InteriorCreationTests
{
    private const string Name = "test_int";

    private static (MapLibrary Library, MapData Map, InteriorCreateEdit Edit) Created()
    {
        var library = MapLibrary.Create();
        var map = library.CreateCustomMap("proj_props.ymap");
        var edit = InteriorCreateEdit.Apply(library, map, Name, new Vector3(100, 200, 30),
            InteriorCreation.FacingAway(Vector3.UnitX), "create");
        return (library, map, edit);
    }

    [Fact]
    public void Names_are_checked_and_suggested()
    {
        Assert.True(InteriorCreation.IsValidName("hopital_int2"));
        Assert.False(InteriorCreation.IsValidName("Hopital"));
        Assert.False(InteriorCreation.IsValidName("2int"));
        Assert.False(InteriorCreation.IsValidName("a-b"));
        Assert.Equal("my_res_int2", InteriorCreation.SuggestName("my_res", n => n != "my_res_int"));
        Assert.Equal("mlo_2pac_int", InteriorCreation.SuggestName("2pac", _ => true));
    }

    [Fact]
    public void Creates_an_interior_with_a_room_and_a_door()
    {
        var (library, map, edit) = Created();
        var archetype = library.GetArchetype(Data.Hashing.Joaat.Hash(Name))!;
        Assert.Equal(ArchetypeKind.Interior, archetype.Kind);
        Assert.Equal(@"noderunner_project.rpf\test_int.ytyp", library.GetArchetypeFile(archetype.NameHash));
        Assert.Equal(["limbo", "room1"], archetype.InteriorRooms.Select(r => r.Name));
        var door = archetype.InteriorPortals.Single();
        Assert.Equal((1, 0), (door.RoomFrom, door.RoomTo));
        Assert.Same(edit.Interior, map.Entities.Single());
        Assert.True(edit.Interior.IsInterior);
        // The door (side -Y) faces a camera looking along +X: the interior's +Y is the world's +X.
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, edit.Interior.Orientation), Vector3.UnitX) < 1e-5f);
        Assert.Throws<ArgumentException>(() => InteriorCreateEdit.Apply(library, map, Name, Vector3.Zero, Quaternion.Identity, "again"));

        edit.Undo();
        Assert.True(edit.Interior.IsDeleted);
        edit.Redo();
        Assert.False(edit.Interior.IsDeleted);
    }

    [Fact]
    public void Writes_a_new_ytyp_with_its_objects()
    {
        var (library, _, edit) = Created();
        var archetype = library.GetArchetype(edit.Interior.ArchetypeHash)!;
        var chair = new MapEntity
        {
            ArchetypeHash = 555, Position = new Vector3(1, 1, 0), StoredRotation = Quaternion.Identity, InteriorRoom = 1,
            LodDistance = 50, LodLevel = LodLevel.OrphanHd, Flags = 32,
        };
        var placed = library.AddToInterior(edit.Interior, chair);
        Assert.Equal(new Vector3(100, 200, 30) + Vector3.Transform(new Vector3(1, 1, 0), edit.Interior.Orientation), placed.Position);
        library.SetRoom(archetype.NameHash, 1, archetype.InteriorRooms[1] with { BoundsMax = new Vector3(5, 3, 3.5f) });

        var written = new YtypWriter().Build(YtypWriterTests.Sample(), Name, [archetype], null);
        var back = new YtypReader().Read(written.Content);
        Assert.Equal(Data.Hashing.Joaat.Hash(Name), back.NameHash);
        var read = back.Archetypes.Single();
        Assert.Equal(archetype.NameHash, read.NameHash);
        Assert.Equal(ArchetypeKind.Interior, read.Kind);
        Assert.Equal(ArchetypeAssetType.Assetless, read.AssetType);
        Assert.Equal(InteriorCreation.DefaultLodDistance, read.LodDistance);
        Assert.Equal(["limbo", "room1"], read.InteriorRooms.Select(r => r.Name));
        Assert.Equal(new Vector3(5, 3, 3.5f), read.InteriorRooms[1].BoundsMax);
        Assert.Equal(InteriorCreation.DefaultRoomFlags, read.InteriorRooms[1].Flags);
        Assert.True(read.InteriorPortals.Single().SameAs(archetype.InteriorPortals.Single() with { SourceIndex = read.InteriorPortals[0].SourceIndex }));
        var readChair = read.InteriorEntities.Single();
        Assert.Equal((555u, new Vector3(1, 1, 0), 1, 50f), (readChair.ArchetypeHash, readChair.Position, readChair.InteriorRoom, readChair.LodDistance));
    }

    [Fact]
    public void Places_the_interior_in_the_project_map()
    {
        var (library, map, edit) = Created();
        var template = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd));
        var instance = DeletionTests.Structure(YmapReader.InteriorInstanceStructure, YmapReader.InteriorInstanceSize);
        var extents = MapExtentsCalculator.Compute(map, library.GetArchetype);
        Assert.True(extents.EntitiesMax.Z >= 30 + InteriorCreation.RoomMax.Z - 0.01f);

        Assert.Throws<InvalidDataException>(() => new YmapWriter().Build(template, map, "proj_props", extents));
        var written = new YmapWriter().Build(template, map, "proj_props", extents, instance, library.GetArchetype);
        var back = new YmapReader().Read(written.Content);
        var read = back.Entities.Single();
        Assert.True(read.IsInterior);
        Assert.Equal(edit.Interior.ArchetypeHash, read.ArchetypeHash);
        Assert.Equal(edit.Interior.Position, read.Position);
        Assert.True(Quaternion.Dot(edit.Interior.Orientation, read.Orientation) > 0.9999f);
        Assert.Equal(LodLevel.OrphanHd, read.LodLevel);
        Assert.Equal(8u, back.ContentFlags & 8);

        var document = MetaDocument.Read(written.Content);
        var block = document.Blocks.Single(b => b.Structure == YmapReader.InteriorInstanceStructure);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(block.Data.AsSpan(152))); // Exits: the door.
    }

    [Fact]
    public void Is_shared_online()
    {
        var (library, _, edit) = Created();
        var codecs = EditCodecs.ForEditor();
        var message = codecs.Encode(edit, library);

        var other = MapLibrary.Create();
        var applied = Assert.IsType<InteriorCreateEdit>(codecs.Apply(message, new EditContext { Library = other }));
        Assert.Equal("proj_props.ymap", applied.Interior.Map!.FilePath);
        Assert.Equal(edit.Interior.Position, applied.Interior.Position);
        Assert.NotNull(other.GetArchetype(edit.Interior.ArchetypeHash));
    }

    [Fact]
    public void An_interior_is_copied_with_its_objects()
    {
        var (library, map, edit) = Created();
        var chair = new MapEntity { ArchetypeHash = 555, Position = new Vector3(1, 1, 0), StoredRotation = Quaternion.Identity, InteriorRoom = 1 };
        var kept = library.AddToInterior(edit.Interior, chair);
        var gone = library.AddToInterior(edit.Interior, new MapEntity { ArchetypeHash = 556, Position = Vector3.Zero, StoredRotation = Quaternion.Identity, InteriorRoom = 1 });
        library.SetDeleted([gone], true);

        var copied = ObjectCopy.Copy([edit.Interior], library.GetArchetype);
        var pasted = ObjectCopy.Paste(copied, new Vector3(500, 0, 10), library.GetArchetype);
        var add = AddEdit.Apply(library, map, pasted, "paste");
        var copy = add.Selection.Single();
        Assert.True(copy.IsInterior);
        Assert.Equal(edit.Interior.ArchetypeHash, copy.ArchetypeHash);
        // The bottom of its rooms on the ground, turned like the original.
        Assert.Equal(10 - InteriorCreation.RoomMin.Z, copy.Position.Z, 3);
        Assert.True(Quaternion.Dot(edit.Interior.Orientation, copy.Orientation) > 0.9999f);
        Assert.Equal(2, copy.InteriorEntities.Count);
        Assert.False(copy.InteriorEntities.Single(e => e.ArchetypeHash == 555).IsDeleted);
        Assert.True(copy.InteriorEntities.Single(e => e.ArchetypeHash == 556).IsDeleted);
        Assert.Equal(copy.Position + Vector3.Transform(new Vector3(1, 1, 0), copy.Orientation),
            copy.InteriorEntities.Single(e => e.ArchetypeHash == 555).Position);

        // Moving an object of the original moves it in the copy too.
        kept.SetPlacement(kept.Position + Vector3.UnitZ, kept.Orientation, kept.Scale);
        Assert.Equal(1f, copy.InteriorEntities.Single(e => e.ArchetypeHash == 555).Position.Z - copy.Position.Z, 3);

        var codecs = EditCodecs.ForEditor();
        var message = codecs.Encode(add, library);
        var other = MapLibrary.Create();
        codecs.Apply(codecs.Encode(edit, library), new EditContext { Library = other });
        var applied = Assert.IsType<AddEdit>(codecs.Apply(message, new EditContext { Library = other })).Selection.Single();
        Assert.True(applied.IsInterior);
        Assert.Equal(copy.Position, applied.Position);
        Assert.True(Quaternion.Dot(copy.Orientation, applied.Orientation) > 0.9999f);
    }

    [Fact]
    public void New_type_files_are_declared_in_the_manifest()
    {
        string manifest = FiveMResource.BuildManifest("Inside Rockstar", "1.0.0", ["a.ymap", "test_int.ytyp"], ["test_int.ytyp"]);
        Assert.Contains("data_file 'DLC_ITYP_REQUEST' 'stream/test_int.ytyp'", manifest);
        Assert.DoesNotContain("DLC_ITYP_REQUEST", FiveMResource.BuildManifest("Inside Rockstar", "1.0.0", ["a.ymap"]));
    }
}
