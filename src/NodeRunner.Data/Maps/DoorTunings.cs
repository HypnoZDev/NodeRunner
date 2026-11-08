using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Maps;

// Bits of CDoorTuning.Flags (order measured by comparing doortuning.ymt with doortuning.meta).
[Flags]
public enum DoorTuningFlags : uint
{
    None = 0,
    DontCloseWhenTouched = 1 << 0,
    AutoOpensForSPVehicleWithPedsOnly = 1 << 1,
    AutoOpensForSPPlayerPedsOnly = 1 << 2,
    AutoOpensForMPVehicleWithPedsOnly = 1 << 3,
    AutoOpensForMPPlayerPedsOnly = 1 << 4,
    DelayDoorClosingForPlayer = 1 << 5,
    AutoOpensForAllVehicles = 1 << 6,
    IgnoreOpenDoorTaskEdgeLerp = 1 << 7,
    AutoOpensForLawEnforcement = 1 << 8,
}

// StdDoorRotDir: 0 = StdDoorOpenBothDir, 1 = StdDoorOpenNegDir (measured), 2 = StdDoorOpenPosDir.
public enum DoorRotation
{
    Both,
    Negative,
    Positive,
}

// One named tuning of doortuning.ymt. RotationLimit 0 = the game's default limit.
public sealed record DoorTuning(string Name, Vector3 AutoOpenVolumeOffset, DoorTuningFlags Flags, float AutoOpenRadiusModifier,
    float AutoOpenRate, float AutoOpenCosineAngleBetweenThreshold, bool AutoOpenCloseRateTaper, bool UseAutoOpenTriggerBox,
    bool CustomTriggerBox, Vector3 TriggerBoxMin, Vector3 TriggerBoxMax, bool BreakableByVehicle, float BreakingImpulse,
    bool ShouldLatchShut, float MassMultiplier, float WeaponImpulseMultiplier, float RotationLimitAngle,
    float TorqueAngularVelocityLimit, DoorRotation StdDoorRotDir)
{
    public bool HasTriggerBox => UseAutoOpenTriggerBox && CustomTriggerBox && TriggerBoxMax.X > TriggerBoxMin.X;
}

public interface IDoorTuningReader
{
    // The named tunings and the (model name, tuning name) pairs of a doortuning.ymt (PSO) or doortuning.meta (XML).
    (IReadOnlyList<DoorTuning> Tunings, IReadOnlyList<(string Model, string Tuning)> Models) Read(byte[] data);
}

// The door tunings of the game (doortuning.ymt), with those of the project's files on top.
public sealed class DoorTunings
{
    private readonly Dictionary<string, DoorTuning> _byName;
    private readonly Dictionary<uint, string> _byModel;

    private DoorTunings(Dictionary<string, DoorTuning> byName, Dictionary<uint, string> byModel, IReadOnlyList<string> errors)
    {
        _byName = byName;
        _byModel = byModel;
        Errors = errors;
    }

    public static readonly DoorTunings Empty = new(new(StringComparer.OrdinalIgnoreCase), [], []);

    public IReadOnlyList<string> Errors { get; }

    public int ModelCount => _byModel.Count;

    public IEnumerable<DoorTuning> All => _byName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public DoorTuning? Named(string name) => _byName.GetValueOrDefault(name);

    // The tuning given to this model by the file, or null (the game then uses a default tuning for its kind of door).
    public DoorTuning? OfModel(uint modelHash) =>
        _byModel.TryGetValue(modelHash, out var name) ? _byName.GetValueOrDefault(name) : null;

    // The files oldest first, later ones overriding by tuning name and by model. The base game ships both a compiled .ymt
    // and the .meta it was made from: the .meta of the game is only used when no .ymt exists.
    public static DoorTunings Load(GameContent content, IDoorTuningReader reader)
    {
        var all = content.EnumerateAllVersions(".ymt").Concat(content.EnumerateAllVersions(".meta"))
            .Where(p => Path.GetFileNameWithoutExtension(p).Equals("doortuning", StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool IsProject(string p) => p.StartsWith(GameContent.LooseFilesRoot, StringComparison.OrdinalIgnoreCase);
        bool IsYmt(string p) => p.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase);
        var game = all.Where(p => !IsProject(p)).ToList();
        if (game.Any(IsYmt))
            game = game.Where(IsYmt).ToList();
        var byName = new Dictionary<string, DoorTuning>(StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<uint, string>();
        var errors = new List<string>();
        foreach (var path in game.Concat(all.Where(IsProject)))
        {
            try
            {
                if (content.ReadFile(path) is not { } data)
                    continue;
                var (tunings, models) = reader.Read(data);
                foreach (var tuning in tunings)
                    byName[tuning.Name] = tuning;
                foreach (var (model, tuning) in models)
                    byModel[Joaat.Hash(model)] = tuning;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }
        return new DoorTunings(byName, byModel, errors);
    }
}
