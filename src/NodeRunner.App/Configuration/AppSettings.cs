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

    // GTA IV folder chosen by hand (otherwise found automatically), and the game opened last time.
    public string? Gta4Folder { get; set; }
    public GameKind LastGame { get; set; } = GameKind.Gta5;

    // GTA IV session: the game or episode whose map is shown, the camera and the layers, kept apart from GTA V.
    public NodeRunner.Data.Native.Gta4.Gta4Episode Gta4Episode { get; set; }
    public CameraSettings? Gta4Camera { get; set; }
    public HashSet<string> Gta4HiddenLayers { get; set; } = new();
    public HashSet<string> Gta4ShownLayers { get; set; } = new();

    // GTA III / Vice City / San Andreas: the folder holding the games (or one game's folder), found once and kept,
    // the game of the trilogy chosen last time, and the camera of each game. Layers are those of GTA IV sessions.
    public string? ClassicFolder { get; set; }
    public GameKind LastClassicGame { get; set; } = GameKind.SanAndreas;
    public Dictionary<GameKind, CameraSettings> ClassicCameras { get; set; } = new();

    // Camera bookmarks of each game, slots 1 to 9 (Ctrl+Shift+digit saves, Ctrl+digit goes back).
    public Dictionary<GameKind, Dictionary<int, CameraSettings>> CameraBookmarks { get; set; } = new();

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

    // Keyboard shortcuts: the preset, and the actions whose keys the user changed (action name -> keys, e.g.
    // "Ctrl+Shift+Z"); the other actions keep the preset's.
    public NodeRunner.App.Input.KeyPreset KeyPreset { get; set; }
    public Dictionary<string, List<string>> KeyBindings { get; set; } = new();

    // Vehicle recording viewer: the vehicle chosen for a recording (recording name -> vehicle model name).
    public Dictionary<string, string> RecordingVehicles { get; set; } = new();

    // Steps of the 3D handles while Ctrl is held: metres, degrees, scale.
    public float SnapMove { get; set; } = 0.5f;
    public float SnapRotate { get; set; } = 15f;
    public float SnapScale { get; set; } = 0.1f;

    // The map collision (.ybn) under an object moved by hand moves with it.
    public bool CollisionFollowsObjects { get; set; } = true;

    // Properties panel shows only placement and the common actions.
    public bool SimpleProperties { get; set; }

    // Welcome screen: shown once, then only if asked.
    public bool WelcomeSeen { get; set; }
    public bool WelcomeAtStartup { get; set; }

    // Rescue copy of the unsaved changes every N minutes (data/secours); 0 = never.
    public float BackupMinutes { get; set; } = 2f;

    // Share of the Windows 11 backdrop (Mica) seen through the interface panels; 0 = opaque. Windows 11 22H2 or later.
    public float Transparency { get; set; } = 0.15f;

    // The map seen blurred behind what floats over the 3D view.
    public bool FrostedGlass { get; set; } = true;

    // Panels can be dragged out of the main window (own Windows windows, other screens). Read at start-up.
    public bool DetachableWindows { get; set; } = true;

    // Ask GitHub at start-up whether a newer version is published (UpdateCheck).
    public bool CheckForUpdates { get; set; } = true;

    // Interface size (1.5 = 150 %); 0 = the Windows display scale.
    public float UiScale { get; set; }

    public bool ShowSky { get; set; } = true;

    public float TimeOfDay { get; set; } = 13f;

    public bool Fog { get; set; } = true;

    public float FogDistance { get; set; } = 4000f;

    public bool Shadows { get; set; } = true;

    public float ShadowDistance { get; set; } = 400f;

    // Water mirroring the scene (not only the sky), bottom bent by the waves.
    public bool WaterReflections { get; set; } = true;

    // The sea's surface moves with the waves of the game's water file (otherwise flat, only its look follows them).
    public bool WaterWaves { get; set; }

    // Advanced rendering of the 3D view (NodeRunner.Render.SceneImage): light beyond white brought back by a filmic
    // curve (HDR), brightness, share of the view's width and height drawn (0.5 to 1), sharpening. Chosen once for the
    // PC's graphics card (GraphicsTierChosen, see QualityPreset.ForCard), then by the user.
    public bool HdrRendering { get; set; }
    public float Exposure { get; set; } = 1f;
    public float RenderScale { get; set; } = 1f;
    public float Sharpness { get; set; } = 0.3f;

    // Smoothing of the edges over several frames (TAA).
    public bool Antialiasing { get; set; }

    // Ambient occlusion (nooks darker): 0 none, 1 normal, 2 high; and its strength (1 = normal).
    public int AmbientOcclusion { get; set; }
    public float OcclusionStrength { get; set; } = 1f;

    // Glow around very bright pixels (HDR only), and its strength (0 to 1).
    public bool Bloom { get; set; }
    public float BloomStrength { get; set; } = 0.5f;

    // Buildings, vehicles and props drawn with their relief and shine textures, shiny surfaces mirroring the sky.
    public bool SurfaceDetail { get; set; }

    // The map's lamps at night (HDR only): their light on the surfaces around and their glows seen from afar; brightness.
    public bool NightLights { get; set; }
    public float NightLightStrength { get; set; } = 1f;

    // Sky worked out from the air (HDR only), and the sun's rays through the gaps (with their strength, 0 to 1).
    public bool PhysicalSky { get; set; }
    public bool SunRays { get; set; }
    public float SunRayStrength { get; set; } = 0.5f;

    // The game's weather shown (clouds, light, fog), and the clouds in volume: 0 none, 1 simple (small PCs), 2 normal,
    // 3 high; -1 in the settings of older versions: chosen from their other effects (Load).
    public NodeRunner.Render.Weather Weather { get; set; } = NodeRunner.Render.Weather.Clear;
    public int CloudQuality { get; set; } = -1;

    // Width and height of each image of the sun's shadows (texels): 1024, 2048 or 4096.
    public int ShadowResolution { get; set; } = 2048;

    public bool GraphicsTierChosen { get; set; }

    // Objects hidden behind the occluders are not drawn, as in the game.
    public bool OcclusionPreview { get; set; }

    // Peds, animals and vehicles shown on the scenario points the game would use at the hour shown.
    public bool ScenarioLife { get; set; }

    public int Anisotropy { get; set; } = 8;

    // Save folder of the versions before projects: turned into a project at start-up (MigrateSaveFolder), then
    // cleared.
    public string? SaveFolder { get; set; }

    public string? ProjectFolder { get; set; }

    // Other projects shown with the open one (each saved into its own folder).
    public List<string> ExtraProjectFolders { get; set; } = [];

    public List<string> RecentProjects { get; set; } = [];

    public string? ProjectsFolder { get; set; }

    // Prop library shared by every project (null = Documents/NodeRunner/Bibliothèque).
    public string? LibraryFolder { get; set; }

    // Favourite props of the library (type names).
    public List<string> LibraryFavorites { get; set; } = [];

    public ViewerSettings Viewer { get; set; } = new();

    public string? ExportFolder { get; set; }

    public string? ExportResourceName { get; set; }

    // NodeRunner Online: name shown to the others, last address joined.
    public string? OnlineName { get; set; }

    public string? OnlineAddress { get; set; }

    // NodeRunner Online relay (wss://...); null = NodeRunner's own.
    public string? OnlineRelay { get; set; }

    // FiveM server folder (server.cfg + resources) where the live link resource is installed.
    public string? GameLinkServer { get; set; }

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
        (DetachableWindows, FrostedGlass) = (d.DetachableWindows, d.FrostedGlass);
        (MouseSensitivity, FieldOfView, InvertMouseY, MovementKeys) = (d.MouseSensitivity, d.FieldOfView, d.InvertMouseY, d.MovementKeys);
        (SnapMove, SnapRotate, SnapScale, BackupMinutes, CopyFormat) = (d.SnapMove, d.SnapRotate, d.SnapScale, d.BackupMinutes, d.CopyFormat);
        (ShowSky, TimeOfDay, Fog, FogDistance) = (d.ShowSky, d.TimeOfDay, d.Fog, d.FogDistance);
        (Shadows, ShadowDistance, Anisotropy, ViewDistanceMultiplier) = (d.Shadows, d.ShadowDistance, d.Anisotropy, d.ViewDistanceMultiplier);
        (WaterReflections, WaterWaves) = (d.WaterReflections, d.WaterWaves);
        (HdrRendering, Exposure, RenderScale, Sharpness, ShadowResolution) = (d.HdrRendering, d.Exposure, d.RenderScale, d.Sharpness, d.ShadowResolution);
        (Antialiasing, AmbientOcclusion, OcclusionStrength) = (d.Antialiasing, d.AmbientOcclusion, d.OcclusionStrength);
        (Bloom, BloomStrength, SurfaceDetail) = (d.Bloom, d.BloomStrength, d.SurfaceDetail);
        (NightLights, NightLightStrength) = (d.NightLights, d.NightLightStrength);
        (PhysicalSky, SunRays, SunRayStrength) = (d.PhysicalSky, d.SunRays, d.SunRayStrength);
        (Weather, CloudQuality) = (d.Weather, d.CloudQuality);
        GraphicsTierChosen = false; // Chosen again for the graphics card.
        (ShowGrid, GridSpacing, GridColor) = (d.ShowGrid, d.GridSpacing, d.GridColor);
        (CollisionRadius, NavMeshRadius, EntityRadius, ProjectsFolder) = (d.CollisionRadius, d.NavMeshRadius, d.EntityRadius, d.ProjectsFolder);
    }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) is { } settings)
            {
                if (settings.CloudQuality < 0)
                    settings.CloudQuality = !settings.HdrRendering ? 1 : settings.ShadowResolution >= 4096 ? 3 : 2;
                return settings;
            }
        }
        catch (Exception)
        {
            // Corrupted file: start with defaults rather than refusing to start.
        }
        return new AppSettings { CloudQuality = 1 };
    }

    public void Save(string path)
    {
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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

// Model viewer: list shown, search, display options, and models opened per game ("v:name" vehicle, "o:name" object).
public sealed class ViewerSettings
{
    public int Filter { get; set; }
    public string Search { get; set; } = "";
    public int Shading { get; set; }
    public bool Edges { get; set; }
    public bool ShowBounds { get; set; }
    public bool ShowGround { get; set; } = true;
    public bool AutoRotate { get; set; }
    public int Background { get; set; }
    public bool LightFollowsCamera { get; set; } = true;
    public float LightAngle { get; set; } = 0.6f;
    public float LightHeight { get; set; } = 0.8f;
    public float FieldOfView { get; set; } = 45f;
    public float ListWidth { get; set; } = 230f;
    public float InspectorWidth { get; set; } = 340f;
    public bool InspectorOpen { get; set; } = true;
    public int Order { get; set; }
    public Dictionary<GameKind, List<string>> Recents { get; set; } = new();
    public Dictionary<GameKind, List<string>> Favorites { get; set; } = new();
}
