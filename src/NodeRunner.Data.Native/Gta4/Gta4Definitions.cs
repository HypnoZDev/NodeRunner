using System.Globalization;
using System.Numerics;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Gta4;

// A model declared in a ".ide" file. The box and sphere are in model space; DrawableDictionary names the ".wdd" file
// holding the model ("nj01_slod21_grp"), null when the model has its own ".wdr" or ".wft". TimeFlags: hours shown
// (bit per hour) for the timed sections, 0 otherwise.
public sealed record Gta4ModelDef(string Name, uint Hash, string TextureDictionary, string? AnimationDictionary,
    float DrawDistance, uint Flags, Vector3 BoxMin, Vector3 BoxMax, Vector3 SphereCenter, float SphereRadius,
    string? DrawableDictionary, bool IsTimed, uint TimeFlags = 0);

// An object of an interior, placed relative to it (rotation as stored, inverted like the placement files). Lod: 0 =
// detailed, 1 and 2 = simplified versions of the building, shown from further away.
public readonly record struct Gta4InteriorObject(uint ModelHash, Vector3 Position, Quaternion Rotation, int Lod, uint Flags);

// A room of an interior: its box in the interior's space and the indices of its objects. Room 0 is "limbo" (outside).
public sealed record Gta4InteriorRoom(string Name, Vector3 BoxMin, Vector3 BoxMax, uint TimecycleHash, int[] Objects);

// An opening between two rooms (four corners in the interior's space) and the objects in it (doors...).
public sealed record Gta4InteriorPortal(int RoomFrom, int RoomTo, Vector3[] Corners, int[] Objects);

// An interior ("mlo" section of a ".ide" file), placed in the ".wpl" files like a model. Many are whole buildings:
// their walls, inside and outside, are objects of the interior.
public sealed record Gta4InteriorDef(string Name, uint Hash, Gta4InteriorObject[] Objects, Gta4InteriorRoom[] Rooms,
    Gta4InteriorPortal[] Portals);

// Model definitions of GTA IV (".ide" text files): "objs", "tobj" (objs + hours), "anim" (objs with an animation
// dictionary after the texture dictionary), "tanm" (anim + hours), the texture dictionary parents ("txdp") and the
// interiors ("mlo").
public sealed class Gta4Definitions
{
    private readonly Dictionary<uint, Gta4ModelDef> _models = [];
    private readonly Dictionary<uint, Gta4InteriorDef> _interiors = [];
    private readonly Dictionary<string, string> _textureParents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<uint, Gta4ModelDef> Models => _models;

    public IReadOnlyDictionary<uint, Gta4InteriorDef> Interiors => _interiors;

    // Texture dictionary -> its parent dictionary, searched when a texture is missing.
    public IReadOnlyDictionary<string, string> TextureParents => _textureParents;

    public Gta4ModelDef? Find(uint hash) => _models.GetValueOrDefault(hash);

