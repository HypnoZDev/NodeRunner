namespace NodeRunner.Data.Installation;

/// <summary>A validated game installation folder.</summary>
/// <param name="FolderPath">Full path of the folder containing the game executable.</param>
/// <param name="Edition">Legacy or Enhanced.</param>
/// <param name="Source">Where we found it (e.g. "Steam", "Epic Games", "Rockstar Launcher", "Manual").</param>
public sealed record GameInstallation(string FolderPath, GameEdition Edition, string Source);
