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

public readonly record struct DoorItem(uint Hash, string? Name, Vector3 Position, float Heading, DoorKind Kind = DoorKind.Hinged);

public enum DoorKind
{
    Hinged,
    Sliding,
    Garage,
    Fixed,
}

// How a door opens, from its model's box: swinging doors have their origin on the hinge (763 of the 937 opening door
// types of the game), sliding and garage doors in their middle (measured).
public readonly record struct DoorShape(DoorKind Kind, float Width, float Height, float Bottom, float Side)
{
    private const int ArcSegments = 24;

    public static DoorShape Of(Archetype archetype, string? name)
    {
        var min = archetype.BoundsMin;
        var max = archetype.BoundsMax;
        float width = max.X - min.X;
        float height = max.Z - min.Z;
        if ((archetype.Flags & 0x20000) == 0)
            return new DoorShape(DoorKind.Fixed, width, height, min.Z, 1f);
        bool centered = width > 0 && MathF.Abs(min.X + max.X) < 0.15f * width && min.X < -0.2f * width;
        if (centered)
        {
            string lower = name?.ToLowerInvariant() ?? "";
            bool garage = lower.Contains("garage") || lower.Contains("roller") || lower.Contains("shutter");
            return new DoorShape(garage ? DoorKind.Garage : DoorKind.Sliding, width, height, min.Z, 1f);
        }
        // The leaf goes from the hinge (origin) to the farther side.
        bool positive = MathF.Abs(max.X) >= MathF.Abs(min.X);
        return new DoorShape(DoorKind.Hinged, positive ? max.X : -min.X, height, min.Z, positive ? 1f : -1f);
    }

    // Opening angles of a swinging door in degrees, from its doortuning: the limit (90 when the tuning gives none) on
    // both sides, or on one side only.
    public static (float Min, float Max) Swing(DoorTuning? tuning)
    {
        float limit = tuning is { RotationLimitAngle: > 0 } t ? t.RotationLimitAngle : 90f;
        return tuning?.StdDoorRotDir switch
        {
            DoorRotation.Negative => (-limit, 0),
            DoorRotation.Positive => (0, limit),
            _ => (-limit, limit),
        };
    }

    // Lines showing how the door opens, in the model's space: swinging doors = the closed leaf and the circle swept by
    // its edge between the angles given (degrees); sliding = arrows along the door; garage = an arrow going up.
    public List<(Vector3 From, Vector3 To)> Lines(float minAngle = -90f, float maxAngle = 90f)
    {
        var lines = new List<(Vector3, Vector3)>();
        float z = Bottom + 0.05f;
        switch (Kind)
        {
            case DoorKind.Hinged:
            {
                float reach = Side * Width;
                Vector3 At(float angle) => new(reach * MathF.Cos(angle), reach * MathF.Sin(angle), z);
                float from = minAngle * MathF.PI / 180f, span = (maxAngle - minAngle) * MathF.PI / 180f;
                for (int i = 0; i < ArcSegments; i++)
                    lines.Add((At(from + span * i / ArcSegments), At(from + span * (i + 1) / ArcSegments)));
                var hinge = new Vector3(0, 0, z);
                lines.Add((hinge, At(from)));
                lines.Add((hinge, At(from + span)));
                lines.Add((hinge, At(0)));
                lines.Add((hinge, hinge + new Vector3(0, 0, Height)));
                break;
            }
            case DoorKind.Sliding:
            {
                float half = Width / 2;
                foreach (float sign in new[] { -1f, 1f })
                {
                    var tip = new Vector3(sign * Width, 0, z);
                    lines.Add((new Vector3(sign * half * 0.2f, 0, z), tip));
                    lines.Add((tip, tip - new Vector3(sign * 0.25f, 0.2f, 0)));
                    lines.Add((tip, tip - new Vector3(sign * 0.25f, -0.2f, 0)));
                }
                break;
            }
            case DoorKind.Garage:
            {
                var from = new Vector3(0, 0, z);
                var tip = new Vector3(0, 0, Bottom + Height * 1.2f);
                lines.Add((from, tip));
                lines.Add((tip, tip - new Vector3(0.3f, 0, 0.3f)));
                lines.Add((tip, tip - new Vector3(-0.3f, 0, 0.3f)));
                break;
            }
        }
        return lines;
    }
}

public enum DoorLockFormat
{
    Natives,
    QbDoorlock,
}

// Door lock configuration for FiveM: door lock scripts match doors by model and exact position.
public static class DoorLockScript
{
    // Two doors this close (and at the same height) form a double door.
    private const float PairDistance = 2.8f;

