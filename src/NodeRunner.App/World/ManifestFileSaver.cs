using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes the resource's _manifest.ymf (PackManifest) from the .ymap and .ytyp files exported in its stream folder.
internal static class ManifestFileSaver
{
    private static (RpfFileSystem Files, PsoSchema Schema)? _schema;

    // files: names of the files in stream; newTypeFiles: the .ytyp among them that the game does not have. A manifest
    // brought by the project (an imported mapping) is kept, with what the export adds to it (Merge).
    public static void Save(WorldLoader world, IReadOnlyList<string> files, IReadOnlyList<string> newTypeFiles, string stream, MapFileSaver.Report report)
    {
        if (newTypeFiles.Count == 0)
            return;
        bool brought = files.Contains(PackManifest.FileName, StringComparer.OrdinalIgnoreCase);
        try
        {
            var typeFiles = new Dictionary<uint, string>();
            var newTypes = new List<PackManifest.TypeFile>();
            foreach (string name in newTypeFiles)
            {
                if (Read(stream, name) is not { } content)
                    continue;
                string file = Path.GetFileNameWithoutExtension(name);
                var types = new YtypReader().Read(content);
                foreach (var archetype in types.Archetypes)
                    typeFiles[archetype.NameHash] = file;
                newTypes.Add(new PackManifest.TypeFile(file, [.. types.Archetypes.Where(a => a.Kind == ArchetypeKind.Interior)
                    .Select(a => new PackManifest.Interior(a.NameHash,
                        [.. a.InteriorEntities.Concat(a.InteriorEntitySets.SelectMany(s => s.Entities)).Select(e => e.ArchetypeHash)]))]));
            }

            var maps = new List<PackManifest.MapFile>();
            foreach (string name in files.Where(f => f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
            {
                if (Read(stream, name) is not { } content)
                    continue;
                MapData map;
                try
                {
                    map = new YmapReader().Read(content);
                }
                catch (InvalidDataException)
                {
                    continue; // Occlusion or grass only, or locked: no objects to list.
                }
                maps.Add(new PackManifest.MapFile(Path.GetFileNameWithoutExtension(name), [.. map.Entities.Select(e => e.ArchetypeHash)],
                    map.Entities.Any(e => e.IsInterior)));
            }

            string? TypeFileOf(uint archetype) =>
                typeFiles.TryGetValue(archetype, out var file) ? file
                : world.Maps?.GetArchetypeFile(archetype) is { } path ? Path.GetFileNameWithoutExtension(path) : null;

            if (PackManifest.Build(maps, newTypes, TypeFileOf) is not { } xml)
                return;
            string path = Path.Combine(stream, PackManifest.FileName);
            if (brought)
            {
                XElement existing;
                try
                {
                    existing = PsoXml.Read(File.ReadAllBytes(path), PackManifest.FieldNames);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or IOException or UnauthorizedAccessException))
                {
                    return; // Not a manifest read here (another format, damaged): kept as it is.
                }
                if (!PackManifest.Merge(existing, xml))
                    return;
                xml = existing;
            }
            var bytes = PsoXml.Build(xml, Schema(world));
            SafeFile.WriteAllBytes(path, bytes);
            if (!brought)
                report.Saved.Add(PackManifest.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{PackManifest.FileName} : {ex.Message}");
        }
    }

    // The decompressed content of an exported resource file, null when it cannot be read (locked by FiveM...).
    private static byte[]? Read(string stream, string name)
    {
        try
        {
            return ResourceFile.Read(File.ReadAllBytes(Path.Combine(stream, name))).Content;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // Descriptions of the manifest's structures, from the game's own _manifest.ymf files (~0.2 s).
    private static PsoSchema Schema(WorldLoader world)
    {
        if (world.Files is not { } files)
            throw new InvalidDataException(L("les fichiers de GTA V doivent être chargés (ils décrivent le format des manifestes)"));
        lock (typeof(ManifestFileSaver))
        {
            if (_schema is { } cached && cached.Files == files)
                return cached.Schema;
            var schema = new PsoSchema();
            foreach (string path in files.EnumerateFiles(".ymf"))
            {
                if (files.ReadFile(path) is { } data)
                    schema.Add(data);
            }
            _schema = (files, schema);
            return schema;
        }
    }
}
