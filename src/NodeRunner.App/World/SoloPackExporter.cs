using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Exports the modified map files as a single-player DLC pack (dlc.rpf): the FiveM export is written to a temporary
// folder, then its files are packed.
internal static class SoloPackExporter
{
    public static MapFileSaver.Report Export(WorldLoader world, IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string packName,
        string dlcRpf, IReadOnlyList<CollisionFileCut> collisionCuts, IReadOnlyList<OccluderFileCut> occlusionCuts, CollisionEdits collisionEdits,
        string occluderFileName, IReadOnlyList<string> interiorFiles, string? projectStream, out List<string> copied, out List<string> fromLibrary)
    {
        string temp = Path.Combine(Path.GetTempPath(), "NodeRunner", "pack-" + Guid.NewGuid().ToString("N"));
        string resource = Path.Combine(temp, packName);
        try
        {
            var report = ResourceExporter.Export(world, maps, pathAreas, resource, collisionCuts, occlusionCuts, collisionEdits,
                occluderFileName, interiorFiles, projectStream, out copied, out fromLibrary);
            if (report.Errors.Count > 0)
                return report;
            string stream = Path.Combine(resource, FiveMResource.StreamFolderName);
            var files = Directory.Exists(stream) ? Directory.GetFiles(stream) : [];
            if (files.Length == 0)
            {
                report.Errors.Add(L("aucun fichier à mettre dans le pack"));
                return report;
            }
            try
            {
                Pack(world, packName, files, Path.Combine(resource, FiveMResource.AudioGameDataFile(packName)), dlcRpf, temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                report.Errors.Add($"{SoloPack.ArchiveName} : {ex.Message}");
            }
            return report;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, which Windows empties.
            }
        }
    }

    private static void Pack(WorldLoader world, string name, string[] files, string audioGameData, string dlcRpf, string temp)
    {
        var maps = new RpfWriter();
        foreach (var file in files)
            maps.AddFile(Path.GetFileName(file), file);
        string mapArchive = Path.Combine(temp, name + ".rpf");
        maps.Write(mapArchive);

        bool hasAudio = File.Exists(audioGameData);
        var newTypes = ResourceExporter.NewTypeFiles(world, files.Select(Path.GetFileName).OfType<string>());
        int order = (world.Content?.HighestPackOrder ?? 0) + 1;
        var pack = new RpfWriter();
        pack.Add(SoloPack.SetupFileName, Encoding.UTF8.GetBytes(SoloPack.BuildSetup(name, order, DateTime.Now)));
        pack.Add(SoloPack.ContentFileName, Encoding.UTF8.GetBytes(SoloPack.BuildContent(name, newTypes, hasAudio)));
        pack.AddFile(SoloPack.MapArchivePath(name), mapArchive);
        if (hasAudio)
            pack.AddFile(SoloPack.AudioGameDataPath(name), audioGameData);

        // Written beside, then moved: a failed export does not leave half a pack in place of the previous one.
        string? folder = Path.GetDirectoryName(Path.GetFullPath(dlcRpf));
        if (folder is not null)
            Directory.CreateDirectory(folder);
        string partial = dlcRpf + ".tmp";
        try
        {
            pack.Write(partial);
            File.Move(partial, dlcRpf, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }
}
