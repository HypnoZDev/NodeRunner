using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;

namespace NodeRunner.Editor.Online;

// What an edit received from another computer is applied to.
public sealed class EditContext
{
    public required MapLibrary Library { get; init; }
    public PathNetwork? Paths { get; init; }
    public CollisionEdits? Collisions { get; init; }
}

// Turns one kind of edit into bytes, and bytes back into the same change made on this computer's map (returning the
// edit to record there, so it can be undone like a local one).
public abstract class EditCodec
{
    // Short name sent with each message; never change it once released (old and new versions must agree on it).
    public abstract string Tag { get; }
    public abstract Type EditType { get; }
    public abstract void Write(IEdit edit, EditWriter writer);
    public abstract IEdit Apply(EditReader reader, string description);
}

public abstract class EditCodec<T> : EditCodec where T : IEdit
{
    public sealed override Type EditType => typeof(T);
    public sealed override void Write(IEdit edit, EditWriter writer) => Write((T)edit, writer);
    protected abstract void Write(T edit, EditWriter writer);
}

// Marks an edit that stays on its computer (view only, never shared): exempt from the codec check.
[AttributeUsage(AttributeTargets.Class)]
public sealed class LocalEditAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

// Every kind of shared edit and its codec. Each edit type needs one: Missing lists those without, and a test fails
// when a new kind of edit is added without its codec.
public sealed class EditCodecs
{
    // Raised when the message layout itself changes.
    public const int FormatVersion = 1;

    private readonly Dictionary<Type, EditCodec> _byType = [];
    private readonly Dictionary<string, EditCodec> _byTag = [];

    public IReadOnlyCollection<EditCodec> All => _byType.Values;

    public EditCodecs Add(EditCodec codec)
    {
        if (!_byTag.TryAdd(codec.Tag, codec) || !_byType.TryAdd(codec.EditType, codec))
            throw new ArgumentException($"Codec \"{codec.Tag}\" registered twice.", nameof(codec));
        return this;
    }

    // The codecs of the editor's own edits.
    public static EditCodecs ForEditor() => new EditCodecs()
        .Add(new PlacementCodec())
        .Add(new GroupPlacementCodec())
        .Add(new AddCodec())
        .Add(new InteriorAddCodec())
        .Add(new EntitySetMoveCodec())
        .Add(new EntitySetCodec())
        .Add(new DeleteCodec())
        .Add(new MergeCodec())
        .Add(new CollisionCodec())
        .Add(new PathNodeMoveCodec())
        .Add(new RoomCodec())
        .Add(new PortalCodec())
        .Add(new InteriorLayoutCodec());

    public bool CanShare(IEdit edit) => _byType.ContainsKey(edit.GetType());

    // Message: tag, description, then the codec's data.
    public byte[] Encode(IEdit edit, MapLibrary library)
    {
        if (!_byType.TryGetValue(edit.GetType(), out var codec))
            throw new NotSupportedException($"No codec for {edit.GetType().Name}.");
        using var writer = new EditWriter(library);
        writer.Write(codec.Tag);
        writer.Write(edit.Description);
        codec.Write(edit, writer);
        return writer.ToArray();
    }

    // Makes the change on this computer and returns the edit to record.
    public IEdit Apply(byte[] message, EditContext context)
    {
        using var reader = new EditReader(message) { Context = context };
        string tag = reader.ReadString();
        if (!_byTag.TryGetValue(tag, out var codec))
            throw new InvalidDataException($"Unknown edit \"{tag}\".");
        string description = reader.ReadString();
        var edit = codec.Apply(reader, description);
        if (!reader.AtEnd)
            throw new InvalidDataException($"Edit \"{tag}\" longer than expected.");
        return edit;
    }

    // Same signature on two computers = same edits understood the same way (checked when joining a session).
    public string Signature
    {
        get
        {
            var text = $"{FormatVersion}|{string.Join(",", _byTag.Keys.Order(StringComparer.Ordinal))}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }
    }

    // Edit types of these assemblies with no codec and not marked LocalEdit.
    public IReadOnlyList<Type> Missing(params Assembly[] assemblies) =>
    [
        .. assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEdit).IsAssignableFrom(t))
            .Where(t => !_byType.ContainsKey(t) && t.GetCustomAttribute<LocalEditAttribute>() is null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal),
    ];
}
