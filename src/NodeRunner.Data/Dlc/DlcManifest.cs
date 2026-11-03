namespace NodeRunner.Data.Dlc;

// A DLC pack's setup2.xml: its device name (the prefix of its paths, e.g. "dlcMPHeist:/"), its loading order and the
// change sets it runs in each group (GROUP_STARTUP, GROUP_MAP...).
public sealed class DlcSetup
{
    public required string DeviceName { get; init; }

    public string? Name { get; init; }

    public string ContentFile { get; init; } = "content.xml";

    public int Order { get; init; }

    public int SubPackCount { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ChangeSetGroups { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

// A DLC pack's content.xml: the files it brings and the change sets that switch them on and off.
public sealed class DlcContent
{
    public IReadOnlyList<DlcDataFile> DataFiles { get; init; } = [];

    public IReadOnlyDictionary<string, DlcChangeSet> ChangeSets { get; init; } = new Dictionary<string, DlcChangeSet>();
}

// A file declared by a pack. Disabled files are only used once a change set enables them.
public sealed record DlcDataFile(string FileName, string FileType, bool Disabled);

// Files a change set acts on, applied in this order: invalidated files (base game archives replaced by the pack) are
// removed, disabled files are switched off, enabled files are switched on.
public sealed class DlcChangeSet
{
    public required string Name { get; init; }
    public IReadOnlyList<string> FilesToInvalidate { get; init; } = [];
    public IReadOnlyList<string> FilesToDisable { get; init; } = [];
    public IReadOnlyList<string> FilesToEnable { get; init; } = [];
}

// Reads the DLC configuration files (XML).
public interface IDlcManifestReader
{
    // Folder names of the packs listed in dlclist.xml, in order, lower case (e.g. "mpheist").
    IReadOnlyList<string> ReadPackList(byte[] data);

    DlcSetup ReadSetup(byte[] data);

    DlcContent ReadContent(byte[] data);
}
