using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using NodeRunner.Data.Cutscenes;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Cutscenes;

// Reads a cutscene file (.cut: PSO, rage::cutfCutsceneFile2).
public static class CutsceneReader
{
    // The field and structure names used here. In these files their hashes keep the case of the name.
    private static readonly string[] Names =
    [
        "fTotalDuration", "vOffset", "fRotation", "pCutsceneObjects", "pCutsceneLoadEventList", "pCutsceneEventList",
        "pCutsceneEventArgsList", "cameraCutList", "iObjectId", "cName", "StreamingName", "AnimStreamingBase", "vPosition",
        "fRadius", "fTime", "iEventId", "iEventArgsIndex", "iObjectIdList", "vRotationQuaternion", "fNearDrawDistance",
        "fFarDrawDistance", "iComponent", "iDrawable", "iTexture", "concatDataList", "cSceneName", "fStartTime",
        "bValidForPlayBack",
    ];

    private static readonly Dictionary<string, CutsceneObjectKind> Kinds = new()
    {
        ["rage__cutfPedModelObject"] = CutsceneObjectKind.Ped,
        ["rage__cutfPropModelObject"] = CutsceneObjectKind.Prop,
        ["rage__cutfVehicleModelObject"] = CutsceneObjectKind.Vehicle,
        ["rage__cutfWeaponModelObject"] = CutsceneObjectKind.Weapon,
        ["rage__cutfCameraObject"] = CutsceneObjectKind.Camera,
        ["rage__cutfAudioObject"] = CutsceneObjectKind.Audio,
        ["rage__cutfSubtitleObject"] = CutsceneObjectKind.Subtitle,
        ["rage__cutfLightObject"] = CutsceneObjectKind.Light,
        ["rage__cutfAnimatedLightObject"] = CutsceneObjectKind.AnimatedLight,
        ["rage__cutfHiddenModelObject"] = CutsceneObjectKind.HiddenModel,
        ["rage__cutfFixupModelObject"] = CutsceneObjectKind.FixupModel,
        ["rage__cutfBlockingBoundsObject"] = CutsceneObjectKind.BlockingBounds,
        ["rage__cutfParticleEffectObject"] = CutsceneObjectKind.ParticleEffect,
        ["rage__cutfOverlayObject"] = CutsceneObjectKind.Overlay,
        ["rage__cutfScreenFadeObject"] = CutsceneObjectKind.ScreenFade,
        ["rage__cutfDecalObject"] = CutsceneObjectKind.Decal,
        ["rage__cutfAnimationManagerObject"] = CutsceneObjectKind.AnimationManager,
        ["rage__cutfAssetManagerObject"] = CutsceneObjectKind.AssetManager,
    };

    private static readonly Dictionary<uint, string> Aliases =
        Names.Concat(Kinds.Keys).Distinct().ToDictionary(CaseHash, n => n);

    public static Cutscene Read(byte[] data, string name)
    {
        var root = PsoDocument.Parse(data, [], Aliases);
        var arguments = (root.Element("pCutsceneEventArgsList")?.Elements("Item") ?? []).Select(Arguments).ToList();
        return new Cutscene
        {
            Name = name,
            Duration = Float(root.Element("fTotalDuration")),
            Offset = Vector(root.Element("vOffset")),
            Rotation = Float(root.Element("fRotation")),
            Objects = (root.Element("pCutsceneObjects")?.Elements("Item") ?? []).Select(Object).ToList(),
            LoadEvents = Events(root.Element("pCutsceneLoadEventList"), arguments),
            Events = Events(root.Element("pCutsceneEventList"), arguments),
            CameraCuts = Floats(root.Element("cameraCutList")),
            Sections = Sections(root.Element("concatDataList")),
        };
    }