    // Reads one ".ide" file; returns the number of lines that could not be read. A later file replaces earlier models.
    public int Read(string text)
    {
        int bad = 0;
        string section = "";
        var interior = new InteriorReader();
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].TrimEnd();
            if (line.IsEmpty)
                continue;
            if (section == "mlo" && !line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (!interior.Read(line.ToString()))
                    bad++;
                if (interior.TakeFinished() is { } finished)
                    _interiors[finished.Hash] = finished;
                continue;
            }
            if (line.IndexOf(',') < 0)
            {
                section = line.Equals("end", StringComparison.OrdinalIgnoreCase) ? "" : line.ToString().ToLowerInvariant();
                continue;
            }
            switch (section)
            {
                case "objs" or "tobj" or "anim" or "tanm":
                    if (ReadModel(line.ToString(), section) is { } model)
                        _models[model.Hash] = model;
                    else
                        bad++;
                    break;
                case "txdp":
                    var parts = Split(line.ToString());
                    if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
                        _textureParents[parts[0]] = parts[1];
                    else
                        bad++;
                    break;
            }
        }
        return bad;
    }

    private static Gta4ModelDef? ReadModel(string line, string section)
    {
        var p = Split(line);
        bool animated = section is "anim" or "tanm";
        bool timed = section is "tobj" or "tanm";
        int o = animated ? 1 : 0;
        if (p.Length < 16 + o || p[0].Length == 0)
            return null;
        if (!TryFloat(p[2 + o], out float drawDistance) || !uint.TryParse(p[3 + o], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags))
            return null;
        Span<float> v = stackalloc float[10];
        for (int i = 0; i < 10; i++)
            if (!TryFloat(p[5 + o + i], out v[i]))
                return null;
        string dictionary = p[15 + o];
        uint hours = 0;
        if (timed && (p.Length < 17 + o || !uint.TryParse(p[16 + o], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)))
            return null;
        return new Gta4ModelDef(p[0], Joaat.Hash(p[0]), p[1], animated ? p[2] : null, drawDistance, flags,
            new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]), new Vector3(v[6], v[7], v[8]), v[9],
            dictionary.Length == 0 || dictionary.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : dictionary, timed, hours);
    }

    // Lines of an "mlo" section: a header (name, flags, room, portal and object counts, LOD distances), the objects, then
    // "mloroomstart" with each room (name, object and portal counts, box max and min, ?, timecycle, flags) followed by
    // its object indices and "roomend", then "mloportalstart" with each portal (rooms, 4 corners, 4 object indices,
    // flags...) and "mloend".
    private sealed class InteriorReader
    {
        private enum Part { Header, Objects, Rooms, RoomObjects, Portals }

        private Part _part;
        private string? _name;
        private readonly List<Gta4InteriorObject> _objects = [];
        private readonly List<Gta4InteriorRoom> _rooms = [];
        private readonly List<Gta4InteriorPortal> _portals = [];
        private readonly List<int> _roomObjects = [];
        private Gta4InteriorRoom? _room;
        private Gta4InteriorDef? _finished;

        public Gta4InteriorDef? TakeFinished()
        {
            var finished = _finished;
            _finished = null;
            return finished;
        }

        public bool Read(string line)
        {
            if (line.IndexOf(',') < 0)
                return Keyword(line.ToLowerInvariant());
            var p = Split(line.TrimEnd(',', ' ', '\t'));
            switch (_part)
            {
                case Part.Header:
                    if (p[0].Length == 0)
                        return false;
                    _name = p[0];
                    _part = Part.Objects;
                    return true;
                case Part.Objects:
                    return ReadObject(p);
                case Part.Rooms:
                    return ReadRoom(p);
                case Part.RoomObjects:
                    foreach (string index in p)
                    {
                        if (!int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                            return false;
                        if (i >= 0)
                            _roomObjects.Add(i);
                    }
                    return true;
                case Part.Portals:
                    return ReadPortal(p);
            }
            return false;
        }

        private bool Keyword(string keyword)
        {
            switch (keyword)
            {
                case "mloroomstart" when _part == Part.Objects:
                    _part = Part.Rooms;
                    return true;
                case "roomend" when _part == Part.RoomObjects && _room is not null:
                    _rooms.Add(_room with { Objects = [.. _roomObjects] });
                    _room = null;
                    _part = Part.Rooms;
                    return true;
                case "mloportalstart" when _part == Part.Rooms:
                    _part = Part.Portals;
                    return true;
                case "mloend" when _name is not null && _part != Part.RoomObjects:
                    _finished = new Gta4InteriorDef(_name, Joaat.Hash(_name), [.. _objects], [.. _rooms], [.. _portals]);
                    Reset();
                    return true;
            }
            Reset();
            return false;
        }

        private void Reset()
        {
            _part = Part.Header;
            _name = null;
            _room = null;
            _objects.Clear();
            _rooms.Clear();
            _portals.Clear();
            _roomObjects.Clear();
        }

        private bool ReadObject(string[] p)
        {
            Span<float> v = stackalloc float[7];
            if (p.Length < 10 || p[0].Length == 0 || !Floats(p.AsSpan(1, 7), v)
                || !int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lod)
                || !uint.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags))
                return false;
            _objects.Add(new Gta4InteriorObject(Joaat.Hash(p[0]), new Vector3(v[0], v[1], v[2]),
                new Quaternion(v[3], v[4], v[5], v[6]), lod, flags));
            return true;
        }

        private bool ReadRoom(string[] p)
        {
            Span<float> v = stackalloc float[6];
            if (p.Length < 12 || p[0].Length == 0 || !Floats(p.AsSpan(3, 6), v)
                || !uint.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint timecycle))
                return false;
            var a = new Vector3(v[0], v[1], v[2]);
            var b = new Vector3(v[3], v[4], v[5]);
            _room = new Gta4InteriorRoom(p[0], Vector3.Min(a, b), Vector3.Max(a, b), timecycle, []);
            _roomObjects.Clear();
            _part = Part.RoomObjects;
            return true;
        }

        private bool ReadPortal(string[] p)
        {
            Span<float> v = stackalloc float[12];
            if (p.Length < 18 || !Floats(p.AsSpan(2, 12), v)
                || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
                || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int to))
                return false;
            var objects = new List<int>();
            for (int i = 14; i < 18; i++)
            {
                if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    return false;
                if (index >= 0)
                    objects.Add(index);
            }
            _portals.Add(new Gta4InteriorPortal(from, to,
                [new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), new(v[6], v[7], v[8]), new(v[9], v[10], v[11])], [.. objects]));
            return true;
        }

        private static bool Floats(ReadOnlySpan<string> parts, Span<float> values)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryFloat(parts[i], out values[i]))
                    return false;
            }
            return true;
        }
    }

    private static string[] Split(string line) => line.Split(',', StringSplitOptions.TrimEntries);

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
