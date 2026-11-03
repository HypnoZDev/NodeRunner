using System.Text.Json;
using System.Text.Json.Serialization;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.App.Configuration;

/// <summary>User settings, saved as settings.json (see <see cref="AppPaths"/>).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Game folder chosen manually by the user. Null = automatic detection.</summary>
    public string? GameFolder { get; set; }

    /// <summary>Synchronise rendering with the screen refresh rate.</summary>
    public bool VSync { get; set; } = true;

    /// <summary>Sky drawn behind the map (otherwise a plain dark background).</summary>
    public bool ShowSky { get; set; } = true;

    /// <summary>Hour of the day for the sun, the sky and the light (0 to 24).</summary>
    public float TimeOfDay { get; set; } = 13f;

    /// <summary>Distance fog; objects beyond <see cref="FogDistance"/> are not drawn at all (faster).</summary>
    public bool Fog { get; set; } = true;

    /// <summary>Distance (m) at which the fog hides everything.</summary>
    public float FogDistance { get; set; } = 4000f;

    /// <summary>Shadows cast by the sun (and the moon at night).</summary>
    public bool Shadows { get; set; } = true;

    /// <summary>Distance (m) up to which the shadows are drawn, in front of the camera.</summary>
    public float ShadowDistance { get; set; } = 400f;

    /// <summary>Texture filtering at grazing angles: 1 (fastest) to 16 (sharpest ground in the distance).</summary>
    public int Anisotropy { get; set; } = 8;

    /// <summary>
    /// Save folder of the versions before projects: turned into a project at start-up (<see cref="AppState.MigrateSaveFolder"/>),
    /// then cleared.
    /// </summary>
    public string? SaveFolder { get; set; }

    /// <summary>Folder of the open project (holds projet.json), null if none.</summary>
    public string? ProjectFolder { get; set; }

    /// <summary>Projects opened lately, most recent first.</summary>
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Folder where new projects are created (each in a sub-folder), null = Documents\NodeRunner.</summary>
    public string? ProjectsFolder { get; set; }

    /// <summary>Last folder where a FiveM resource was exported: proposed for the projects that have none yet.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>Name of the last exported FiveM resource (before projects; now kept by each project).</summary>
    public string? ExportResourceName { get; set; }

    /// <summary>Show the reference grid on the sea-level plane.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>While an object is moved horizontally with the 3D handles, it stays on the collision below it.</summary>
    public bool FollowGround { get; set; }

    /// <summary>Names of the layers the user has hidden (e.g. "Path.Pedestrian").</summary>
    public HashSet<string> HiddenLayers { get; set; } = new();

    /// <summary>Layers hidden by default that the user has turned on (e.g. "Collision.Road").</summary>
    public HashSet<string> ShownLayers { get; set; } = new();

    /// <summary>Collision files are loaded within this distance of the camera (metres).</summary>
    public float CollisionRadius { get; set; } = 200f;

    /// <summary>Navmesh tiles are loaded within this distance of the camera (metres).</summary>
    public float NavMeshRadius { get; set; } = 450f;

    /// <summary>Object boxes are shown within this distance of the camera (metres).</summary>
    public float EntityRadius { get; set; } = 300f;

    /// <summary>Multiplies the game's draw distances of the 3D models (1 = as in the game).</summary>
    public float ViewDistanceMultiplier { get; set; } = 1f;

    /// <summary>Multiplayer map (as used by FiveM) with the DLC packs' map changes, or story mode map.</summary>
    public MapVersion MapVersion { get; set; } = MapVersion.Multiplayer;

    /// <summary>Last DLC pack applied (folder name, e.g. "mpheist"): shows the map as it was after it. Null = all.</summary>
    public string? LastDlcPack { get; set; }

    public MapSelection MapSelection => new(MapVersion, LastDlcPack);

    /// <summary>
    /// Maps loaded by scripts and missions that are not shown (file names in lower case, e.g. "v_tunnel_hole_swap").
    /// Null = the default choice: everything except the alternative versions of a same place.
    /// </summary>
    public HashSet<string>? HiddenScriptedMaps { get; set; }

    /// <summary>Tool of the 3D view last chosen (keys 1 to 4).</summary>
    public TransformTool Tool { get; set; } = TransformTool.Select;

    /// <summary>Language of the copied positions: Lua (vector3), JSON or C#.</summary>
    public NodeRunner.Editor.CopyFormat CopyFormat { get; set; } = NodeRunner.Editor.CopyFormat.Lua;

    /// <summary>Camera position when the application was last closed. Null = default view.</summary>
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

/// <summary>Saved camera: world position (metres), angles (radians) and base speed (m/s).</summary>
public sealed class CameraSettings
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Speed { get; set; } = 50f;
}