    public static List<IReadOnlyList<DoorItem>> Group(IReadOnlyList<DoorItem> doors)
    {
        var left = doors.ToList();
        var groups = new List<IReadOnlyList<DoorItem>>();
        while (left.Count > 0)
        {
            var door = left[0];
            left.RemoveAt(0);
            int best = -1;
            float bestDistance = PairDistance;
            for (int i = 0; i < left.Count; i++)
            {
                float d = Vector2.Distance(new Vector2(door.Position.X, door.Position.Y), new Vector2(left[i].Position.X, left[i].Position.Y));
                if (d < bestDistance && MathF.Abs(door.Position.Z - left[i].Position.Z) < 0.5f)
                    (best, bestDistance) = (i, d);
            }
            if (best < 0)
            {
                groups.Add([door]);
                continue;
            }
            groups.Add([door, left[best]]);
            left.RemoveAt(best);
        }
        return groups;
    }

    public static string Write(IReadOnlyList<DoorItem> doors, DoorLockFormat format, bool csharp) => format switch
    {
        DoorLockFormat.QbDoorlock => QbDoorlock(Group(doors)),
        _ => Natives(Group(doors), csharp),
    };

    private static string Model(DoorItem door, bool csharp) => csharp
        ? door.Name is { } n ? $"(uint)API.GetHashKey(\"{n}\")" : $"0x{door.Hash:X8}u"
        : door.Name is { } m ? $"`{m}`" : $"0x{door.Hash:X8}";

    // Door system ids are global to the game: built from the position so they do not clash with other resources.
    private static string Id(DoorItem door) =>
        Invariant($"nrs_door_{MathF.Round(door.Position.X):0}_{MathF.Round(door.Position.Y):0}_{MathF.Round(door.Position.Z):0}");

    private static string Natives(List<IReadOnlyList<DoorItem>> groups, bool cs)
    {
        var text = new StringBuilder();
        text.Append(cs ? "// client : 1 = verrouillée, 0 = ouverte\n" : "-- client.lua : 1 = verrouillée, 0 = ouverte\n");
        foreach (var group in groups)
        {
            if (group.Count > 1)
                text.Append(cs ? "// porte double\n" : "-- porte double\n");
            foreach (var door in group)
            {
                var p = door.Position;
                string id = cs ? $"(int)API.GetHashKey(\"{Id(door)}\")" : $"`{Id(door)}`";
                text.Append(cs
                    ? Invariant($"API.AddDoorToSystem((uint){id}, {Model(door, true)}, {p.X:0.000}f, {p.Y:0.000}f, {p.Z:0.000}f, false, false, false);\n")
                      + $"API.DoorSystemSetDoorState((uint){id}, 1, false, false);\n"
                    : Invariant($"AddDoorToSystem({id}, {Model(door, false)}, {p.X:0.000}, {p.Y:0.000}, {p.Z:0.000}, false, false, false)\n")
                      + $"DoorSystemSetDoorState({id}, 1, false, false)\n");
            }
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string QbDoorlock(List<IReadOnlyList<DoorItem>> groups)
    {
        var text = new StringBuilder();
        foreach (var group in groups)
        {
            string id = Id(group[0]).Replace("nrs_door_", "door_");
            text.Append($"Config.DoorList['{id}'] = {{\n");
            if (group.Count > 1)
            {
                text.Append(group.All(d => d.Kind == DoorKind.Sliding) ? "    doorType = 'doublesliding',\n    doors = {\n" : "    doorType = 'double',\n    doors = {\n");
                foreach (var door in group)
                    text.Append($"        {{ {Object(door)} }},\n");
                text.Append("    },\n");
            }
            else
            {
                text.Append($"    doorType = '{QbType(group[0].Kind)}',\n");
                foreach (string field in Fields(group[0]))
                    text.Append($"    {field},\n");
            }
            text.Append("    locked = true,\n    distance = 2.0,\n    authorizedJobs = { ['police'] = 0 },\n}\n");
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string QbType(DoorKind kind) => kind switch
    {
        DoorKind.Sliding => "sliding",
        DoorKind.Garage => "garage",
        _ => "door",
    };

    private static string Object(DoorItem door) => string.Join(", ", Fields(door));

    private static string[] Fields(DoorItem door)
    {
        var p = door.Position;
        string model = door.Name is { } n ? $"objName = '{n}'" : Invariant($"objHash = {unchecked((int)door.Hash)}");
        return [model, Invariant($"objYaw = {door.Heading:0.0}"), Invariant($"objCoords = vec3({p.X:0.000}, {p.Y:0.000}, {p.Z:0.000})")];
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
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
