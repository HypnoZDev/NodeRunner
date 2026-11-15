using System.Text.RegularExpressions;
using NodeRunner.Data.Native.Paths;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;

namespace NodeRunner.App.World;

// Writes the modified road network files (nodesN.ynd) to a folder, with their game name, like MapFileSaver.
internal static partial class PathFileSaver
{
    // Saved positions are rounded by the file format (1/4 m across, 1/32 m up).
    private const float Tolerance = 0.2f;

    public static void Save(WorldLoader world, IEnumerable<int> areaIds, string folder, MapFileSaver.Report report, bool markSaved = true)
    {
        if (world.Files is not { } files || world.PathNetwork is not { } network)
            return;
        Directory.CreateDirectory(folder);
        var writer = new YndWriter();
        foreach (int areaId in areaIds)
        {
            string name = PathNetwork.FileName(areaId);
            try
            {
                if (network.GetArea(areaId) is not { } area || network.FilePath(areaId) is not { } path
                    || files.GetResourceHeader(path) is not { } header)
                    throw new InvalidDataException(L("fichier du jeu introuvable"));
                var file = writer.Write(area, network.LinkLength, header.Version);
                Verify(file, area);

                string target = Path.Combine(folder, name);
                string temporary = target + ".tmp";
                File.WriteAllBytes(temporary, file);
                File.Move(temporary, target, overwrite: true);
                if (markSaved)
                    network.MarkSaved(areaId);
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    // The .ynd files of a folder, uncompressed, by area number; unreadable ones are reported in errors.
    public static Dictionary<int, byte[]> ReadSaved(string? folder, List<string> errors)
    {
        var areas = new Dictionary<int, byte[]>();
        if (folder is null || !Directory.Exists(folder))
            return areas;
        foreach (var file in Directory.EnumerateFiles(folder, "*.ynd"))
        {
            var match = NodesFileName().Match(Path.GetFileName(file));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int areaId) || areaId >= PathNodeArea.AreaCount)
                continue;
            try
            {
                areas[areaId] = ResourceFile.Read(File.ReadAllBytes(file)).Content;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)} : {ex.Message}");
            }
        }
        return areas;
    }

    private static void Verify(byte[] file, PathNodeArea area)
    {
        var written = new YndReader().Read(ResourceFile.Read(file).Content, area.AreaId);
        if (written.Nodes.Count != area.Nodes.Count || written.Links.Count != area.Links.Count
            || written.VehicleNodeCount != area.VehicleNodeCount)
            throw new InvalidDataException(L("vérification échouée (nombre de nœuds)"));
        for (int i = 0; i < written.Nodes.Count; i++)
        {
            var difference = written.Nodes[i].Position - area.Nodes[i].Position;
            if (MathF.Abs(difference.X) > Tolerance || MathF.Abs(difference.Y) > Tolerance || MathF.Abs(difference.Z) > Tolerance)
                throw new InvalidDataException(L($"vérification échouée (nœud n° {i}, position hors des limites du format ?)"));
            if (written.Nodes[i].Values != area.Nodes[i].Values || written.Nodes[i].Id != area.Nodes[i].Id
                || written.Nodes[i].LinkCount != area.Nodes[i].LinkCount || (written.Nodes[i].Junction is null) != (area.Nodes[i].Junction is null))
                throw new InvalidDataException(L($"vérification échouée (nœud n° {i}, réglages)"));
        }
    }

    [GeneratedRegex(@"^nodes(\d+)\.ynd$", RegexOptions.IgnoreCase)]
    private static partial Regex NodesFileName();
}
