using System.Diagnostics;
using System.Text;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Installs single-player packs in the game's "mods" folder, as mod managers do: the game's update.rpf is copied there
// once (tables in clear), the pack is added to its dlclist.xml and its dlc.rpf goes to mods\update\x64\dlcpacks\<name>\.
// The game's own files are never written.
internal static class SoloPackInstaller
{
    public enum Support { Ready, NoModsLoader, NoAsiLoader, Enhanced, NoGame }

    // The mods folder is read by OpenIV.asi, itself loaded by an ASI loader (a DLL the game loads by its name).
    public const string ModsLoader = "OpenIV.asi";

    public const string OpenIvUrl = "https://openiv.com";

    // Names an ASI loader takes in the game folder (OpenIV's ASI Manager installs dinput8.dll); the game ships none.
    private static readonly string[] AsiLoaderNames = ["dinput8.dll", "version.dll", "winmm.dll", "dsound.dll", "xinput1_3.dll"];

    private const long SpareSpace = 256L * 1024 * 1024;

    public static Support Check(GameInstallation? game)
    {
        if (game is null)
            return Support.NoGame;
        if (game.Edition == GameEdition.Enhanced)
            return Support.Enhanced;
        if (!HasModsLoader(game))
            return Support.NoModsLoader;
        return AsiLoader(game) is null ? Support.NoAsiLoader : Support.Ready;
    }

    public static bool HasModsLoader(GameInstallation game) => File.Exists(Path.Combine(game.FolderPath, ModsLoader));

    // File name of the ASI loader found, or null.
    public static string? AsiLoader(GameInstallation game) =>
        AsiLoaderNames.FirstOrDefault(name => File.Exists(Path.Combine(game.FolderPath, name)));

    public static string ModsUpdate(GameInstallation game) => Path.Combine(game.FolderPath, "mods", "update", "update.rpf");

    public static string PackFolder(GameInstallation game, string name) =>
        Path.Combine(game.FolderPath, "mods", "update", "x64", "dlcpacks", name);

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("GTA5");
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    // Packs made by NodeRunner that the mods folder's dlclist.xml loads.
    public static List<string> Installed(GameInstallation game)
    {
        var result = new List<string>();
        string modsUpdate = ModsUpdate(game);
        if (!File.Exists(modsUpdate) || RpfEditor.IsEncrypted(modsUpdate))
            return result;
        byte[]? list;
        using (var editor = new RpfEditor(modsUpdate, writable: false))
            list = editor.Read(DlcList.PathInUpdate);
        if (list is null)
            return result;
        foreach (var entry in DlcList.Entries(list))
        {
            string name = entry.Trim().TrimEnd('/').Split('/', ':').Last();
            if (name.Length == 0 || !entry.StartsWith("dlcpacks:", StringComparison.OrdinalIgnoreCase))
                continue;
            string pack = Path.Combine(PackFolder(game, name), SoloPack.ArchiveName);
            if (!File.Exists(pack))
                continue;
            try
            {
                using var archive = new RpfEditor(pack, writable: false);
                if (archive.Read(SoloPack.SetupFileName) is { } setup && SoloPack.IsOurSetup(Encoding.UTF8.GetString(setup)))
                    result.Add(name);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Someone else's pack (encrypted or damaged): not listed.
            }
        }
        return result;
    }

    // The game's packs missing from the mods folder's list: its copy of update.rpf dates from before a game update.
    public static bool IsModsCopyOutdated(GameInstallation game, byte[]? gameList)
    {
        string modsUpdate = ModsUpdate(game);
        if (gameList is null || !File.Exists(modsUpdate) || RpfEditor.IsEncrypted(modsUpdate))
            return false;
        using var editor = new RpfEditor(modsUpdate, writable: false);
        if (editor.Read(DlcList.PathInUpdate) is not { } modsList)
            return false;
        var mods = DlcList.Entries(modsList).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DlcList.Entries(gameList).Any(e => !mods.Contains(Normalize(e)));
    }

    private static string Normalize(string entry) => entry.Trim().TrimEnd('/');

    public static void Install(GameInstallation game, string dlcRpf, string name, IProgress<string> progress, CancellationToken cancel = default)
    {
        if (IsGameRunning())
            throw new InvalidOperationException(L("Ferme GTA V avant d'installer le pack."));
        string modsUpdate = ModsUpdate(game);
        if (!File.Exists(modsUpdate) || RpfEditor.IsEncrypted(modsUpdate))
            PrepareModsUpdate(game, modsUpdate, progress, cancel);

        // The pack is in place before the list names it.
        progress.Report(L("Copie du pack..."));
        string folder = PackFolder(game, name);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, SoloPack.ArchiveName);
        File.Copy(dlcRpf, target + ".tmp", overwrite: true);
        File.Move(target + ".tmp", target, overwrite: true);

        progress.Report(L("Ajout du pack à dlclist.xml..."));
        using var editor = new RpfEditor(modsUpdate);
        var list = editor.Read(DlcList.PathInUpdate)
            ?? throw new InvalidDataException(L("dlclist.xml introuvable dans mods\\update\\update.rpf."));
        if (DlcList.Add(list, name) is { } updated)
            editor.Replace(DlcList.PathInUpdate, updated);
    }

    public static void Uninstall(GameInstallation game, string name)
    {
        if (IsGameRunning())
            throw new InvalidOperationException(L("Ferme GTA V avant de désinstaller le pack."));
        string modsUpdate = ModsUpdate(game);
        if (File.Exists(modsUpdate) && !RpfEditor.IsEncrypted(modsUpdate))
        {
            using var editor = new RpfEditor(modsUpdate);
            if (editor.Read(DlcList.PathInUpdate) is { } list && DlcList.Remove(list, name) is { } updated)
                editor.Replace(DlcList.PathInUpdate, updated);
        }
        string folder = PackFolder(game, name);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // A copy of the game's update.rpf in the mods folder, with tables in clear so that its list can be changed. An
    // encrypted copy already there (made by another tool) is converted.
    private static void PrepareModsUpdate(GameInstallation game, string modsUpdate, IProgress<string> progress, CancellationToken cancel)
    {
        string source = File.Exists(modsUpdate) ? modsUpdate : Path.Combine(game.FolderPath, "update", "update.rpf");
        if (!File.Exists(source))
            throw new FileNotFoundException(L("update\\update.rpf introuvable dans le dossier du jeu."));
        long size = new FileInfo(source).Length;
        string? root = Path.GetPathRoot(Path.GetFullPath(modsUpdate));
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < size + SpareSpace)
            throw new IOException(L($"Pas assez de place sur le disque {root} : il faut {(size + SpareSpace) / (1024 * 1024 * 1024.0):0.0} Go libres pour copier update.rpf dans mods."));

        progress.Report(L("Lecture des clés du jeu..."));
        var keys = new GameArchiveKeys().Load(game, null);
        Directory.CreateDirectory(Path.GetDirectoryName(modsUpdate)!);
        string temp = modsUpdate + ".tmp";
        try
        {
            int last = -1;
            RpfEditor.CopyAsOpen(source, temp, keys, new Progress(p =>
            {
                int percent = (int)(p * 100);
                if (percent != last)
                {
                    last = percent;
                    progress.Report(L($"Copie de update.rpf dans mods ({percent} %, une seule fois)..."));
                }
            }), cancel);
            File.Move(temp, modsUpdate, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    // Reports on the calling thread (IProgress<T> of the framework posts to the UI thread, which is not wanted here).
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
