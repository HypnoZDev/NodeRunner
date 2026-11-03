using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.App.World;

// The game's files as a tree for the archive explorer: folders of the game directory, archives (.rpf, nested ones
// included), the folders inside them and their files.
internal sealed class ArchiveTree
{
    public sealed class Node(string name, string path, Node? parent)
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public Node? Parent { get; } = parent;
        public List<Node> Children { get; } = [];
        public int Depth { get; init; }
        public bool IsArchive { get; set; }
        public bool IsFile => Children.Count == 0 && !IsArchive && !IsFolder;
        public bool IsFolder { get; set; }

        public int FileCount { get; set; }
    }

    public required Node Root { get; init; }
    public required RpfFileSystem Files { get; init; }

    public required IReadOnlyList<Node> AllFiles { get; init; }

    public static ArchiveTree Build(RpfFileSystem files)
    {
        var root = new Node("", "", null) { IsFolder = true, Depth = -1 };
        var byPath = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        var archives = files.EnumerateArchives().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Node Get(string path)
        {
            if (byPath.TryGetValue(path, out var node))
                return node;
            int cut = path.LastIndexOf('\\');
            var parent = Get(cut < 0 ? "" : path[..cut]);
            node = new Node(cut < 0 ? path : path[(cut + 1)..], path, parent) { Depth = parent.Depth + 1, IsFolder = true };
            parent.Children.Add(node);
            byPath[path] = node;
            return node;
        }

        var all = new List<Node>();
        foreach (var archive in archives)
            Get(archive).IsArchive = true;
        foreach (var path in files.AllFiles)
        {
            if (archives.Contains(path))
                continue;
            var node = Get(path);
            node.IsFolder = false;
            all.Add(node);
        }
        foreach (var node in byPath.Values)
        {
            if (node.IsArchive)
                node.IsFolder = false;
        }

        Sort(root);
        all.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new ArchiveTree { Root = root, Files = files, AllFiles = all };
    }

    private static int Sort(Node node)
    {
        node.Children.Sort((a, b) =>
        {
            int kind = (a.IsFile ? 1 : 0).CompareTo(b.IsFile ? 1 : 0);
            return kind != 0 ? kind : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        int count = node.IsFile ? 1 : 0;
        foreach (var child in node.Children)
            count += Sort(child);
        node.FileCount = count;
        return count;
    }

    // A file as it would sit on disk: binary files decompressed, resources (.ydr, .ymap...) in their usual "RSC7"
    // form (readable by the modding tools and usable in a FiveM "stream" folder).
    public byte[]? Extract(string path)
    {
        if (Files.ReadFile(path) is not { } data)
            return null;
        return Files.GetResourceHeader(path) is { } header ? ResourceFile.Write(header, data) : data;
    }

    // Writes every file below node into folder, keeping the folders (an archive becomes a folder of the same name).
    public (int Written, int Failed) ExtractAll(Node node, string folder, IProgress<(int Done, int Total)>? progress,
        CancellationToken cancel)
    {
        var files = new List<Node>();
        Collect(node, files);
        int written = 0, failed = 0;
        string baseFolder = Path.Combine(folder, node.Name);
        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var file = files[i];
            try
            {
                string relative = file.Path[(node.Path.Length + 1)..];
                string target = Path.Combine(baseFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Extract(file.Path) is { } bytes)
                {
                    File.WriteAllBytes(target, bytes);
                    written++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed++;
            }
            if (i % 50 == 0)
                progress?.Report((i, files.Count));
        }
        progress?.Report((files.Count, files.Count));
        return (written, failed);
    }

    private static void Collect(Node node, List<Node> files)
    {
        if (node.IsFile)
        {
            files.Add(node);
            return;
        }
        foreach (var child in node.Children)
            Collect(child, files);
    }
}
