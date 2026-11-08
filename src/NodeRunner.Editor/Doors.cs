using System.Globalization;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Doors are separate props placed next to (or inside) a building. Found by their name: the files only store hashes,
// so a door whose name is unknown is not found.
public static class Doors
{
    private const float Margin = 1f;

    public static bool IsDoor(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        string lower = name.ToLowerInvariant().Replace("outdoor", "").Replace("indoor", "");
        return lower.Contains("door") || lower.Contains("garaged");
    }

    // The doors of the building or interior of `entity`: all doors of the interior when it is one (or an object inside
    // one), otherwise the doors whose position is in the building's box (interior doors included).
    public static List<MapEntity> Of(MapEntity entity, MapLibrary library, Func<uint, string?> names)
    {
        bool Door(MapEntity e) => !e.IsDeleted && e.LodLevel is LodLevel.Hd or LodLevel.OrphanHd && IsDoor(names(e.ArchetypeHash));

        if ((entity.IsInterior ? entity : entity.Interior) is { } interior)
            return Shown(interior).Where(Door).ToList();

        if (library.GetArchetype(entity.ArchetypeHash) is not { } archetype)
            return Door(entity) ? [entity] : [];
        var (min, max) = WorldBox(archetype.BoundsMin, archetype.BoundsMax, entity.Transform);
        min -= new Vector3(Margin);
        max += new Vector3(Margin);
        bool Inside(MapEntity e) => Vector3.Clamp(e.Position, min, max) == e.Position;

        var center = new Vector2((min.X + max.X) / 2f, (min.Y + max.Y) / 2f);
        float radius = Vector2.Distance(center, new Vector2(max.X, max.Y));
        var found = new List<MapEntity>();
        foreach (var near in library.EntitiesAround(center, radius))
        {
            if (Door(near) && Inside(near))
                found.Add(near);
            if (near.IsInterior)
                found.AddRange(Shown(near).Where(e => Door(e) && Inside(e)));
        }
        return found.Distinct().ToList();
    }

    private static IEnumerable<MapEntity> Shown(MapEntity interior) =>
        interior.InteriorEntities.Where(e => e.EntitySet is null || e.EntitySet.IsActive);

    private static (Vector3 Min, Vector3 Max) WorldBox(Vector3 boundsMin, Vector3 boundsMax, Matrix4x4 transform)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? boundsMin.X : boundsMax.X, (i & 2) == 0 ? boundsMin.Y : boundsMax.Y,
                (i & 4) == 0 ? boundsMin.Z : boundsMax.Z);
            var world = Vector3.Transform(corner, transform);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
        return (min, max);
    }
}

public readonly record struct HiddenModel(uint Hash, string? Name, Vector3 Position);

// FiveM client script that hides placed objects without shipping a modified YMAP (no conflict with other resources).
// Also works for doors created by the game's scripts, which are in no map file.
public static class ModelHideScript
{
    public const float Radius = 0.5f;

    public static string Hide(IReadOnlyList<HiddenModel> models, CopyFormat format)
    {
        bool cs = format == CopyFormat.CSharp;
        var text = new StringBuilder();
        text.Append(cs ? "// client\n" : "-- client.lua\n");
        foreach (var model in models)
        {
            var p = model.Position;
            string hash = cs
                ? model.Name is { } n ? $"(uint)API.GetHashKey(\"{n}\")" : $"0x{model.Hash:X8}u"
                : model.Name is { } m ? $"`{m}`" : $"0x{model.Hash:X8}";
            text.Append(cs
                ? Invariant($"API.CreateModelHide({p.X:0.00}f, {p.Y:0.00}f, {p.Z:0.00}f, {Radius:0.0}f, {hash}, true);\n")
                : Invariant($"CreateModelHide({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}, {Radius:0.0}, {hash}, true)\n"));
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
