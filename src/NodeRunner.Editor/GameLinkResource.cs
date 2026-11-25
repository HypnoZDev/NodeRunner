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
    public const int Version = 4;

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

    // server.cfg with the link's block (replaced when already there, added at the end otherwise). The map resources it
    // starts keep their place; the link may run "refresh" and "ensure" to restart them after an export.
    public static string AddToConfig(string config, IEnumerable<string>? maps = null)
    {
        string newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>
        {
            ConfigBegin,
            $"add_ace resource.{ResourceName} command.refresh allow",
            $"add_ace resource.{ResourceName} command.ensure allow",
            $"ensure {ResourceName}",
        };
        lines.AddRange((maps ?? ConfigMaps(config)).Distinct(StringComparer.Ordinal).Select(map => $"ensure {map}"));
        lines.Add(ConfigEnd);
        string block = string.Join(newline, lines);
        string without = RemoveFromConfig(config);
        if (without.Length > 0 && !without.EndsWith('\n'))
            without += newline;
        if (without.Length > 0)
            without += newline;
        return without + block + newline;
    }

    // Map resources started by the link's block.
    public static List<string> ConfigMaps(string config)
    {
        var maps = new List<string>();
        int begin = config.IndexOf(ConfigBegin, StringComparison.Ordinal);
        if (begin < 0)
            return maps;
        int end = config.IndexOf(ConfigEnd, begin, StringComparison.Ordinal);
        string block = config[begin..(end < 0 ? config.Length : end)];
        foreach (string line in block.Split('\n'))
        {
            string text = line.Trim();
            if (!text.StartsWith("ensure ", StringComparison.Ordinal))
                continue;
            string name = text[7..].Trim();
            if (name != ResourceName && FiveMResource.IsValidName(name))
                maps.Add(name);
        }
        return maps;
    }

    // The folder of a resource of the server (anywhere under resources/, [categories] included), or null.
    public static string? FindResource(string server, string name)
    {
        var budget = new Budget(4000);
        return Find(Path.Combine(server, ResourcesFolderName), 5);

        string? Find(string folder, int depth)
        {
            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            foreach (string child in children)
            {
                if (!budget.Take())
                    return null;
                string childName = Path.GetFileName(child);
                if (File.Exists(Path.Combine(child, FiveMResource.ManifestFileName)) || File.Exists(Path.Combine(child, "__resource.lua")))
                {
                    if (string.Equals(childName, name, StringComparison.OrdinalIgnoreCase))
                        return child;
                    continue;
                }
                if (depth > 1 && !childName.StartsWith('.') && Find(child, depth - 1) is { } found)
                    return found;
            }
            return null;
        }
    }

    // Where a map resource is exported on the server: where it already is, or next to the link resource.
    public static string MapFolder(string server, string name) =>
        FindResource(server, name) ?? Path.Combine(server, ResourcesFolderName, CategoryFolder, name);

    // Has the server start a map resource exported next to the link resource (one the mapper put elsewhere is left
    // to their own server.cfg).
    public static void StartWithServer(string server, string folder, string name)
    {
        string ours = Path.GetFullPath(Path.Combine(server, ResourcesFolderName, CategoryFolder)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(folder).StartsWith(ours, StringComparison.OrdinalIgnoreCase))
            return;
        string config = Path.Combine(server, ConfigFileName);
        string text = File.ReadAllText(config);
        var maps = ConfigMaps(text);
        if (maps.Contains(name, StringComparer.Ordinal))
            return;
        maps.Add(name);
        SafeFile.WriteAllText(config, AddToConfig(text, maps));
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

        -- Preview of the editor's changes: game objects hidden, local objects put in their place.
        local previewRev = 0
        local shown = {}

        local function hideKey(item)
            if not item.hm then
                return nil
            end
            return ('%s:%.3f:%.3f:%.3f'):format(tostring(item.hm), item.hx, item.hy, item.hz)
        end

        local function place(object, item)
            SetEntityCoordsNoOffset(object, item.x + 0.0, item.y + 0.0, item.z + 0.0, false, false, false)
            SetEntityQuaternion(object, item.qx + 0.0, item.qy + 0.0, item.qz + 0.0, item.qw + 0.0)
        end

        local function clear(entry)
            entry.cleared = true
            if entry.object and DoesEntityExist(entry.object) then
                DeleteObject(entry.object)
            end
            if entry.hide then
                RemoveModelHide(entry.hide.hx + 0.0, entry.hide.hy + 0.0, entry.hide.hz + 0.0, 0.1, entry.hide.hm, false)
            end
        end

        local function apply(item)
            local entry = { model = item.m, hideKey = hideKey(item), item = item }
            if item.hm then
                entry.hide = { hm = item.hm, hx = item.hx, hy = item.hy, hz = item.hz }
                CreateModelHide(item.hx + 0.0, item.hy + 0.0, item.hz + 0.0, 0.1, item.hm, true)
            end
            if item.m and IsModelInCdimage(item.m) then
                CreateThread(function()
                    RequestModel(item.m)
                    local waited = 0
                    while not HasModelLoaded(item.m) and waited < 5000 do
                        Wait(20)
                        waited = waited + 20
                    end
                    if entry.cleared or not HasModelLoaded(item.m) then
                        return
                    end
                    local latest = entry.item
                    local object = CreateObjectNoOffset(item.m, latest.x + 0.0, latest.y + 0.0, latest.z + 0.0, false, false, false)
                    FreezeEntityPosition(object, true)
                    place(object, latest)
                    SetModelAsNoLongerNeeded(item.m)
                    entry.object = object
                end)
            end
            return entry
        end

        local function clearAll()
            for key, entry in pairs(shown) do
                clear(entry)
                shown[key] = nil
            end
            previewRev = 0
        end

        RegisterNetEvent('noderunner-link:preview', function(c)
            if type(c) ~= 'table' or type(c.items) ~= 'table' then
                return
            end
            previewRev = tonumber(c.rev) or 0
            local wanted = {}
            for _, item in ipairs(c.items) do
                wanted[item.k] = item
            end
            for key, entry in pairs(shown) do
                local item = wanted[key]
                if not item or item.m ~= entry.model or hideKey(item) ~= entry.hideKey then
                    clear(entry)
                    shown[key] = nil
                else
                    entry.item = item
                    if entry.object and DoesEntityExist(entry.object) then
                        place(entry.object, item)
                    end
                end
            end
            for key, item in pairs(wanted) do
                if not shown[key] then
                    shown[key] = apply(item)
                end
            end
        end)

        AddEventHandler('onResourceStop', function(name)
            if name == GetCurrentResourceName() then
                clearAll()
            end
        end)

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
                    cx = cam.x, cy = cam.y, cz = cam.z, rx = rot.x, ry = rot.y, rz = rot.z, pr = previewRev,
                })
                Wait(200)
            end
        end)

        RegisterNetEvent('noderunner-link:status', function(connected)
            if connected then
                notify('~b~NodeRunner Studio~s~ : connecté / connected')
            else
                clearAll()
                notify('~b~NodeRunner Studio~s~ : déconnecté / disconnected')
            end
        end)

        RegisterNetEvent('noderunner-link:reloaded', function(resource)
            notify('~b~NodeRunner Studio~s~ : ' .. tostring(resource) .. ' rechargée / reloaded')
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
        local allowed = { teleport = true, preview = true }

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
                                if command.type == 'restart' and type(command.resource) == 'string'
                                    and command.resource:match('^[%w_%-]+$') then
                                    ExecuteCommand('refresh')
                                    ExecuteCommand('ensure ' .. command.resource)
                                    TriggerClientEvent('noderunner-link:reloaded', -1, command.resource)
                                end
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
