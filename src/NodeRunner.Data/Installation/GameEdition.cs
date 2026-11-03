namespace NodeRunner.Data.Installation;

/// <summary>Which PC version of the game an installation folder contains.</summary>
public enum GameEdition
{
    /// <summary>The original PC release (2015), a.k.a. "Legacy". Executable: GTA5.exe.</summary>
    Legacy,

    /// <summary>The 2025 "Enhanced" release (Gen9 assets). Executable: GTA5_Enhanced.exe.</summary>
    Enhanced,
}
