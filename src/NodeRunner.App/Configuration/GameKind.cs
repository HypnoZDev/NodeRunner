namespace NodeRunner.App.Configuration;

// The game NodeRunner works on in this session.
public enum GameKind
{
    Gta5,

    Gta4,

    Gta3,

    ViceCity,

    SanAndreas,
}

public static class GameKinds
{
    // The game of the first trilogy, null for GTA IV and GTA V.
    public static NodeRunner.Data.Installation.ClassicGame? Classic(this GameKind game) => game switch
    {
        GameKind.Gta3 => NodeRunner.Data.Installation.ClassicGame.Gta3,
        GameKind.ViceCity => NodeRunner.Data.Installation.ClassicGame.ViceCity,
        GameKind.SanAndreas => NodeRunner.Data.Installation.ClassicGame.SanAndreas,
        _ => null,
    };

    public static GameKind Kind(this NodeRunner.Data.Installation.ClassicGame game) => game switch
    {
        NodeRunner.Data.Installation.ClassicGame.Gta3 => GameKind.Gta3,
        NodeRunner.Data.Installation.ClassicGame.ViceCity => GameKind.ViceCity,
        _ => GameKind.SanAndreas,
    };
}
