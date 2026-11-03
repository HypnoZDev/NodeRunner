using NodeRunner.App.Configuration;

namespace NodeRunner.App;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public const string Name = "NodeRunner Studio";

    public const string Author = "Inside Rockstar";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var paths = AppPaths.Resolve();
        var settings = AppSettings.Load(paths.SettingsFile);
        var state = new AppState(paths, settings);
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
            MessageBox.Show(
                $"{Name} a rencontré une erreur et doit se fermer :\n\n" + ex,
                Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
