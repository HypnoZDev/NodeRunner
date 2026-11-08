using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Reader for doortuning.ymt (PSO in the game) and doortuning.meta (XML, also what mappers write by hand).
public sealed class DoorTuningReader : IDoorTuningReader
{
    // The compiled file names its fields with other hashes than the XML names (matched field by field on the two files).
    private static readonly Dictionary<uint, string> PsoNames = new()
    {
        [0x8FB9AE81] = "CDoorTuningFile",
        [0x800A762C] = "NamedTuningArray",
        [0x3BB7FE32] = "ModelToTuneMapping",
        [0xACEC22BE] = "Name",
        [0x8A295D02] = "Tuning",
        [0xFB5833A9] = "ModelName",
        [0xAE23B988] = "TuningName",
        [0x9AE6C89C] = "AutoOpenVolumeOffset",
        [0x4B621302] = "Flags",
        [0x16ACF93F] = "AutoOpenRadiusModifier",
        [0xEFDFD9A6] = "AutoOpenRate",
        [0x973DBC3D] = "AutoOpenCosineAngleBetweenThreshold",
        [0x31F4FCD0] = "AutoOpenCloseRateTaper",
        [0xF77A0CF4] = "UseAutoOpenTriggerBox",
        [0x99BEEADB] = "CustomTriggerBox",
        [0x9D7E3262] = "TriggerBoxMinMax",
        [0xCC52011A] = "BreakableByVehicle",
        [0x577E64DB] = "BreakingImpulse",
        [0x41AC8340] = "ShouldLatchShut",
        [0x6032C592] = "MassMultiplier",
        [0x0525A311] = "WeaponImpulseMultiplier",
        [0x26AB5818] = "RotationLimitAngle",
        [0xD2F12FC5] = "TorqueAngularVelocityLimit",
        [0x4098B7C1] = "StdDoorRotDir",
    };

    private static readonly string[] Names = ["min", "max", "Item"];