    // Scenes put end to end, each at its own place: the used entries (a name, playable), by start time.
    private static List<CutsceneSection> Sections(XElement? list) =>
        (list?.Elements("Item") ?? [])
        .Where(i => !string.IsNullOrWhiteSpace(i.Element("cSceneName")?.Value) && i.Element("bValidForPlayBack")?.Value.Trim() != "false")
        .Select(i => new CutsceneSection(Float(i.Element("fStartTime")), Vector(i.Element("vOffset")), Float(i.Element("fRotation"))))
        .OrderBy(s => s.Start)
        .ToList();

    private static CutsceneObject Object(XElement item)
    {
        string? type = item.Attribute("type")?.Value;
        var kind = type is not null && Kinds.TryGetValue(type, out var known) ? known : CutsceneObjectKind.Other;
        string? text = item.Element("cName")?.Value.Trim();
        return new CutsceneObject(Int(item.Element("iObjectId")), kind, Hash(text), Hash(item.Element("StreamingName")?.Value),
            Hash(item.Element("AnimStreamingBase")?.Value))
        {
            Text = text is { Length: > 0 } && !text.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text : null,
            Position = Vector(item.Element("vPosition")),
            Radius = Float(item.Element("fRadius")),
        };
    }

    private static List<CutsceneEvent> Events(XElement? list, List<CutsceneEventArgs?> arguments)
    {
        var events = new List<CutsceneEvent>();
        foreach (var item in list?.Elements("Item") ?? [])
        {
            int index = item.Element("iEventArgsIndex") is { } at ? Int(at) : -1;
            var args = index >= 0 && index < arguments.Count ? arguments[index] : null;
            events.Add(new CutsceneEvent(Float(item.Element("fTime")), Int(item.Element("iEventId")),
                item.Element("iObjectId") is { } target ? Int(target) : -1, args));
        }
        // Stable: events at the same time keep the file's order.
        return [.. events.OrderBy(e => e.Time)];
    }

    private static CutsceneEventArgs? Arguments(XElement item)
    {
        var ids = new List<int>();
        if (item.Element("iObjectId") is { } single)
            ids.Add(Int(single));
        if (item.Element("iObjectIdList") is { } several)
            ids.AddRange(Ints(several));
        var rotation = item.Element("vRotationQuaternion") is { } q && Numbers(q) is { Length: 4 } r
            ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;
        return new CutsceneEventArgs
        {
            ObjectIds = ids,
            Name = Hash(item.Element("cName")?.Value),
            Position = Vector(item.Element("vPosition")),
            Rotation = rotation,
            NearClip = Float(item.Element("fNearDrawDistance")),
            FarClip = Float(item.Element("fFarDrawDistance")),
            Component = item.Element("iComponent") is { } c ? Int(c) : -1,
            Drawable = item.Element("iDrawable") is { } d ? Int(d) : -1,
            Texture = item.Element("iTexture") is { } t ? Int(t) : -1,
        };
    }

    // Jenkins one-at-a-time hash, case kept (the PSO names of the .cut files).
    public static uint CaseHash(string text)
    {
        uint hash = 0;
        foreach (char c in text)
        {
            hash += (byte)c;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    // A name: "hash_XXXXXXXX" or "0xXXXXXXXX" as read, or text (hashed, lower case, as the game's names).
    private static uint Hash(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        if ((text.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            && uint.TryParse(text.AsSpan(text[0] == '0' ? 2 : 5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
            return hash;
        return Hashing.Joaat.Hash(text);
    }

    private static float[] Numbers(XElement? element) =>
        (element?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f).ToArray();

    private static float Float(XElement? element) => Numbers(element) is { Length: > 0 } n ? n[0] : 0f;

    private static List<float> Floats(XElement? element) => [.. Numbers(element)];

    private static int Int(XElement? element) => (int)Float(element);

    private static IEnumerable<int> Ints(XElement element) => Numbers(element).Select(f => (int)f);

    private static Vector3 Vector(XElement? element) => Numbers(element) is { Length: >= 3 } n ? new Vector3(n[0], n[1], n[2]) : Vector3.Zero;
}
