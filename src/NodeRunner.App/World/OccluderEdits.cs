using System.Numerics;
using System.Text.Json;
using NodeRunner.Data.Maps;

namespace NodeRunner.App.World;

internal sealed record SavedOccluderBox(float X, float Y, float Z, float SizeX, float SizeY, float SizeZ, float AngleDegrees)
{
    public static SavedOccluderBox From(OccluderBox box) =>
        new(box.Center.X, box.Center.Y, box.Center.Z, box.Size.X, box.Size.Y, box.Size.Z, box.Angle * 180f / MathF.PI);

    public OccluderBox ToBox(int index) =>
        new OccluderBox(index, new Vector3(X, Y, Z), new Vector3(SizeX, SizeY, SizeZ), AngleDegrees * MathF.PI / 180f).Quantized();
}

// Changes to one game occluder file, by box / mesh number in the game's file. BoxCount and ModelCount are the game
// file's, to notice a game update.
internal sealed record SavedOccluderFile(int BoxCount, int ModelCount, Dictionary<int, SavedOccluderBox>? Changed,
    List<int>? DeletedBoxes, List<int>? DeletedModels);

// The project's occluder changes (occlusion.json): the game's files are rewritten from them at each save, and the
// boxes created by hand go to the project's own file.
internal sealed record OccluderEdits(int Format, string? ProjectFile, List<SavedOccluderBox>? Created,
    Dictionary<string, SavedOccluderFile>? Files)
{
    public const string FileName = "occlusion.json";

    public static readonly OccluderEdits Empty = new(1, null, null, null);

    public bool IsEmpty => (Files is null || Files.Count == 0) && (Created is null || Created.Count == 0);

    // The .ymap files written into the stream folder.
    public IEnumerable<string> StreamFileNames =>
        (Files?.Keys.Select(Path.GetFileName).OfType<string>() ?? [])
        .Concat(Created is { Count: > 0 } && ProjectFile is { } project ? [project] : []);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OccluderEdits FromJson(string json) =>
        JsonSerializer.Deserialize<OccluderEdits>(json) ?? throw new InvalidDataException($"{FileName} vide.");

    // The project's saved changes, or Empty without project or file.
    public static OccluderEdits Load(string? projectFolder)
    {
        if (projectFolder is null)
            return Empty;
        string file = Path.Combine(projectFolder, FileName);
        return File.Exists(file) ? FromJson(File.ReadAllText(file)) : Empty;
    }
}
