using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class OccluderCheckTests
{
    // A 20 x 20 x 30 m building centred on the origin, standing on the ground.
    private static readonly PlacedVolume Building = new(Matrix4x4.Identity, new Vector3(-10, -10, 0), new Vector3(10, 10, 30));

    private static List<OccluderIssueKind> Check(OccluderBox box, IReadOnlyList<PlacedVolume>? interiors = null,
        IReadOnlyList<Vector3>? paths = null, IReadOnlyList<OccluderBox>? others = null) =>
        OccluderCheck.Run([box], others ?? [], [Building], interiors ?? [], paths ?? []).Select(i => i.Kind).ToList();

    [Fact]
    public void Box_inside_the_building_is_fine()
    {
        Assert.Empty(Check(new OccluderBox(0, new Vector3(0, 0, 14), new Vector3(18, 18, 26), 0.4f)));
    }

    [Fact]
    public void Box_out_in_the_street_or_across_a_road_is_reported()
    {
        var street = new OccluderBox(0, new Vector3(25, 0, 10), new Vector3(10, 10, 20), 0f);
        Assert.Contains(OccluderIssueKind.OutsideBuildings, Check(street));
        Assert.Contains(OccluderIssueKind.AcrossPath, Check(street, paths: [new Vector3(25, 2, 0.5f)]));
        Assert.DoesNotContain(OccluderIssueKind.AcrossPath, Check(street, paths: [new Vector3(40, 2, 0.5f)]));
    }

    [Fact]
    public void Box_over_an_interior_is_reported()
    {
        var interior = new PlacedVolume(Matrix4x4.CreateTranslation(0, 0, 5), new Vector3(-4, -4, -4), new Vector3(4, 4, 4), 1234);
        var issues = OccluderCheck.Run([new OccluderBox(0, new Vector3(0, 0, 14), new Vector3(18, 18, 26), 0f)], [], [Building], [interior], []);
        Assert.Contains(issues, i => i.Kind == OccluderIssueKind.InsideInterior && i.Other == 1234);
    }

    [Fact]
    public void Tiny_and_doubled_boxes_are_reported()
    {
        Assert.Contains(OccluderIssueKind.TooSmall, Check(new OccluderBox(0, new Vector3(0, 0, 5), new Vector3(1, 1, 1.5f), 0f)));
        var box = new OccluderBox(0, new Vector3(0, 0, 14), new Vector3(10, 10, 20), 0f);
        Assert.Contains(OccluderIssueKind.Duplicate, Check(box, others: [box with { Index = 7, Size = new Vector3(12, 12, 22) }]));
        Assert.DoesNotContain(OccluderIssueKind.Duplicate, Check(box, others: [box with { Index = 7, Center = new Vector3(8, 0, 14) }]));
    }
}
