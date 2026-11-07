using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.App.World;

// Codecs of the occluder edits. Game files are found by path; the project's own file is this computer's project file
// (its name follows each computer's resource name).
internal abstract class OccluderCodec<T>(WorldLoader world, Func<string> projectFileName) : EditCodec<T> where T : IEdit
{
    protected void WriteFile(EditWriter w, OccluderMapFile file)
    {
        w.Write(file.IsProject);
        w.Write(file.IsProject ? "" : file.GamePath);
    }

    protected OccluderMapFile ReadFile(EditReader r)
    {
        bool isProject = r.ReadBool();
        string path = r.ReadString();
        var file = isProject
            ? OccluderMaps.ProjectFile(world, projectFileName())
            : OccluderMaps.Get(world).FirstOrDefault(f => !f.IsProject && string.Equals(f.GamePath, path, StringComparison.OrdinalIgnoreCase));
        return file ?? throw new InvalidDataException($"Occluder file {path} not found here.");
    }

    protected static void WriteBox(EditWriter w, OccluderBox box)
    {
        w.Write(box.Index);
        w.Write(box.Center);
        w.Write(box.Size);
        w.Write(box.Angle);
    }

    protected static OccluderBox ReadBox(EditReader r) => new(r.ReadInt(), r.ReadVector3(), r.ReadVector3(), r.ReadFloat());

    protected static OccluderBox BoxAt(OccluderMapFile file, int index) =>
        index >= 0 && index < file.Boxes.Count ? file.Boxes[index] : throw new InvalidDataException("Occluder box not found here.");
}

internal sealed class OccluderBoxCodec(WorldLoader world, Func<string> projectFileName) : OccluderCodec<OccluderBoxEdit>(world, projectFileName)
{
    public override string Tag => "occluder-box";

    protected override void Write(OccluderBoxEdit edit, EditWriter w)
    {
        WriteFile(w, edit.File);
        WriteBox(w, edit.File.Boxes[edit.Index]);
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var file = ReadFile(r);
        var after = ReadBox(r);
        var before = BoxAt(file, after.Index);
        OccluderMaps.SetBox(file, after);
        return new OccluderBoxEdit(file, before, after, description);
    }
}

internal sealed class OccluderPresenceCodec(WorldLoader world, Func<string> projectFileName) : OccluderCodec<OccluderPresenceEdit>(world, projectFileName)
{
    public override string Tag => "occluder-presence";

    protected override void Write(OccluderPresenceEdit edit, EditWriter w)
    {
        w.Write(edit.Deletes);
        w.WriteList(edit.Occluders, o =>
        {
            WriteFile(w, o.File);
            w.Write((int)o.Kind);
            w.Write(o.Index);
        });
    }

    public override IEdit Apply(EditReader r, string description)
    {
        bool deletes = r.ReadBool();
        var occluders = r.ReadList(() => new OccluderSelection(ReadFile(r), (OcclusionLayerPart)r.ReadInt(), r.ReadInt()));
        foreach (var o in occluders)
            OccluderMaps.SetDeleted(o.File, o.Kind, o.Index, deletes);
        return new OccluderPresenceEdit(occluders, deletes, description);
    }
}

internal sealed class OccluderAddCodec(WorldLoader world, Func<string> projectFileName) : OccluderCodec<OccluderAddEdit>(world, projectFileName)
{
    public override string Tag => "occluder-add";

    protected override void Write(OccluderAddEdit edit, EditWriter w)
    {
        WriteFile(w, edit.File);
        w.WriteList(edit.Added, o => WriteBox(w, edit.File.Boxes[o.Index]));
    }

    public override IEdit Apply(EditReader r, string description)
    {
        var file = ReadFile(r);
        var boxes = r.ReadList(() => ReadBox(r));
        var edit = OccluderAddEdit.Apply(file, boxes, description);
        if (edit.Added.Select(o => o.Index).SequenceEqual(boxes.Select(b => b.Index)))
            return edit;
        throw new InvalidDataException("Occluder boxes differ here.");
    }
}

// Every shared edit of the application: the editor's and the occluders'.
internal static class OnlineEdits
{
    public static EditCodecs Create(WorldLoader world, Func<string> projectFileName) => EditCodecs.ForEditor()
        .Add(new OccluderBoxCodec(world, projectFileName))
        .Add(new OccluderPresenceCodec(world, projectFileName))
        .Add(new OccluderAddCodec(world, projectFileName));

    // Edit types of the application and the editor with no codec (none expected).
    public static IReadOnlyList<Type> Missing(EditCodecs codecs) =>
        codecs.Missing(typeof(IEdit).Assembly, typeof(OnlineEdits).Assembly);
}
