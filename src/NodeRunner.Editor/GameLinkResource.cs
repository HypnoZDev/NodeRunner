using System.Text;
using System.Text.RegularExpressions;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

// The "noderunner-link" resource that NodeRunner installs on the mapper's own FiveM server (server.cfg + resources/)
// to talk to the editor while the game runs.
public static class GameLinkResource
{
    public const string ResourceName = "noderunner-link";
    public const string CategoryFolder = "[noderunner]";
    public const int Version = 2;

    public const string ConfigFileName = "server.cfg";
    public const string ResourcesFolderName = "resources";
    public const string ConfigBackupSuffix = ".noderunner-backup";

    private const string ConfigBegin = "# >>> noderunner-link (NodeRunner Studio)";
    private const string ConfigEnd = "# <<< noderunner-link";
    private const string ManifestMarker = "-- noderunner-link version ";

    public enum State { NotInstalled, Installed, Outdated }

    public static bool IsServerFolder(string folder) =>
        File.Exists(Path.Combine(folder, ConfigFileName)) && Directory.Exists(Path.Combine(folder, ResourcesFolderName));

    public static string ResourceFolder(string server) =>
        Path.Combine(server, ResourcesFolderName, CategoryFolder, ResourceName);

    // The server folder from a folder inside it (a resource, resources/) or right above it (txData, server-data...).
    public static string? FindServerFolder(string start)
    {
        try
        {
            var folder = new DirectoryInfo(Path.GetFullPath(start));
            for (var up = folder; up is not null; up = up.Parent)
                if (IsServerFolder(up.FullName))
                    return up.FullName;
            return Below(folder.FullName, 2, new Budget(400)).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // FiveM server folders found in the usual places (a few levels under each root), without walking the whole disk;
    // the most recently used first. RedM servers are left out.
    public static List<string> Search(IEnumerable<string> roots, int depth = 5, int maxFolders = 8000)
    {
        var budget = new Budget(maxFolders);
        var found = new List<string>();
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string server in Below(root, depth, budget))
                if (!found.Contains(server, StringComparer.OrdinalIgnoreCase) && !IsRedM(server))
                    found.Add(server);
        return [.. found.OrderByDescending(LastUsed)];
    }

    private static bool IsRedM(string server) =>
        Regex.IsMatch(SafeRead(Path.Combine(server, ConfigFileName)) ?? "", @"^\s*sets?\s+gamename\s+rdr3", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static DateTime LastUsed(string server)
    {
        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(server, ConfigFileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private sealed class Budget(int left)
    {
        public bool Take() => left-- > 0;
    }

    private static readonly string[] SkippedFolders =
        ["Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information",
         "node_modules", ".git", "stream", "cache", "Steam", "SteamLibrary", "Epic Games", "artifacts", "citizen", "resources"];

    private static IEnumerable<string> Below(string folder, int depth, Budget budget)
    {
        if (depth <= 0)
            yield break;
        string[] children;
        try
        {
            children = Directory.GetDirectories(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || !budget.Take())
                continue;
            if (IsServerFolder(child))
            {
                yield return child;
                continue;
            }
            foreach (string server in Below(child, depth - 1, budget))
                yield return server;
        }
    }

    public static State StateOf(string server)
    {
        int? installed = InstalledVersion(server);
        bool configured = File.Exists(Path.Combine(server, ConfigFileName))
            && HasConfigBlock(SafeRead(Path.Combine(server, ConfigFileName)) ?? "");
        if (installed is null || !configured)
            return State.NotInstalled;
        return installed < Version ? State.Outdated : State.Installed;
    }

    public static int? InstalledVersion(string server)
    {
        string? manifest = SafeRead(Path.Combine(ResourceFolder(server), FiveMResource.ManifestFileName));
        if (manifest is null)
            return null;
        var match = Regex.Match(manifest, Regex.Escape(ManifestMarker) + @"(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int version) ? version : null;
    }

    public static bool HasConfigBlock(string config) => config.Contains(ConfigBegin, StringComparison.Ordinal);

    // server.cfg with the link's block (replaced when already there, added at the end otherwise).
    public static string AddToConfig(string config)
    {
        string newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string block = string.Join(newline, ConfigBegin, $"ensure {ResourceName}", ConfigEnd);
        string without = RemoveFromConfig(config);
        if (without.Length > 0 && !without.EndsWith('\n'))
            without += newline;
        if (without.Length > 0)
            without += newline;
        return without + block + newline;
    }

    // server.cfg without the link's block (and the blank line put before it), the rest untouched.
    public static string RemoveFromConfig(string config)
    {
        int begin = config.IndexOf(ConfigBegin, StringComparison.Ordinal);
        if (begin < 0)
            return config;
        int end = config.IndexOf(ConfigEnd, begin, StringComparison.Ordinal);
        end = end < 0 ? config.Length : end + ConfigEnd.Length;
        while (end < config.Length && config[end] is '\r' or '\n')
            end++;
        string before = config[..begin];
        string after = config[end..];
        if (after.Length > 0)
            return before + after;
        string trimmed = before.TrimEnd('\r', '\n');
        string newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return trimmed.Length == 0 ? "" : trimmed + newline;
    }

    // Writes (or updates) the resource and the server.cfg block; server.cfg is copied aside once beforehand.
    public static void Install(string server)
    {
        string folder = ResourceFolder(server);
        Directory.CreateDirectory(folder);
        foreach (var (name, text) in Files())
            SafeFile.WriteAllText(Path.Combine(folder, name), text);

        string config = Path.Combine(server, ConfigFileName);
        string text0 = File.ReadAllText(config);
        string backup = config + ConfigBackupSuffix;
        if (!HasConfigBlock(text0) && !File.Exists(backup))
            File.Copy(config, backup);
        string updated = AddToConfig(text0);
        if (updated != text0)
            SafeFile.WriteAllText(config, updated);
    }

    // Removes only the files NodeRunner wrote, then the empty folders, and the server.cfg block.
    public static void Uninstall(string server)
    {
        string folder = ResourceFolder(server);
        if (InstalledVersion(server) is not null)
        {
            foreach (var (name, _) in Files())
                File.Delete(Path.Combine(folder, name));
            DeleteIfEmpty(folder);
            DeleteIfEmpty(Path.GetDirectoryName(folder)!);
        }
        string config = Path.Combine(server, ConfigFileName);
        if (File.Exists(config))
        {
            string text = File.ReadAllText(config);
            string updated = RemoveFromConfig(text);
            if (updated != text)
                SafeFile.WriteAllText(config, updated);
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Name, string Text)> Files() =>
    [
        (FiveMResource.ManifestFileName, Manifest()),
        ("client.lua", Client),
        ("server.lua", Server.Replace("{PORT}", GameLinkHost.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture))),
    ];

    private static string Manifest()
    {
        var text = new StringBuilder();
        text.Append(ManifestMarker).Append(Version).Append('\n');
        text.Append(FiveMResource.GeneratedMarker).Append(" Studio: live link with the editor, safe to delete.\n");
        text.Append("fx_version 'cerulean'\n");
        text.Append("game 'gta5'\n");
        text.Append("author 'Inside Rockstar'\n");
        text.Append("description 'NodeRunner Studio live link'\n");
        text.Append("version '").Append(Version).Append("'\n\n");
        text.Append("client_script 'client.lua'\n");
        text.Append("server_script 'server.lua'\n");
        return text.ToString();
    }

    private const string Client = """
        -- NodeRunner Studio live link (client side): sends where the player and the camera are, runs the editor's commands.

        local function notify(text)
            BeginTextCommandThefeedPost('STRING')
            AddTextComponentSubstringPlayerName(text)
            EndTextCommandThefeedPostTicker(false, false)
        end

        CreateThread(function()
            while not NetworkIsPlayerActive(PlayerId()) do
                Wait(500)
            end
            Wait(2000)
            notify('~b~NodeRunner Studio~s~ : lien avec l\'éditeur prêt / editor link ready')
            while true do
                local ped = PlayerPedId()
                local pos = GetEntityCoords(ped)
                local cam = GetFinalRenderedCamCoord()
                local rot = GetFinalRenderedCamRot(2)
                TriggerServerEvent('noderunner-link:state', {
                    x = pos.x, y = pos.y, z = pos.z, h = GetEntityHeading(ped),
                    cx = cam.x, cy = cam.y, cz = cam.z, rx = rot.x, ry = rot.y, rz = rot.z,
                })
                Wait(200)
            end
        end)

        RegisterNetEvent('noderunner-link:status', function(connected)
            if connected then
                notify('~b~NodeRunner Studio~s~ : connecté / connected')
            else
                notify('~b~NodeRunner Studio~s~ : déconnecté / disconnected')
            end
        end)

        RegisterNetEvent('noderunner-link:teleport', function(c)
            local ped = PlayerPedId()
            local target = ped
            if IsPedInAnyVehicle(ped, false) then
                target = GetVehiclePedIsIn(ped, false)
            end
            local x, y, z = c.x + 0.0, c.y + 0.0, c.z + 0.0
            FreezeEntityPosition(target, true)
            SetEntityCoordsNoOffset(target, x, y, z, false, false, false)
            local ground = z
            for _ = 1, 60 do
                RequestCollisionAtCoord(x, y, z)
                local found, groundZ = GetGroundZFor_3dCoord(x, y, z + 1.0, false)
                if found then
                    ground = groundZ
                    break
                end
                Wait(50)
            end
            SetEntityCoordsNoOffset(target, x, y, ground, false, false, false)
            SetEntityHeading(target, c.h + 0.0)
            FreezeEntityPosition(target, false)
            SetGameplayCamRelativeHeading(0.0)
        end)

        """;

    private const string Server = """
        -- NodeRunner Studio live link (server side): polls the editor running on this PC.

        local url = ('http://127.0.0.1:%d/link'):format(GetConvarInt('noderunner_port', {PORT}))
        local states = {}
        local connected = false
        local allowed = { teleport = true }

        RegisterNetEvent('noderunner-link:state', function(state)
            local source = source
            if type(state) ~= 'table' then
                return
            end
            state.id = source
            state.name = GetPlayerName(source)
            states[source] = state
        end)

        AddEventHandler('playerDropped', function()
            states[source] = nil
        end)

        CreateThread(function()
            while true do
                local players = {}
                for _, state in pairs(states) do
                    players[#players + 1] = state
                end
                local done = false
                PerformHttpRequest(url, function(status, body)
                    local ok = status == 200
                    if ok ~= connected then
                        connected = ok
                        TriggerClientEvent('noderunner-link:status', -1, ok)
                    end
                    if ok and body then
                        local reply = json.decode(body)
                        if type(reply) == 'table' and type(reply.commands) == 'table' then
                            for _, command in ipairs(reply.commands) do
                                if allowed[command.type] then
                                    TriggerClientEvent('noderunner-link:' .. command.type, tonumber(command.player) or -1, command)
                                end
                            end
                        end
                    end
                    done = true
                end, 'POST', json.encode({ v = 2, players = players }), { ['Content-Type'] = 'application/json' })
                local waited = 0
                while not done and waited < 3000 do
                    Wait(20)
                    waited = waited + 20
                end
                if connected then
                    Wait(100)
                else
                    Wait(2000)
                end
            end
        end)

        """;
}
