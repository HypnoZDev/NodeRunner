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
        state.RestoreProject();
        state.SaveSettings(); // Creates settings.json on first launch so the user can find it.

        try
        {
            // The map starts loading at once, behind the start-up screen, then the 3D models around the camera (main
            // window still hidden): the main window opens on a loaded view.
            var main = new MainForm(state);
            if (state.Game is not null)
            {
                state.World.Load(state.Game, settings.MapSelection, settings.HiddenScriptedMaps, state.SaveFolder);
                using var splash = new SplashForm(state.World, main.PreloadModels);
                Application.Run(splash);
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
