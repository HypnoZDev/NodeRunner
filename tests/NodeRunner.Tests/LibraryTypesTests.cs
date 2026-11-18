using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;

namespace NodeRunner.Tests;

public class LibraryTypesTests
{
    [Fact]
    public void A_library_prop_only_in_an_inactive_entity_set_is_shared_too()
    {
        var interior = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, IsInterior = true };
        var inSet = new MapEntity { ArchetypeHash = 0xABCD, Position = Vector3.Zero };
        interior.EntitySets = [new PlacedEntitySet { NameHash = 7, Interior = interior, Entities = [inSet], IsActive = false }];
        var map = new MapData { NameHash = 1, Entities = [interior, new MapEntity { ArchetypeHash = 0x1111, Position = Vector3.Zero }] };

        var used = LibraryFileSaver.PlacedTypes([map], new HashSet<uint> { 0xABCD, 0x2222 });

        Assert.Equal([0xABCDu], used);
    }
}
