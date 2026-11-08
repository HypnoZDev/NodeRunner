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

    // Images per second at most; 0 = no limit (other than VSync).
    public int MaxFps { get; set; }

    // Images per second while another window has the focus; 0 = as when active.
    public int BackgroundFps { get; set; } = 30;

    // Name of the graphics card to use (as Windows names it); null = the most powerful one. Read at start-up.
    public string? GraphicsCard { get; set; }

    // Video memory for the models, in MB; 0 = automatic (70 % of what Windows grants NodeRunner).
    public int ModelMemoryMb { get; set; }

    // Interface language; null = the Windows language (French for a French Windows, otherwise English).
    public NodeRunner.Editor.Localization.Language? Language { get; set; }

    [JsonIgnore]
    public NodeRunner.Editor.Localization.Language InterfaceLanguage => Language
        ?? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr"
            ? NodeRunner.Editor.Localization.Language.French
            : NodeRunner.Editor.Localization.Language.English);

    // Accent colour of the interface (0xRRGGBB).
    public uint AccentColor { get; set; } = 0x4C8DFF;

    // Mouse look speed of the camera (1 = default).
    public float MouseSensitivity { get; set; } = 1f;

    // Vertical field of view of the 3D view, in degrees.
    public float FieldOfView { get; set; } = 60f;

    public bool InvertMouseY { get; set; }

    public NodeRunner.App.Input.MovementKeys MovementKeys { get; set; }

    // Steps of the 3D handles while Ctrl is held: metres, degrees, scale.
    public float SnapMove { get; set; } = 0.5f;
    public float SnapRotate { get; set; } = 15f;
    public float SnapScale { get; set; } = 0.1f;

    // The map collision (.ybn) under an object moved by hand moves with it.
    public bool CollisionFollowsObjects { get; set; } = true;

    // Rescue copy of the unsaved changes every N minutes (data/secours); 0 = never.
    public float BackupMinutes { get; set; } = 2f;

    // Share of the Windows 11 backdrop (Mica) seen through the interface panels; 0 = opaque. Windows 11 22H2 or later.
    public float Transparency { get; set; } = 0.15f;

    // Panels can be dragged out of the main window (own Windows windows, other screens). Read at start-up.
    public bool DetachableWindows { get; set; } = true;

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

    // Metres between two grid lines; 0 = automatic (10 m near the ground, 100 m high up).
    public float GridSpacing { get; set; }

    // Colour of the main grid lines (red, green, blue, opacity from 0 to 1).
    public float[] GridColor { get; set; } = [0.55f, 0.58f, 0.62f, 0.6f];

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

    // Everything the display settings window changes, back to its default. The game folder, projects, map version,
    // layers, camera and tools are kept.
    public void ResetDisplaySettings()
    {
        var d = new AppSettings();
        (VSync, MaxFps, BackgroundFps, GraphicsCard, ModelMemoryMb) = (d.VSync, d.MaxFps, d.BackgroundFps, d.GraphicsCard, d.ModelMemoryMb);
        (Language, AccentColor, Transparency, UiScale) = (d.Language, d.AccentColor, d.Transparency, d.UiScale);
        DetachableWindows = d.DetachableWindows;
        (MouseSensitivity, FieldOfView, InvertMouseY, MovementKeys) = (d.MouseSensitivity, d.FieldOfView, d.InvertMouseY, d.MovementKeys);
        (SnapMove, SnapRotate, SnapScale, BackupMinutes, CopyFormat) = (d.SnapMove, d.SnapRotate, d.SnapScale, d.BackupMinutes, d.CopyFormat);
        (ShowSky, TimeOfDay, Fog, FogDistance) = (d.ShowSky, d.TimeOfDay, d.Fog, d.FogDistance);
        (Shadows, ShadowDistance, Anisotropy, ViewDistanceMultiplier) = (d.Shadows, d.ShadowDistance, d.Anisotropy, d.ViewDistanceMultiplier);
        (ShowGrid, GridSpacing, GridColor) = (d.ShowGrid, d.GridSpacing, d.GridColor);
        (CollisionRadius, NavMeshRadius, EntityRadius, ProjectsFolder) = (d.CollisionRadius, d.NavMeshRadius, d.EntityRadius, d.ProjectsFolder);
    }

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