    public (IReadOnlyList<DoorTuning> Tunings, IReadOnlyList<(string Model, string Tuning)> Models) Read(byte[] data)
    {
        var root = Parse(data);
        var tunings = new List<DoorTuning>();
        foreach (var item in Child(root, "NamedTuningArray")?.Elements() ?? [])
        {
            string? name = Child(item, "Name")?.Value.Trim();
            if (string.IsNullOrEmpty(name) || Child(item, "Tuning") is not { } t)
                continue;
            var box = Child(t, "TriggerBoxMinMax");
            tunings.Add(new DoorTuning(name, Vector(Child(t, "AutoOpenVolumeOffset")), Flags(Child(t, "Flags")),
                Float(t, "AutoOpenRadiusModifier", 1), Float(t, "AutoOpenRate", 1), Float(t, "AutoOpenCosineAngleBetweenThreshold", -1),
                Bool(t, "AutoOpenCloseRateTaper"), Bool(t, "UseAutoOpenTriggerBox"), Bool(t, "CustomTriggerBox"),
                Vector(Child(box, "min")), Vector(Child(box, "max")), Bool(t, "BreakableByVehicle"), Float(t, "BreakingImpulse", 0),
                Bool(t, "ShouldLatchShut"), Float(t, "MassMultiplier", 1), Float(t, "WeaponImpulseMultiplier", 1),
                Float(t, "RotationLimitAngle", 0), Float(t, "TorqueAngularVelocityLimit", 5), Rotation(Child(t, "StdDoorRotDir"))));
        }
        var models = new List<(string, string)>();
        foreach (var item in Child(root, "ModelToTuneMapping")?.Elements() ?? [])
        {
            string? model = Child(item, "ModelName")?.Value.Trim();
            string? tuning = Child(item, "TuningName")?.Value.Trim();
            if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(tuning))
                models.Add((model, tuning));
        }
        return (tunings, models);
    }

    // The XML a mapper pastes in a doortuning.meta: the tuning, then the model given to it.
    public static string Write(DoorTuning t, string modelName)
    {
        static string F(float v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
        static string B(bool v) => v ? "true" : "false";
        static string V(Vector3 v) => $"x=\"{F(v.X)}\" y=\"{F(v.Y)}\" z=\"{F(v.Z)}\"";
        var flags = Enum.GetValues<DoorTuningFlags>().Where(f => f != DoorTuningFlags.None && t.Flags.HasFlag(f)).Select(f => f.ToString());
        string rotation = t.StdDoorRotDir switch
        {
            DoorRotation.Negative => "StdDoorOpenNegDir",
            DoorRotation.Positive => "StdDoorOpenPosDir",
            _ => "StdDoorOpenBothDir",
        };
        string flagText = string.Join(' ', flags);
        var text = new StringBuilder();
        text.Append("<!-- NamedTuningArray -->\n<Item>\n")
            .Append($"  <Name>{t.Name}</Name>\n  <Tuning>\n")
            .Append($"    <AutoOpenVolumeOffset {V(t.AutoOpenVolumeOffset)} />\n")
            .Append(flagText.Length == 0 ? "    <Flags />\n" : $"    <Flags>{flagText}</Flags>\n")
            .Append($"    <AutoOpenRadiusModifier value=\"{F(t.AutoOpenRadiusModifier)}\" />\n")
            .Append($"    <AutoOpenRate value=\"{F(t.AutoOpenRate)}\" />\n")
            .Append($"    <AutoOpenCosineAngleBetweenThreshold value=\"{F(t.AutoOpenCosineAngleBetweenThreshold)}\" />\n")
            .Append($"    <AutoOpenCloseRateTaper value=\"{B(t.AutoOpenCloseRateTaper)}\" />\n")
            .Append($"    <UseAutoOpenTriggerBox value=\"{B(t.UseAutoOpenTriggerBox)}\" />\n")
            .Append($"    <CustomTriggerBox value=\"{B(t.CustomTriggerBox)}\" />\n")
            .Append($"    <TriggerBoxMinMax>\n      <min {V(t.TriggerBoxMin)} w=\"0.000000\" />\n      <max {V(t.TriggerBoxMax)} w=\"0.000000\" />\n    </TriggerBoxMinMax>\n")
            .Append($"    <BreakableByVehicle value=\"{B(t.BreakableByVehicle)}\" />\n")
            .Append($"    <BreakingImpulse value=\"{F(t.BreakingImpulse)}\" />\n")
            .Append($"    <ShouldLatchShut value=\"{B(t.ShouldLatchShut)}\" />\n")
            .Append($"    <MassMultiplier value=\"{F(t.MassMultiplier)}\" />\n")
            .Append($"    <WeaponImpulseMultiplier value=\"{F(t.WeaponImpulseMultiplier)}\" />\n")
            .Append($"    <RotationLimitAngle value=\"{F(t.RotationLimitAngle)}\" />\n")
            .Append($"    <TorqueAngularVelocityLimit value=\"{F(t.TorqueAngularVelocityLimit)}\" />\n")
            .Append($"    <StdDoorRotDir>{rotation}</StdDoorRotDir>\n")
            .Append("  </Tuning>\n</Item>\n\n")
            .Append("<!-- ModelToTuneMapping -->\n<Item>\n")
            .Append($"  <ModelName>{modelName}</ModelName>\n  <TuningName>{t.Name}</TuningName>\n</Item>");
        return text.ToString();
    }

    private static XElement Parse(byte[] data)
    {
        if (PsoDocument.IsPso(data))
            return PsoDocument.Parse(data, Names, PsoNames);
        try
        {
            return XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root ?? throw new InvalidDataException("XML vide.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
        }
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    // XML keeps values in a "value" attribute, the PSO reader as text.
    private static string Value(XElement? element) => (element?.Attribute("value")?.Value ?? element?.Value ?? "").Trim();

    private static float Float(XElement t, string name, float fallback) =>
        float.TryParse(Value(Child(t, name)), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static bool Bool(XElement t, string name) => Value(Child(t, name)).Equals("true", StringComparison.OrdinalIgnoreCase);

    // XML: x / y / z attributes; PSO: "x y z [w]".
    private static Vector3 Vector(XElement? element)
    {
        if (element is null)
            return Vector3.Zero;
        static float P(string? s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;
        if (element.Attribute("x") is not null)
            return new Vector3(P(element.Attribute("x")?.Value), P(element.Attribute("y")?.Value), P(element.Attribute("z")?.Value));
        var parts = element.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? new Vector3(P(parts[0]), P(parts[1]), P(parts[2])) : Vector3.Zero;
    }

    // XML: flag names separated by spaces; PSO: the number.
    private static DoorTuningFlags Flags(XElement? element)
    {
        string text = Value(element);
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint bits))
            return (DoorTuningFlags)bits;
        var flags = DoorTuningFlags.None;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<DoorTuningFlags>(word, true, out var flag))
                flags |= flag;
        return flags;
    }

    private static DoorRotation Rotation(XElement? element) => Value(element) switch
    {
        "1" or "StdDoorOpenNegDir" => DoorRotation.Negative,
        "2" or "StdDoorOpenPosDir" => DoorRotation.Positive,
        _ => DoorRotation.Both,
    };
}
