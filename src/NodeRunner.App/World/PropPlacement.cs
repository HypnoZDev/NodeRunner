using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Placing a prop with the mouse: its model follows the mouse over the 3D view (a "ghost"), each click adds one, the
// wheel or a key turns it, Escape ends.
internal sealed class PropPlacement : IDisposable
{
    private readonly PreviewModel _ghost = new();

    public uint ArchetypeHash { get; private set; }
    public bool IsActive => ArchetypeHash != 0;
    public string Name { get; private set; } = "";
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    // Turn when standing on a floor (front towards the camera when started) and the user's rotation added to it.
    public float FloorYaw { get; set; }
    public float Turn { get; set; }

    // Where it would go now (null: the mouse is not over a surface of the 3D view) and what is wrong there.
    public PlacementPose? Pose { get; set; }
    public PlacementProblem Problem { get; set; }

    // Objects added since the start, selected at the end.
    public List<MapEntity> Placed { get; } = [];

    public GpuModel? Model => _ghost.Model;

    public void Start(uint archetypeHash, string name, Archetype archetype, ViewerItem? model, WorldLoader world, float floorYaw)
    {
        ArchetypeHash = archetypeHash;
        Name = name;
        BoundsMin = archetype.BoundsMin;
        BoundsMax = archetype.BoundsMax;
        FloorYaw = floorYaw;
        Turn = 0f;
        Pose = null;
        Problem = PlacementProblem.None;
        Placed.Clear();
        if (model is not null)
            _ghost.Open(model, world);
        else
            _ghost.Close();
    }

    public void Stop()
    {
        ArchetypeHash = 0;
        Pose = null;
        _ghost.Close();
    }

    public void Update(ModelRenderer renderer)
    {
        if (IsActive)
            _ghost.Update(renderer);
    }

    public void Dispose() => _ghost.Dispose();
}
