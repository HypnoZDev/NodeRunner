using NodeRunner.Data.Dlc;

namespace NodeRunner.Data.FileSystem;

public enum MapVersion
{
    SinglePlayer,

    // Multiplayer map, as used by FiveM: story mode plus the map changes of the DLC packs (GROUP_MAP), e.g. the
    // rebuilt downtown of the Heists update.
    Multiplayer,
}

// Which map to show: the version, and the last DLC pack applied (null for all of them).
public readonly record struct MapSelection(MapVersion Version, string? LastPack = null);

// A DLC pack found in the installation: folder name (e.g. "mpheist") and name given by the pack.
public sealed record DlcPackInfo(string Name, string DisplayName);

// Decides, like the game, which archives (.rpf) are in use, and so which version of each file counts. <list
// type="number"> <item>Base layer: every archive nested in the base archives (x64*.rpf, common.rpf), then those of
// update.rpf, which replace the base ones of the same path (e.g.
// "x64/levels/gta5/_citye/downtown_01/dt1_lod.rpf").</item> <item>DLC packs, listed by
// update.rpf\common\data\dlclist.xml and mounted by order (setup2.xml): each declares archives (content.xml).
public sealed class GameContent
{
    private const string DlcListPath = @"update\update.rpf\common\data\dlclist.xml";
    private const string UpdateRoot = @"update\update.rpf";
    private const string DlcPatchFolder = @"update\update.rpf\dlc_patch\";

    private static readonly string[] StartupGroups =
    [
        "GROUP_EARLY_ON", "GROUP_TITLEUPDATE_STARTUP", "GROUP_UPDATE_STREAMING", "GROUP_UPDATE_TEXT",
        "GROUP_UPDATE_DLC_PATCH", "GROUP_UPDATE_DLC_METADATA", "GROUP_UPDATE_WEAPON_PATCH", "GROUP_POST_DLC_PATCH",
        "GROUP_STARTUP",
    ];

    private const string MapGroup = "GROUP_MAP";

    // Virtual archive holding the files of the open project (LooseFileOverlay), activated after every pack.
    public const string LooseFilesRoot = "noderunner_project.rpf";

    private readonly IGameFileSystem _files;
    private readonly HashSet<string> _archives = new(StringComparer.OrdinalIgnoreCase);

    // Active archives by key: the platform path ("x64/levels/...") for base archives,
    // the device path ("dlcmpheist:/x64/levels/...") for archives of the packs.
    private readonly Dictionary<string, (string Physical, long Sequence)> _active = new(StringComparer.OrdinalIgnoreCase);

    // Archives whose loose files count (not nested in another active archive): base roots, update.rpf, packs.
    private readonly Dictionary<string, long> _roots = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Pack> _packsByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Pack> _packsByName = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    private GameContent(IGameFileSystem files)
    {
        _files = files;
        foreach (var archive in files.EnumerateArchives())
            _archives.Add(archive);
    }

    public MapSelection Selection { get; private init; }
    public MapVersion Version => Selection.Version;

    public IReadOnlyList<DlcPackInfo> AllPacks { get; private set; } = [];

    public IReadOnlyList<string> Packs { get; private set; } = [];

    public int ActiveArchiveCount => _active.Count;

    public List<string> Errors { get; } = new();

    public List<string> MissingArchives { get; } = new();

    public static GameContent Load(IGameFileSystem files, IDlcManifestReader reader, MapSelection selection)
    {
        var content = new GameContent(files) { Selection = selection };
        content.AddBaseLayer();
        content.ApplyDlcPacks(reader, selection);
        if (content._archives.Contains(LooseFilesRoot))
            content._roots[LooseFilesRoot] = content._sequence++;
        return content;
    }

