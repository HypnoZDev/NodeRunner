using System.Reflection;

namespace NodeRunner.App.Ui;

/// <summary>
/// Lucide icons (https://lucide.dev, ISC licence, see Assets/Fonts/LICENSE-lucide.txt), drawn as characters of
/// lucide.ttf merged into the interface font. Only the icons listed here are loaded: add a constant to use another
/// one (code points from font/codepoints.json of the lucide-static package, version 1.66.0).
/// </summary>
internal static class Icons
{
    public const string Activity = "\ue038";
    public const string ArrowDownToLine = "\ue455";
    public const string Box = "\ue061";
    public const string Boxes = "\ue2d0";
    public const string Camera = "\ue064";
    public const string Check = "\ue06c";
    public const string ChevronDown = "\ue06d";
    public const string ChevronLeft = "\ue06e";
    public const string ChevronRight = "\ue06f";
    public const string Car = "";
    public const string Clapperboard = "\ue29b";
    public const string Copy = "\ue09e";
    public const string Cpu = "\ue0a9";
    public const string Crosshair = "\ue0ac";
    public const string Ellipsis = "\ue0b6";
    public const string Eye = "\ue0ba";
    public const string EyeOff = "\ue0bb";
    public const string File = "\ue0c0";
    public const string FolderOpen = "\ue247";
    public const string Footprints = "\ue3b9";
    public const string Gauge = "\ue1bf";
    public const string GitBranch = "\ue0e2";
    public const string Globe = "\ue0e8";
    public const string Grid = "\ue0e9";
    public const string HardDrive = "\ue0ed";
    public const string House = "\ue0f5";
    public const string Info = "\ue0f9";
    public const string Layers = "\ue529";
    public const string ListTree = "\ue408";
    public const string LoaderCircle = "\ue10a";
    public const string Map = "\ue110";
    public const string MapPinned = "\ue53d";
    public const string Menu = "\ue115";
    public const string Monitor = "\ue11d";
    public const string Mountain = "\ue231";
    public const string MousePointer = "\ue1c3";
    public const string Move = "\ue121";
    public const string Package = "\ue129";
    public const string Redo = "\ue2a0";
    public const string RefreshCw = "\ue145";
    public const string Rotate = "\ue2ea";
    public const string Route = "\ue53e";
    public const string Ruler = "\ue14b";
    public const string Save = "\ue14d";
    public const string Scale = "\ue2eb";
    public const string ScanEye = "\ue536";
    public const string Search = "\ue151";
    public const string Settings = "\ue154";
    public const string SlidersHorizontal = "\ue29a";
    public const string Sun = "\ue178";
    public const string Sunrise = "\ue179";
    public const string Sunset = "\ue17a";
    public const string Moon = "\ue11e";
    public const string TableProperties = "\ue4db";
    public const string Trash = "\ue18e";
    public const string TreePine = "\ue2f4";
    public const string TriangleAlert = "\ue193";
    public const string Undo = "\ue2a1";
    public const string Users = "\ue1a4";
    public const string Video = "\ue1a5";
    public const string Waypoints = "\ue542";
    public const string Waves = "";
    public const string X ="\ue1b2";

    /// <summary>Code points of all the icons above, for the font atlas.</summary>
    public static IEnumerable<char> All() =>
        typeof(Icons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => ((string)f.GetRawConstantValue()!)[0]);
}
