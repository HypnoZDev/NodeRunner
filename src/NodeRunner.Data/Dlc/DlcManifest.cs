namespace NodeRunner.Data.Dlc;

/// <summary>
/// A DLC pack's setup2.xml: its device name (the prefix of its paths, e.g. "dlcMPHeist:/"), its loading
/// order and the change sets it runs in each group (GROUP_STARTUP, GROUP_MAP...).
/// </summary>
public sealed class DlcSetup
{
    public required string DeviceName { get; init; }

    /// <summary>Name given by the pack (nameHash, e.g. "mpHeist"), or null.</summary>
    public string? Name { get; init; }

    /// <summary>Name of the content file in the pack (usually content.xml).</summary>
    public string ContentFile { get; init; } = "content.xml";

    /// <summary>Packs are mounted by increasing order (ties keep the order of dlclist.xml).</summary>
    public int Order { get; init; }

    /// <summary>Number of extra archives (dlc1.rpf, dlc2.rpf...) next to dlc.rpf.</summary>
    public int SubPackCount { get; init; }

    /// <summary>Change set names by group name (both in upper case).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ChangeSetGroups { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>A DLC pack's content.xml: the files it brings and the change sets that switch them on and off.</summary>
public sealed class DlcContent
{
    public IReadOnlyList<DlcDataFile> DataFiles { get; init; } = [];

    /// <summary>Change sets by name (upper case).</summary>
    public IReadOnlyDictionary<string, DlcChangeSet> ChangeSets { get; init; } = new Dictionary<string, DlcChangeSet>();
}

/// <summary>A file declared by a pack. Disabled files are only used once a change set enables them.</summary>
public sealed record DlcDataFile(string FileName, string FileType, bool Disabled);

/// <summary>
/// Files a change set acts on, applied in this order: invalidated files (base game archives replaced by the pack)
/// are removed, disabled files are switched off, enabled files are switched on. The lists of the map change set
/// data (mapChangeSetData) are merged in.
/// </summary>
public sealed class DlcChangeSet
{
    public required string Name { get; init; }
    public IReadOnlyList<string> FilesToInvalidate { get; init; } = [];
    public IReadOnlyList<string> FilesToDisable { get; init; } = [];
    public IReadOnlyList<string> FilesToEnable { get; init; } = [];
}

/// <summary>Reads the DLC configuration files (XML).</summary>
public interface IDlcManifestReader
{
    /// <summary>Folder names of the packs listed in dlclist.xml, in order, lower case (e.g. "mpheist").</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    IReadOnlyList<string> ReadPackList(byte[] data);

    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    DlcSetup ReadSetup(byte[] data);

    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    DlcContent ReadContent(byte[] data);
}