    // The path of every file with this extension that the game uses, one per file name (the version from the archive
    // activated last).
    public List<string> EnumerateFiles(string extension, bool withInactiveFallback = false)
    {
        var chosen = new Dictionary<string, (string Path, long Sequence)>(StringComparer.OrdinalIgnoreCase);
        var activePhysical = ActivePhysicalSequences();
        foreach (var path in _files.EnumerateFiles(extension))
        {
            long? active = SequenceOf(path, activePhysical);
            if (active is null && (!withInactiveFallback || IsInUserMods(path)))
                continue;
            long sequence = active ?? -1;
            string name = Path.GetFileName(path);
            if (!chosen.TryGetValue(name, out var existing) || sequence >= existing.Sequence)
                chosen[name] = (path, sequence);
        }
        return chosen.Values.Select(c => c.Path).ToList();
    }

    private static bool IsInUserMods(string path) => path.StartsWith(@"mods", StringComparison.OrdinalIgnoreCase);

    // Every version of the files with this extension found in active archives, oldest activation first (for files
    // that add up rather than replace each other, like the gtxd texture relationships).
    public List<string> EnumerateAllVersions(string extension) =>
        ActiveFiles(extension).OrderBy(f => f.Sequence).Select(f => f.Path).ToList();

    public byte[]? ReadFile(string path) => _files.ReadFile(path);

    public ResourceHeader? GetResourceHeader(string path) => _files.GetResourceHeader(path);

    private IEnumerable<(string Path, long Sequence)> ActiveFiles(string extension)
    {
        var activePhysical = ActivePhysicalSequences();
        foreach (var path in _files.EnumerateFiles(extension))
        {
            if (SequenceOf(path, activePhysical) is { } sequence)
                yield return (path, sequence);
        }
    }

