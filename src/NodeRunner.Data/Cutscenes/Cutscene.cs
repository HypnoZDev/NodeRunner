using System.Numerics;

namespace NodeRunner.Data.Cutscenes;

// What a cutscene object is (its structure type in the .cut file).
public enum CutsceneObjectKind
{
    Other, Ped, Prop, Vehicle, Weapon, Camera, Audio, Subtitle, Light, AnimatedLight, HiddenModel, FixupModel,
    BlockingBounds, ParticleEffect, Overlay, ScreenFade, Decal, AnimationManager, AssetManager,
}

// An object of a cutscene. Name: its name hash (the camera's is "exportcamera"); Model: the model it streams (ped,
// prop, vehicle, weapon: its model name hash); Animation: the base name hash of its clips ("player_one_dual": clips
// "player_one_dual-0", "-1"... in the .ycd of each camera cut). Hidden and fix-up models: Position and Radius say
// which map object they act on. Text: the name when stored as text (audio: "FBI_5_INT_SEQ.WAV").
public sealed record CutsceneObject(int Id, CutsceneObjectKind Kind, uint Name, uint Model, uint Animation)
{
    public string? Text { get; init; }
    public Vector3 Position { get; init; }
    public float Radius { get; init; }
}

// Event numbers of the .cut files (those NodeRunner acts on).
public static class CutsceneEventKind
{
    public const int LoadModels = 6;
    public const int UnloadModels = 7;
    public const int EnableHideObject = 14;
    public const int DisableHideObject = 15;
    public const int EnableAnimation = 22;
    public const int DisableAnimation = 23;
    public const int Subtitle = 30;
    public const int PedVariation = 34;
    public const int CameraCut = 43;
}

// The values an event works on: objects, a name, a place (camera cut: where the camera starts), a ped's clothes.
public sealed record CutsceneEventArgs
{
    public IReadOnlyList<int> ObjectIds { get; init; } = [];
    public uint Name { get; init; }
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public float NearClip { get; init; }
    public float FarClip { get; init; }
    public int Component { get; init; } = -1;
    public int Drawable { get; init; } = -1;
    public int Texture { get; init; } = -1;
}

// An event at a time (seconds from the start): Kind (see CutsceneEventKind), the object it is sent to (-1 none).
public sealed record CutsceneEvent(float Time, int Kind, int ObjectId, CutsceneEventArgs? Args);

// A cutscene (.cut file): where it plays on the map (Offset, turned by Rotation degrees around Z), how long, its
// objects, the events that load them and those that play it, and the times of its camera cuts (the clips of cut n are
// in the .ycd "<name>-n").
public sealed class Cutscene
{
    public required string Name { get; init; }
    public float Duration { get; init; }
    public Vector3 Offset { get; init; }
    public float Rotation { get; init; }
    public IReadOnlyList<CutsceneObject> Objects { get; init; } = [];
    public IReadOnlyList<CutsceneEvent> LoadEvents { get; init; } = [];
    public IReadOnlyList<CutsceneEvent> Events { get; init; } = [];
    public IReadOnlyList<float> CameraCuts { get; init; } = [];

    // Number of the camera cut playing at a time, and when it started.
    public (int Index, float Start) CutAt(float time)
    {
        int index = 0;
        float start = 0f;
        foreach (float cut in CameraCuts)
        {
            if (cut > time)
                break;
            index++;
            start = cut;
        }
        return (index, start);
    }

    // Which clips play each object (by id): its model name hash (the camera: its name, "exportcamera") and which copy
    // of that model it is (0 for the first object with it, 1 for the second...).
    public IReadOnlyDictionary<int, (uint Name, int Copy)> ClipKeys()
    {
        var keys = new Dictionary<int, (uint, int)>();
        var copies = new Dictionary<uint, int>();
        foreach (var o in Objects)
        {
            uint name = o.Model != 0 ? o.Model : o.Kind == CutsceneObjectKind.Camera ? o.Name : 0;
            if (name == 0)
                continue;
            int copy = copies.GetValueOrDefault(name);
            copies[name] = copy + 1;
            keys[o.Id] = (name, copy);
        }
        return keys;
    }

    // The object a clip of camera cut n plays, from its name ("player_zero_dual-3", "s_m_y_cop_01^1-3",
    // "exportcamera-3"): model name hash and copy.
    public static (uint Name, int Copy) ClipKey(string clipName, int cut)
    {
        string name = clipName.Replace('\\', '/').Split('/')[^1];
        if (name.EndsWith(".clip", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        string suffix = "-" + cut.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        if (name.EndsWith("_dual", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        int copy = 0;
        if (name.LastIndexOf('^') is var at and > 0 && int.TryParse(name.AsSpan(at + 1), out int n))
        {
            copy = n;
            name = name[..at];
            if (name.EndsWith("_dual", StringComparison.OrdinalIgnoreCase))
                name = name[..^5];
        }
        return (Hashing.Joaat.Hash(name), copy);
    }

    // Where a point of the scene (in its own space) is on the map.
    public Matrix4x4 SceneTransform => Matrix4x4.CreateRotationZ(Rotation * MathF.PI / 180f) * Matrix4x4.CreateTranslation(Offset);
}
