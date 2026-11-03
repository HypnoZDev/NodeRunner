namespace NodeRunner.Data.Installation;

// A validated game installation folder.
public sealed record GameInstallation(string FolderPath, GameEdition Edition, string Source);
