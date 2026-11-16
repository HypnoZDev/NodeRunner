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
            // With both games installed, the start-up screen asks which one to open (the last one by default).
            var main = new MainForm(state);
            bool hasGta5 = state.Game is not null, hasGta4 = state.Gta4Folder is not null;
            if (hasGta5 || hasGta4)
            {
                var game = !hasGta5 ? GameKind.Gta4 : !hasGta4 ? GameKind.Gta5 : settings.LastGame;
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
                    else
                    {
                        state.RestoreProject();
                        state.World.Load(state.Game, settings.MapSelection, settings.HiddenScriptedMaps, state.SaveFolder);
                    }
                    if (hasGta5 && hasGta4 && settings.LastGame != chosen)
                    {
                        settings.LastGame = chosen;
                        state.SaveSettings();
                    }
                }
                bool offer = hasGta5 && hasGta4;
                if (!offer)
                    Start(game);
                using var splash = new SplashForm(state.World, state.Gta4, main.PreloadModels, game, settings.Gta4Episode, offer ? Start : null);
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
