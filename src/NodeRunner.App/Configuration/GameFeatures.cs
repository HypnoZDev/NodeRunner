namespace NodeRunner.App.Configuration;

// What NodeRunner offers for a game: the interface asks this rather than which game is open.
public sealed record GameFeatures(
    bool Editing,
    bool Gta5Exports,
    bool Online,
    bool PedViewer,
    bool CutsceneViewer,
    bool Play)
{
    private static readonly GameFeatures Everything = new(true, true, true, true, true, true);
    private static readonly GameFeatures BrowseOnly = new(false, false, false, false, false, false);

    // Editing: projects, creating and placing objects, saving, the model workshop.
    // Gta5Exports: FiveM resource, solo pack, FiveM scripts.
    public static GameFeatures Of(GameKind game) => game switch
    {
        GameKind.Gta5 => Everything,
        _ => BrowseOnly,
    };
}
