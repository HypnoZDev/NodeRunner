using System.Text.Json;
using System.Text.Json.Serialization;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.App.Configuration;

// User settings, saved as settings.json.
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string? GameFolder { get; set; }

    public bool VSync { get; set; } = true;

    // Mouse look speed of the camera (1 = default).
    public float MouseSensitivity { get; set; } = 1f;

    // Share of the Windows 11 backdrop (Mica) seen through the interface panels; 0 = opaque. Windows 11 22H2 or later.
    public float Transparency { get; set; } = 0.15f;

    // Interface size (1.5 = 150 %); 0 = the Windows display scale.
    public float UiScale { get; set; }

    public bool ShowSky { get; set; } = true;

    public float TimeOfDay { get; set; } = 13f;

    public bool Fog { get; set; } = true;

    public float FogDistance { get; set; } = 4000f;

    public bool Shadows { get; set; } = true;

    public float ShadowDistance { get; set; } = 400f;

    // Objects hidden behind the occluders are not drawn, as in the game.
    public bool OcclusionPreview { get; set; }

    public int Anisotropy { get; set; } = 8;

    // Save folder of the versions before projects: turned into a project at start-up (MigrateSaveFolder), then
    // cleared.
    public string? SaveFolder { get; set; }

    public string? ProjectFolder { get; set; }

    public List<string> RecentProjects { get; set; } = [];

    public string? ProjectsFolder { get; set; }

    public string? ExportFolder { get; set; }

    public string? ExportResourceName { get; set; }

    // NodeRunner Online: name shown to the others, last address joined.
    public string? OnlineName { get; set; }

    public string? OnlineAddress { get; set; }

    public bool ShowGrid { get; set; } = true;

    public bool FollowGround { get; set; }

    public HashSet<string> HiddenLayers { get; set; } = new();

    public HashSet<string> ShownLayers { get; set; } = new();

    public float CollisionRadius { get; set; } = 200f;

    // Version of the collision shown and edited: peds and objects, vehicles (hi@) or other (ma@).
    public NodeRunner.Data.Collisions.CollisionKind CollisionKind { get; set; }

    public float NavMeshRadius { get; set; } = 450f;

    public float EntityRadius { get; set; } = 300f;

    public float ViewDistanceMultiplier { get; set; } = 1f;

    public MapVersion MapVersion { get; set; } = MapVersion.Multiplayer;

    public string? LastDlcPack { get; set; }

    public MapSelection MapSelection => new(MapVersion, LastDlcPack);

    // Maps loaded by scripts and missions that are not shown (file names in lower case, e.g. "v_tunnel_hole_swap").
    public HashSet<string>? HiddenScriptedMaps { get; set; }

    public TransformTool Tool { get; set; } = TransformTool.Select;

    public NodeRunner.Editor.CopyFormat CopyFormat { get; set; } = NodeRunner.Editor.CopyFormat.Lua;

    public CameraSettings? Camera { get; set; }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupted file: start with defaults rather than refusing to start.
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

// Saved camera: world position (metres), angles (radians) and base speed (m/s).
public sealed class CameraSettings
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Speed { get; set; } = 50f;
}