    private long? SequenceOf(string path, Dictionary<string, long> activePhysical)
    {
        int end = path.LastIndexOf(".rpf\\", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;
        string archive = path[..(end + 4)];
        if (activePhysical.TryGetValue(archive, out long sequence))
            return sequence;
        if (!_roots.TryGetValue(archive, out sequence))
            return null;

        // Loose files of update.rpf\dlc_patch\<pack>\ belong to that pack (and win over its own).
        if (archive.Equals(UpdateRoot, StringComparison.OrdinalIgnoreCase)
            && path.StartsWith(DlcPatchFolder, StringComparison.OrdinalIgnoreCase))
        {
            string pack = path[DlcPatchFolder.Length..].Split('\\')[0];
            return _packsByName.TryGetValue(pack, out var p) && _roots.TryGetValue(p.Root, out long packSequence)
                ? packSequence
                : null;
        }
        return sequence;
    }

    private Dictionary<string, long> ActivePhysicalSequences()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (physical, sequence) in _active.Values)
            result[physical] = sequence;
        return result;
    }

    private void AddBaseLayer()
    {
        // Base archives first, then update.rpf's, so that the update replaces the base.
        var nested = _archives.Where(IsBaseArchive).ToList();
        foreach (var root in _archives.Where(a => !a.Contains('\\') || a.Equals(UpdateRoot, StringComparison.OrdinalIgnoreCase)))
            _roots[root] = root.Equals(UpdateRoot, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _sequence = 2;
        foreach (var archive in nested.Where(a => !IsUpdate(a)))
            _active[PlatformPath(archive)] = (archive, _sequence);
        _sequence++;
        foreach (var archive in nested.Where(IsUpdate))
            _active[PlatformPath(archive)] = (archive, _sequence);
        _sequence++;
    }

    private void ApplyDlcPacks(IDlcManifestReader reader, MapSelection selection)
    {
        var listData = _files.ReadFile(DlcListPath);
        if (listData is null)
        {
            Errors.Add("dlclist.xml introuvable : packs DLC ignorés.");
            return;
        }

        var packs = new List<Pack>();
        IReadOnlyList<string> names;
        try
        {
            names = reader.ReadPackList(listData);
        }
        catch (InvalidDataException ex)
        {
            Errors.Add($"dlclist.xml : {ex.Message}");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (seen.Add(name) && LoadPack(reader, name) is { } pack)
                packs.Add(pack);
        }

        // OrderBy is stable: packs of equal order keep the order of dlclist.xml.
        packs = packs.OrderBy(p => p.Setup.Order).ToList();
        AllPacks = packs.Select(p => new DlcPackInfo(p.Name, p.Setup.Name ?? p.Name)).ToList();

        // Only the packs up to the chosen one exist for the rest of the process.
        int last = selection.LastPack is { } lastPack
            ? packs.FindIndex(p => p.Name.Equals(lastPack, StringComparison.OrdinalIgnoreCase))
            : -1;
        if (last >= 0)
            packs = packs.Take(last + 1).ToList();
        Packs = packs.Select(p => p.Name).ToList();
        foreach (var pack in packs)
        {
            _packsByName[pack.Name] = pack;
            _packsByDevice[pack.Setup.DeviceName] = pack;
        }

        foreach (var pack in packs)
        {
            foreach (var root in pack.Roots(this))
                _roots[root] = _sequence;
            _sequence++;
            foreach (var file in pack.Content.DataFiles)
            {
                if (!file.Disabled && file.FileType.Equals("RPF_FILE", StringComparison.OrdinalIgnoreCase))
                    Enable(file.FileName, pack);
            }
        }

        var groups = selection.Version == MapVersion.Multiplayer ? [.. StartupGroups, MapGroup] : StartupGroups;
        foreach (var group in groups)
        {
            foreach (var pack in packs)
            {
                foreach (var name in pack.Setup.ChangeSetGroups.GetValueOrDefault(group) ?? [])
                {
                    if (pack.Content.ChangeSets.TryGetValue(name, out var changeSet))
                        Apply(changeSet, pack);
                }
            }
        }
    }

    private Pack? LoadPack(IDlcManifestReader reader, string name)
    {
        string? root = _archives.FirstOrDefault(a => a.EndsWith($@"\dlcpacks\{name}\dlc.rpf", StringComparison.OrdinalIgnoreCase));
        if (root is null)
            return null; // Listed but not installed (e.g. removed packs): the game skips it too.

        try
        {
            var setupData = ReadPackFile(name, root, "setup2.xml");
            if (setupData is null)
                return null;
            var setup = reader.ReadSetup(setupData);
            var contentData = ReadPackFile(name, root, setup.ContentFile);
            var content = contentData is null ? new DlcContent() : reader.ReadContent(contentData);
            return new Pack(name, root, setup, content);
        }
        catch (InvalidDataException ex)
        {
            Errors.Add($"DLC {name} : {ex.Message}");
            return null;
        }
    }

    private byte[]? ReadPackFile(string packName, string root, string relativePath)
    {
        string patched = $@"{DlcPatchFolder}{packName}\{relativePath}";
        return _files.FileExists(patched) ? _files.ReadFile(patched) : _files.ReadFile($@"{root}\{relativePath}");
    }

    private void Apply(DlcChangeSet changeSet, Pack pack)
    {
        foreach (var file in changeSet.FilesToInvalidate)
            Invalidate(file, pack);
        foreach (var file in changeSet.FilesToDisable)
            Disable(file, pack);
        foreach (var file in changeSet.FilesToEnable)
            Enable(file, pack);
    }

    private void Enable(string fileName, Pack currentPack)
    {
        var (device, relative) = Split(fileName);
        if (!relative.EndsWith(".rpf", StringComparison.Ordinal))
            return; // Single files (e.g. .meta) are not tracked here.
        var pack = _packsByDevice.GetValueOrDefault(device);
        if (pack is null)
            return; // Base game paths are already active.
        if (ResolveArchive(pack, relative) is not { } physical)
        {
            MissingArchives.Add(fileName); // Declared but not installed.
            return;
        }

        _active[$"{device}:/{relative}"] = (physical, _sequence++);
    }

    // Removes every active version of an archive: the base one and those that packs added over it (e.g. the Heists
    // version of downtown replaces the base one and the patchday27ng additions).
    private void Invalidate(string fileName, Pack currentPack)
    {
        var (_, relative) = Split(fileName);
        if (!relative.EndsWith(".rpf", StringComparison.Ordinal))
            return; // e.g. "dt1_19_interior_v_policehub_milo_.interior": interiors are not drawn yet.
        _active.Remove(relative);
        string suffix = ":/" + relative;
        foreach (var key in _active.Keys.Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList())
            _active.Remove(key);
    }

    private void Disable(string fileName, Pack currentPack)
    {
        var (device, relative) = Split(fileName);
        if (!_packsByDevice.TryGetValue(device, out var pack))
            return;
        _active.Remove($"{device}:/{relative}");
        if (_active.TryGetValue(relative, out var entry) && pack.Owns(entry.Physical))
            _active.Remove(relative);
    }

    // Physical path of an archive of a pack: its fixed version in dlc_patch, else dlc.rpf, else a sub-pack.
    private string? ResolveArchive(Pack pack, string relative)
    {
        string inner = relative.Replace('/', '\\');
        string patched = $@"{DlcPatchFolder}{pack.Name}\{inner}";
        if (_archives.Contains(patched))
            return patched;
        foreach (var root in pack.Roots(this))
        {
            string candidate = $@"{root}\{inner}";
            if (_archives.Contains(candidate))
                return candidate;
        }
        return null;
    }

    // "dlcMPHeist:/%PLATFORM%/levels/x.rpf" gives ("dlcmpheist", "x64/levels/x.rpf").
    internal static (string Device, string Relative) Split(string fileName)
    {
        string path = fileName.Trim().Replace('\\', '/').ToLowerInvariant().Replace("%platform%", "x64");
        int colon = path.IndexOf(":/", StringComparison.Ordinal);
        if (colon < 0)
            return ("", path);
        string device = path[..colon];
        string relative = path[(colon + 2)..];
        if (device == "platform")
            relative = "x64/" + relative;
        else if (device == "common")
            relative = "common/" + relative;
        return (device, relative);
    }

    // "x64i.rpf\levels\gta5\a.rpf" or "update\update.rpf\x64\levels\gta5\a.rpf" gives "x64/levels/gta5/a.rpf".
    internal static string PlatformPath(string archive)
    {
        string path = archive.Replace('\\', '/').ToLowerInvariant();
        if (path.StartsWith("update/update.rpf/", StringComparison.Ordinal))
            return path["update/update.rpf/".Length..];
        int slash = path.IndexOf('/');
        string first = path[..slash];
        string rest = path[(slash + 1)..];
        return first.StartsWith("x64", StringComparison.Ordinal) ? "x64/" + rest : first.Replace(".rpf", "") + "/" + rest;
    }

    private static bool IsBaseArchive(string archive) =>
        archive.Contains('\\')
        && !archive.Equals(UpdateRoot, StringComparison.OrdinalIgnoreCase)
        && !archive.Contains(@"\dlcpacks\", StringComparison.OrdinalIgnoreCase)
        && !archive.Contains(@"\dlc_patch\", StringComparison.OrdinalIgnoreCase)
        && !archive.StartsWith(@"update\x64\", StringComparison.OrdinalIgnoreCase)
        && (!archive.StartsWith("update", StringComparison.OrdinalIgnoreCase) || archive.StartsWith(UpdateRoot + "\\", StringComparison.OrdinalIgnoreCase));

    private static bool IsUpdate(string archive) => archive.StartsWith(UpdateRoot + "\\", StringComparison.OrdinalIgnoreCase);

    private sealed record Pack(string Name, string Root, DlcSetup Setup, DlcContent Content)
    {
        public IEnumerable<string> Roots(GameContent content)
        {
            yield return Root;
            string folder = Root[..^"dlc.rpf".Length];
            for (int i = 1; i <= Setup.SubPackCount; i++)
            {
                string subPack = $"{folder}dlc{i}.rpf";
                if (content._archives.Contains(subPack))
                    yield return subPack;
            }
        }

        public bool Owns(string physical) =>
            physical.StartsWith(Root[..^"dlc.rpf".Length], StringComparison.OrdinalIgnoreCase)
            || physical.StartsWith($@"{DlcPatchFolder}{Name}\", StringComparison.OrdinalIgnoreCase);
    }
}
