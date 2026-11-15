using NodeRunner.Data.Audio;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Audio;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.App.World;

// Writes the audio occlusion (<proxy hash>.ymt) of the interiors placed by the exported maps that have none where they
// are (created, moved, imported without one), or whose rooms and portals were changed.
internal static class AudioOcclusionFileSaver
{
    // files: names of the files in stream; interiorFiles: the .ytyp of the interiors changed in the project.
    public static void Save(WorldLoader world, IReadOnlyList<string> files, IReadOnlyList<string> interiorFiles, string stream,
        MapFileSaver.Report report)
    {
        if (world.Maps is not { } library)
            return;
        var changed = interiorFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var interiors = new Dictionary<uint, Archetype>();
        foreach (string name in files.Where(f => f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
        {
            MapData map;
            try
            {
                map = new YmapReader().Read(ResourceFile.Read(File.ReadAllBytes(Path.Combine(stream, name))).Content);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                continue; // Occlusion or grass only, or locked: no interiors to place.
            }
            foreach (var entity in map.Entities.Where(e => e.IsInterior))
            {
                if (library.GetArchetype(entity.ArchetypeHash) is { Kind: ArchetypeKind.Interior } type)
                    interiors.TryAdd(AudioOcclusionHash.Of(entity.ArchetypeHash, entity.Position), type);
            }
        }
        if (interiors.Count == 0)
            return;

        var catalog = world.WaitAudioOcclusion();
        PsoSchema? schema = null;
        foreach (var (hash, type) in interiors)
        {
            string name = $"{hash}.ymt";
            string target = Path.Combine(stream, name);
            try
            {
                var built = AudioOcclusionBuilder.Build(type, hash, library.GetArchetype);
                if (Existing(catalog, hash, target) is { } existing)
                {
                    // The game's file (or the mapping's) is kept while it matches the interior.
                    bool edited = library.GetArchetypeFile(type.NameHash) is { } file && changed.Contains(file);
                    if (!edited || existing.Occlusion is not { } read || SameLinks(read, built, hash))
                        continue;
                }
                if (built.Links.Count == 0)
                    continue; // No portal: nothing for the sound to go through.
                schema ??= AudioOcclusionWriter.Schema(catalog?.Files() ?? [])
                    ?? throw new InvalidDataException(L("aucun fichier d'occlusion audio du jeu (il décrit le format)"));
                var bytes = AudioOcclusionWriter.Write(built, schema);
                var check = new AudioOcclusionReader().Read(bytes);
                if (check.Links.Count != built.Links.Count || check.Paths.Count != built.Paths.Count)
                    throw new InvalidDataException(L("vérification échouée"));
                string temporary = target + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    private sealed record Found(AudioOcclusion? Occlusion);

    // The occlusion already given to this placed interior, by the game or by a file copied from the project; a file that
    // cannot be read is found without content (left as it is).
    private static Found? Existing(AudioOcclusionCatalog? catalog, uint hash, string target)
    {
        if (File.Exists(target))
        {
            try
            {
                return new Found(new AudioOcclusionReader().Read(File.ReadAllBytes(target)));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException
                or OverflowException)
            {
                return new Found(null);
            }
        }
        return catalog?.PathOf(hash) is null ? null : new Found(catalog.Get(hash).Occlusion);
    }

    // Same passages between the rooms of the interior (those towards other interiors left aside).
    private static bool SameLinks(AudioOcclusion read, AudioOcclusion built, uint hash) =>
        read.Links.Where(l => l.DestInteriorProxyHash == hash).Select(l => (l.RoomIndex, l.PortalIndex, l.DestRoomIndex))
            .SequenceEqual(built.Links.Select(l => (l.RoomIndex, l.PortalIndex, l.DestRoomIndex)));
}
