using System.Numerics;
using NodeRunner.Data.Native.Gta4;

namespace NodeRunner.App.World;

// The objects of the GTA IV map as boxes, sorted into a grid for the boxes layer. Level of detail from the LOD links:
// an object used as a parent by parents is a SLOD, by other objects a LOD; an object with a parent is a detail
// (HD) one, without one a detail object with no LOD.
internal sealed class Gta4MapBoxes
{
    private const float CellSize = 100f;

    private readonly EntityBox[] _boxes;
    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];

    public Vector3 Min { get; }

    public Vector3 Max { get; }

    public int Count => _boxes.Length;

    public Gta4MapBoxes(Gta4Map map)
    {
        var first = new Dictionary<Gta4MapFile, int>();
        int total = 0;
        foreach (var file in map.Files)
        {
            first[file] = total;
            total += file.Instances.Length;
        }

        // Parent of each object (-1 if none), then which objects are parents and of what.
        var parents = new int[total];
        var isParent = new bool[total];
        int index = 0;
        foreach (var file in map.Files)
        {
            foreach (var instance in file.Instances)
            {
                int parent = -1;
                if (file.LodFile is { } lodFile && instance.LodParent >= 0 && instance.LodParent < lodFile.Instances.Length)
                {
                    parent = first[lodFile] + instance.LodParent;
                    isParent[parent] = true;
                }
                parents[index++] = parent;
            }
        }
        var parentOfParents = new bool[total];
        for (int i = 0; i < total; i++)
        {
            if (isParent[i] && parents[i] >= 0)
                parentOfParents[parents[i]] = true;
        }

        _boxes = new EntityBox[total];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        index = 0;
        foreach (var file in map.Files)
        {
            foreach (var instance in file.Instances)
            {
                var category = parentOfParents[index] ? EntityBoxCategory.Slod
                    : isParent[index] ? EntityBoxCategory.Lod
                    : parents[index] >= 0 ? EntityBoxCategory.Hd
                    : EntityBoxCategory.OrphanHd;
                var transform = Matrix4x4.CreateFromQuaternion(instance.Orientation) * Matrix4x4.CreateTranslation(instance.Position);
                _boxes[index] = map.Definitions.Find(instance.ModelHash) is { } model
                    ? new EntityBox(category, model.BoxMin, model.BoxMax, transform, false)
                    : map.Definitions.Interiors.GetValueOrDefault(instance.ModelHash) is { } interior
                    ? InteriorBox(interior, instance, map.Definitions)
                    : new EntityBox(category, default, default, transform, true);
                min = Vector3.Min(min, instance.Position);
                max = Vector3.Max(max, instance.Position);

                var cell = CellOf(new Vector2(instance.Position.X, instance.Position.Y));
                if (!_cells.TryGetValue(cell, out var list))
                    _cells[cell] = list = [];
                list.Add(index);
                index++;
            }
        }
        (Min, Max) = total > 0 ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    public IEnumerable<EntityBox> Around(Vector2 center, float radius)
    {
        var min = CellOf(center - new Vector2(radius));
        var max = CellOf(center + new Vector2(radius));
        float radiusSquared = radius * radius;
        for (int y = min.Y; y <= max.Y; y++)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                if (!_cells.TryGetValue((x, y), out var indices))
                    continue;
                foreach (int i in indices)
                {
                    var position = _boxes[i].Transform.Translation;
                    if (Vector2.DistanceSquared(new Vector2(position.X, position.Y), center) <= radiusSquared)
                        yield return _boxes[i];
                }
            }
        }
    }

    // The box of its rooms; an interior's placement is its real orientation (not inverted like the other objects).
    private static EntityBox InteriorBox(Gta4InteriorDef interior, in Gta4Instance instance, Gta4Definitions definitions)
    {
        var box = Gta4Scene.InteriorBox(interior, definitions);
        var transform = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(instance.Orientation)) * Matrix4x4.CreateTranslation(instance.Position);
        return new EntityBox(EntityBoxCategory.Interior, box.Min, box.Max, transform, false);
    }

    private static (int X, int Y) CellOf(Vector2 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));
}
