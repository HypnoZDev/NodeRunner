using System.Security.Cryptography;
using System.Text;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.App.World;

// Rescue copy of the unsaved changes: every few minutes, the changes made since the last save are written (as the
// messages an online session sends) to data/secours, never into the project. After a crash, the next load of the same
// project and map offers to replay them.
internal sealed class AutoBackup
{
    private const string Magic = "NRBK";
    private const int Format = 1;

    private readonly AppState _state;
    private EditCodecs? _codecs;
    private readonly Dictionary<IEdit, byte[]?> _messages = new(ReferenceEqualityComparer.Instance);
    private int _version;
    private int _writtenVersion = -1;
    private string? _writtenFile;
    private double _lastWrite;
    private object? _checkedMaps;

    public AutoBackup(AppState state)
    {
        _state = state;
        state.History.Added += OnChanged;
        state.History.Undone += _ => _version++;
        state.History.Redone += _ => _version++;
    }

    // A copy found for the map just loaded, waiting for the user's answer.
    public Recovery? Found { get; private set; }

    public sealed record Recovery(string File, DateTime Date, IReadOnlyList<byte[]> Messages, IReadOnlyList<string> Descriptions);

    private EditCodecs Codecs => _codecs ??= OnlineEdits.Create(_state.World, () => _state.OccluderFileName);

    private void OnChanged(IEdit edit)
    {
        _version++;
        if (!Codecs.CanShare(edit) || _state.World.Maps is not { } maps)
            return;
        try
        {
            _messages[edit] = Codecs.Encode(edit, maps);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            _messages[edit] = null; // No rescue copy until the next save.
        }
    }

    // Called once per frame.
    public void Update(double now)
    {
        var world = _state.World;
        if (world.Maps is not { } maps || world.IsBusy || _state.Online.IsGuest)
            return;
        if (!ReferenceEquals(maps, _checkedMaps))
        {
            _checkedMaps = maps;
            _messages.Keys.Where(e => !_state.History.All.Contains(e)).ToList().ForEach(e => _messages.Remove(e));
            _writtenFile = null;
            _writtenVersion = -1;
            _loadKey = Key; // Before any change (added objects change the counts).
            Found = _state.History.Count == 0 ? Read(FilePath()) : null;
            return;
        }

        float minutes = _state.Settings.BackupMinutes;
        if (Found is not null || minutes <= 0f)
            return;
        if (!_state.History.HasUnsavedChanges)
        {
            if (_writtenFile is not null)
                Discard();
            return;
        }
        if (_version == _writtenVersion || now - _lastWrite < minutes * 60.0)
            return;
        _lastWrite = now;
        if (Write())
        {
            _writtenVersion = _version;
            _failedVersion = -1;
        }
        else
        {
            // Disk unavailable for a moment: tried again soon, without waiting for another change.
            _lastWrite = now - minutes * 60.0 + RetrySeconds;
            _failedVersion = _version;
        }
    }

    private const double RetrySeconds = 30.0;
    private int _failedVersion = -1;

    // The changes are saved or given up: the copy goes.
    public void Discard()
    {
        string file = _writtenFile ?? FilePath();
        _writtenFile = null;
        _writtenVersion = -1;
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Replays the found changes; returns how many were applied.
    public int Restore()
    {
        if (Found is not { } found || _state.World.Maps is not { } maps)
            return 0;
        Found = null;
        var context = new EditContext { Library = maps, Paths = _state.World.PathNetwork, Collisions = _state.CollisionEdits, Scenarios = _state.World.Scenarios };
        int applied = 0;
        foreach (var message in found.Messages)
        {
            try
            {
                _state.History.Add(Codecs.Apply(message, context));
                applied++;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or EndOfStreamException or NotSupportedException)
            {
                Diagnostics.ErrorLog.Add(L("Copie de secours"), L($"Modification {applied + 1} non reprise : {ex.Message}"));
                break;
            }
        }
        _writtenFile = found.File;
        return applied;
    }

    public void Decline()
    {
        Found = null;
        Discard();
    }

    // --- File ---

    private string FilePath()
    {
        string name = "sans-projet";
        if (_state.Project is { } project)
        {
            string folder = Path.GetFullPath(project.Folder).TrimEnd('\\').ToLowerInvariant();
            name = $"{MapProject.ToFolderName(project.Name)}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..8]}";
        }
        return Path.Combine(_state.Paths.DataDirectory, "secours", name + ".nrsecours");
    }

    // Same map, same DLC, same kinds of edits: the messages can be replayed.
    private string _loadKey = "";

    private string Key => $"{_state.Settings.MapVersion}|{_state.Settings.LastDlcPack}|{_state.Online.Fingerprint}|{Codecs.Signature}";

    // False when the file could not be written (to try again); true when written or when there is nothing that can be.
    private bool Write()
    {
        var history = _state.History;
        var done = history.Done;
        int start = 0;
        if (history.SavedTop is { } saved)
        {
            start = IndexOf(done, saved) + 1;
            if (start == 0)
                return true; // Saved changes were undone: not a list of changes to replay.
        }
        var messages = new List<(string Description, byte[] Data)>();
        for (int i = start; i < done.Count; i++)
        {
            if (!_messages.TryGetValue(done[i], out var message))
            {
                if (Codecs.CanShare(done[i]))
                    return true;
                continue; // View only (LocalEdit).
            }
            if (message is null)
                return true; // A change that cannot be replayed: no copy until the next save.
            messages.Add((done[i].Description, message));
        }
        if (messages.Count == 0)
        {
            Discard();
            return true;
        }

        string file = FilePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Format);
                writer.Write(_loadKey);
                writer.Write(DateTime.Now.ToBinary());
                writer.Write(messages.Count);
                foreach (var (description, data) in messages)
                {
                    writer.Write(description);
                    writer.Write(data.Length);
                    writer.Write(data);
                }
            }
            File.Move(temporary, file, overwrite: true);
            _writtenFile = file;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_failedVersion != _version)
                Diagnostics.ErrorLog.Add(L("Copie de secours"), L($"Écriture impossible ({file}) : {ex.Message}"));
            return false;
        }
    }

    private static int IndexOf(IReadOnlyList<IEdit> list, IEdit edit)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], edit))
                return i;
        return -1;
    }

    private Recovery? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadString() != Magic || reader.ReadInt32() != Format || reader.ReadString() != _loadKey)
                return null; // Another map, DLC or version of NodeRunner: kept for when it matches.
            var date = DateTime.FromBinary(reader.ReadInt64());
            int count = reader.ReadInt32();
            // Each change takes at least 5 bytes (description and message lengths).
            if (count < 0 || count > (stream.Length - stream.Position) / 5)
                throw new InvalidDataException();
            var messages = new List<byte[]>(count);
            var descriptions = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                descriptions.Add(reader.ReadString());
                int length = reader.ReadInt32();
                if (length < 0 || length > stream.Length - stream.Position)
                    throw new InvalidDataException();
                messages.Add(reader.ReadBytes(length));
            }
            return count > 0 ? new Recovery(file, date, messages, descriptions) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or InvalidDataException or ArgumentException)
        {
            KeepDamaged(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A damaged copy is set aside rather than overwritten by the next one.
    private static void KeepDamaged(string file)
    {
        try
        {
            File.Move(file, file + ".damaged", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
