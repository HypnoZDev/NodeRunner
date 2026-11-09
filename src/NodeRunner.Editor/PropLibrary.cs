using System.IO.Compression;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// The prop library shared by every project: a folder of addon packs (FiveM resources, or plain folders of .ytyp, models
// and textures). Their props can be placed in any project; a FiveM export takes only the files the placed props need.
public static class PropLibrary
{
    // Copies a pack (folder or .zip) into the library, in a folder of its name. Returns that folder.
    public static string AddPack(string source, string library)
    {
        source = Path.GetFullPath(source.TrimEnd('\\', '/'));
        string target = Path.Combine(library, PackName(source));
        if (IsInside(source, library) || IsInside(library, source))
            throw new IOException(L("ce dossier fait déjà partie de la bibliothèque"));
        string temporary = target + ".tmp";
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, recursive: true);
        if (Directory.Exists(source))
            CopyFolder(source, temporary);
        else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(source, temporary);
        else
            throw new IOException(L("seuls les dossiers et les archives .zip sont pris en charge (décompresse d'abord un .rar ou un .7z)"));

        // A .zip that only holds the pack's folder: that folder becomes the pack.
        string content = temporary;
        while (Directory.GetFiles(content).Length == 0 && Directory.GetDirectories(content) is [var only])
            content = only;
        if (!Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories).Any(IsLibraryFile))
        {
            Directory.Delete(temporary, recursive: true);
            throw new IOException(L("aucun fichier .ytyp, .ydr, .ydd, .yft, .ytd ou .ybn dans ce pack"));
        }
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(library);
        Directory.Move(content, target);
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, recursive: true);
        return target;
    }

    // A folder or .zip of addon props: object types (.ytyp) and no maps (.ymap), which would make it a mapping.
    public static bool LooksLikePack(string source)
    {
        try
        {
            IEnumerable<string> names;
            if (Directory.Exists(source))
                names = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
            else if (File.Exists(source) && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(source);
                names = zip.Entries.Select(e => e.FullName).ToList();
            }
            else
                return false;
            bool types = false;
            foreach (var name in names)
            {
                string extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension == ".ymap")
                    return false;
                types |= extension == ".ytyp";
            }
            return types;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    public static string PackName(string source) =>
        Path.GetFileNameWithoutExtension(Path.GetFullPath(source.TrimEnd('\\', '/')));

    public static bool HasPack(string source, string library) => Directory.Exists(Path.Combine(library, PackName(source)));

    private static bool IsLibraryFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ytyp" or ".ydr" or ".ydd" or ".yft" or ".ytd" or ".ybn";

    private static bool IsInside(string path, string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }

    // The library's files (virtual paths of GameContent.LibraryRoot) that these placed object types need: the .ytyp that
    // defines each type of the library, its model (with the very detailed "_hi" version of a fragment), texture
    // dictionary, drawable dictionary and physics dictionary; for an interior, the same for the objects inside it.
    public static List<string> Dependencies(IEnumerable<uint> archetypes, Func<uint, Archetype?> archetypeOf,
        Func<uint, string?> fileOf, Func<Archetype, string?> modelOf, IEnumerable<string> libraryFiles)
    {
        var byHash = new Dictionary<(string Extension, uint Hash), string>();
        foreach (var path in libraryFiles)
            byHash.TryAdd((Path.GetExtension(path).ToLowerInvariant(), Joaat.Hash(Path.GetFileNameWithoutExtension(path).ToLowerInvariant())), path);

        var needed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (path is not null && GameContent.IsLibraryFile(path) && seen.Add(path))
                needed.Add(path);
        }
        void AddByHash(string extension, uint hash)
        {
            if (hash != 0 && byHash.TryGetValue((extension, hash), out var path))
                Add(path);
        }

        var queue = new Queue<uint>(archetypes);
        var done = new HashSet<uint>();
        while (queue.TryDequeue(out uint hash))
        {
            if (!done.Add(hash) || archetypeOf(hash) is not { } archetype)
                continue;
            if (fileOf(hash) is { } file && GameContent.IsLibraryFile(file))
            {
                Add(file);
                string? model = modelOf(archetype);
                Add(model);
                if (model is null)
                {
                    AddByHash(".ydr", archetype.AssetNameHash);
                    AddByHash(".yft", archetype.AssetNameHash);
                }
                if (archetype.AssetType == ArchetypeAssetType.Fragment || model?.EndsWith(".yft", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string name = model is null ? "" : Path.GetFileNameWithoutExtension(model);
                    AddByHash(".yft", Joaat.Hash(name.ToLowerInvariant() + "_hi"));
                }
                AddByHash(".ydd", archetype.DrawableDictionaryHash);
                AddByHash(".ytd", archetype.TextureDictionaryHash);
                AddByHash(".ybn", archetype.PhysicsDictionaryHash);
            }
            foreach (var entity in archetype.InteriorEntities.Concat(archetype.InteriorEntitySets.SelectMany(s => s.Entities)))
                queue.Enqueue(entity.ArchetypeHash);
        }
        return needed;
    }
}
