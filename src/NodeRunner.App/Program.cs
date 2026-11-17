using NodeRunner.App.Configuration;
using NodeRunner.App.Diagnostics;

namespace NodeRunner.App;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public const string Name = "NodeRunner Studio";

    public const string Author = "Inside Rockstar";

    private static AppState? _state;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Errors escaping a window event or a thread: our own message and report instead of the raw .NET dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Crash(ex);
        };

        var paths = AppPaths.Resolve();
        var settings = AppSettings.Load(paths.SettingsFile);
        NodeRunner.Editor.Localization.Loc.Current = settings.InterfaceLanguage;
        var state = new AppState(paths, settings);
        _state = state;
        state.RedetectGame();
        state.SaveSettings(); // Creates settings.json on first launch so the user can find it.

        try
        {
            // The map starts loading at once, behind the start-up screen, then the 3D models around the camera (main
            // window still hidden): the main window opens on a loaded view.
            // With several games installed, the start-up screen asks which one to open (the last one by default). The
            // trilogy has one button, for the game of it opened last (the others are picked above the 3D view).
            var main = new MainForm(state);
            var games = new List<GameKind>();
            if (state.Game is not null)
                games.Add(GameKind.Gta5);
            if (state.Gta4Folder is not null)
                games.Add(GameKind.Gta4);
            if (state.ClassicFolders.Count > 0)
            {
                var classic = settings.LastClassicGame.Classic() is { } last && state.ClassicFolders.ContainsKey(last)
                    ? settings.LastClassicGame
                    : state.ClassicFolders.Keys.Order().First().Kind();
                games.Add(classic);
            }
            if (games.Count > 0)
            {
                var game = games.Contains(settings.LastGame) ? settings.LastGame
                    : settings.LastGame.Classic() is not null && games.Any(g => g.Classic() is not null) ? games.First(g => g.Classic() is not null)
                    : games[0];
                bool started = false;
                void Start(GameKind chosen)
                {
                    started = true;
                    state.ActiveGame = chosen;
                    // Projects belong to GTA V: the last one is opened again only there.
                    if (chosen == GameKind.Gta4)
                    {
                        state.Gta4.Load(state.Gta4Folder!, settings.Gta4Episode);
                    }
                    else if (chosen.Classic() is { } classicGame)
                    {
                        state.Classic.Load(state.ClassicFolders[classicGame], classicGame);
                        settings.LastClassicGame = chosen;
                    }
                    else
                    {
                        state.RestoreProject();
                        state.World.Load(state.Game, settings.MapSelection, settings.HiddenScriptedMaps, state.SaveFolder);
                    }
                    settings.LastGame = chosen;
                    state.SaveSettings();
                }
                bool offer = games.Count > 1;
                if (!offer)
                    Start(game);
                using var splash = new SplashForm(state.World, state.Gta4, state.Classic, games, main.PreloadModels, game, settings.Gta4Episode,
                    offer ? Start : null);
                Application.Run(splash);
                // Closed before a choice (Alt+F4): the highlighted game.
                if (!started)
                    Start(game);
            }
            else
            {
                state.RestoreProject();
            }
            Application.Run(main);
        }
        catch (Exception ex)
        {
            Crash(ex);
        }
    }

    private static int _crashing;

    private static void Crash(Exception ex)
    {
        if (Interlocked.Exchange(ref _crashing, 1) == 1)
            return;
        ErrorLog.Add(L("Plantage"), ex);
        string? report = ErrorReport.WriteCrashFile(_state, ex);
        string where = report is null ? "" : L($"\n\nUn rapport a été enregistré ici :\n{report}\n\nEnvoie ce fichier au développeur (Discord, GitHub) pour qu'il corrige le problème.");
        MessageBox.Show(L($"{Name} a rencontré une erreur et doit se fermer :\n\n{ex.Message}{where}"),
            Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        if (report is not null)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{report}\"");
            }
            catch (Exception)
            {
                // Explorer not available: the path is in the message.
            }
        }
        Environment.Exit(1);
    }
}
