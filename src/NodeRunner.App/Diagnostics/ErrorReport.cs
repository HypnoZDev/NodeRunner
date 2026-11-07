using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Diagnostics;

// Text report a user copies and sends when something goes wrong: program, system, game installation, how the archive
// keys were found, errors and unreadable files.
internal static class ErrorReport
{
    public sealed record ProblemList(string Title, IReadOnlyCollection<string> Items, string? Note);

    // Game files that could not be read (expected on some installations, listed in the State panel).
    public static List<ProblemList> Problems(AppState state, ModelLayer? modelLayer, CollisionLayer? collisionLayer, NavMeshLayer? navMeshLayer)
    {
        var world = state.World;
        var lists = new List<ProblemList>();
        if (state.ProjectError is { } projectError)
            lists.Add(new(L("Projet"), [projectError], null));
        if (world.Files is { Errors.Count: > 0 } files)
            lists.Add(new(L("Archives illisibles"), files.Errors, null));
        if (world.PathNetwork is { Errors.Count: > 0 } network)
            lists.Add(new(L("Pathnodes illisibles"), network.Errors, null));
        if (world.Content is { Errors.Count: > 0 } content)
            lists.Add(new(L("Problèmes de DLC"), content.Errors, null));
        if (world.Maps is { SavedMapErrors.Count: > 0 } savedMaps)
            lists.Add(new(L("Fichiers enregistrés non repris"), savedMaps.SavedMapErrors, L("La version du jeu est affichée à leur place.")));
        if (OccluderMaps.Errors is { Count: > 0 } occluderErrors)
            lists.Add(new(L("Occlusion enregistrée non reprise"), occluderErrors, L("Les occluders du jeu sont affichés à leur place.")));
        if (world.Maps is { Errors.Count: > 0 } maps)
            lists.Add(new(L("Fichiers de carte ignorés"), maps.Errors, L("Fichiers dans un autre format (XML, PSO), pas encore lus.")));
        if (world.Textures is { Errors.Count: > 0 } textures)
            lists.Add(new(L("Liens de textures illisibles"), textures.Errors, null));
        if (modelLayer is { Errors.Count: > 0 })
            lists.Add(new(L("Modèles ou textures illisibles"), modelLayer.Errors, null));
        if (world.Collisions is { Errors.Count: > 0 } collisionCatalog)
            lists.Add(new(L("Fichiers cache illisibles"), collisionCatalog.Errors, L("Les collisions qu'ils situent ne sont pas affichées.")));
        if (collisionLayer is { Errors.Count: > 0 })
            lists.Add(new(L("Collisions illisibles"), collisionLayer.Errors, null));
        if (navMeshLayer is { Errors.Count: > 0 })
            lists.Add(new(L("Cases de navmesh illisibles"), navMeshLayer.Errors, null));
        if (world.Scenarios is { Errors.Count: > 0 } scenarios)
            lists.Add(new(L("Fichiers de scénarios ignorés"), scenarios.Errors, null));
        return lists;
    }

    public static string Build(AppState? state, IReadOnlyList<ProblemList> problems, Exception? fatal = null)
    {
        var text = new StringBuilder();
        text.AppendLine(L($"=== Rapport {Program.Name} {Program.Version} ==="));
        text.AppendLine(L($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        text.AppendLine(L($"Windows : {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}"));
        long totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        text.AppendLine(L($"Mémoire : {totalMemory / (1024 * 1024 * 1024.0):0.0} Go au total, ")
            + L($"{Environment.WorkingSet / (1024 * 1024 * 1024.0):0.0} Go utilisés par {Program.Name}"));
        if (state is not null)
        {
            text.AppendLine(L($"Carte graphique : {state.AdapterName}"));
            text.AppendLine(L($"Échelle de l'interface : {(state.Settings.UiScale <= 0 ? "auto" : $"{state.Settings.UiScale:0}%")}"));
            text.AppendLine(L($"Dossier des données : {Anonymize(state.Paths.DataDirectory)}"));
            AppendGame(text, state);
            text.AppendLine(L($"Carte : {state.Settings.MapVersion}, DLC : {state.Settings.LastDlcPack ?? "tous"}"));
            text.AppendLine(L($"État du chargement : {state.World.Status}"));
            text.AppendLine(L($"Projet : {(state.Project is { } project ? project.Name : "aucun")}"));
        }

        if (fatal is not null)
        {
            text.AppendLine();
            text.AppendLine(L("--- Plantage ---"));
            text.AppendLine(Anonymize(fatal.ToString()));
        }

        var errors = ErrorLog.Snapshot();
        text.AppendLine();
        text.AppendLine(L($"--- Erreurs ({errors.Count}) ---"));
        foreach (var error in errors)
        {
            text.AppendLine($"[{error.Time:HH:mm:ss}] {error.Where} : {Anonymize(error.Message)}");
            if (error.Details is not null)
                text.AppendLine(Anonymize(error.Details));
        }

        foreach (var list in problems)
        {
            text.AppendLine();
            text.AppendLine($"--- {list.Title} ({list.Items.Count}) ---");
            foreach (var item in list.Items.Take(30))
                text.AppendLine(Anonymize(item));
            if (list.Items.Count > 30)
                text.AppendLine(L($"... et {list.Items.Count - 30} autres"));
        }
        return text.ToString();
    }

    private static void AppendGame(StringBuilder text, AppState state)
    {
        if (state.Game is not { } game)
        {
            text.AppendLine(L("GTA V : introuvable"));
            return;
        }
        text.AppendLine(L($"GTA V : {Anonymize(game.FolderPath)} ({game.Edition}, trouvé via : {game.Source}, plateforme probable : {Store(game.FolderPath)})"));
        string exeName = game.Edition == GameEdition.Enhanced ? GameFolderValidator.EnhancedExecutable : GameFolderValidator.LegacyExecutable;
        string exePath = Path.Combine(game.FolderPath, exeName);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            text.AppendLine(L($"{exeName} : version {info.FileVersion}, {new FileInfo(exePath).Length:N0} octets"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine(L($"{exeName} : illisible ({ex.Message})"));
        }
        if (state.World.Keys is { } keys)
        {
            text.AppendLine(L($"Clés des archives : {(keys.UsedSource is { } source ? L($"ouvertes par {source}") : L("non ouvertes"))}"));
            if (keys.Attempts.Count > 0)
                text.AppendLine(L($"  essais : {string.Join(" ; ", keys.Attempts)}"));
        }
    }

    private static string Store(string folder)
    {
        string f = folder.ToLowerInvariant();
        if (f.Contains("epic")) return "Epic Games";
        if (f.Contains("steam")) return "Steam";
        if (f.Contains("rockstar")) return "Rockstar Games Launcher";
        if (File.Exists(Path.Combine(folder, "steam_api64.dll"))) return "Steam";
        if (File.Exists(Path.Combine(folder, "EOSSDK-Win64-Shipping.dll"))) return "Epic Games";
        return "inconnue";
    }

    // The Windows user name is personal: replaced in the paths of the report.
    private static string Anonymize(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? text : text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    // Written next to the settings when the program crashes, so the report survives the closing.
    public static string? WriteCrashFile(AppState? state, Exception fatal)
    {
        try
        {
            string folder = state?.Paths.DataDirectory ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "rapport-erreur.txt");
            File.WriteAllText(path, Build(state, state is null ? [] : Problems(state, null, null, null), fatal));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
