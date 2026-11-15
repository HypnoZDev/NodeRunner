using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Scenarios;

// sp_manifest.ymt (PSO): the box of each scenario region, inside which the game loads the region's file. Points moved
// or added outside it would never be loaded: the boxes are grown to hold them (never shrunk), in place.
public static class ScenarioManifest
{
    public const string FileName = "sp_manifest.ymt";

    // The newest copy first.
    public static readonly string[] GamePaths =
    [
        @"update\update.rpf\x64\levels\gta5\sp_manifest.ymt",
        @"x64a.rpf\levels\gta5\sp_manifest.ymt",
    ];

    private const uint DataSection = 0x5053494E; // "PSIN"
    private const uint MapSection = 0x504D4150;  // "PMAP"
    private const uint RegionDefStructure = 1251976652;
    private const int RegionDefSize = 64;
    private const int BoxMin = 16, BoxMax = 32;

    public static uint RegionHash(string region) => Joaat.Hash("platform:/levels/gta5/scenario/" + region);

    // The manifest with the boxes of these regions grown to hold the given bounds, or null when every one already fits.
    // Regions the manifest does not list are left out (reported in missing).
    public static byte[]? Grow(ReadOnlySpan<byte> manifest, IEnumerable<(string Region, Vector3 Min, Vector3 Max)> bounds, List<string>? missing = null)
    {
        if (manifest.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(manifest) != DataSection)
            throw new InvalidDataException("sp_manifest.ymt : ce n'est pas un fichier PSO.");
        var data = manifest.ToArray();
        var defs = RegionDefs(data);
        bool changed = false;
        foreach (var (region, min, max) in bounds)
        {
            if (!defs.TryGetValue(RegionHash(region), out int at))
            {
                missing?.Add(region);
                continue;
            }
            var oldMin = ReadVector(data, at + BoxMin);
            var oldMax = ReadVector(data, at + BoxMax);
            var newMin = Vector3.Min(oldMin, min);
            var newMax = Vector3.Max(oldMax, max);
            if (newMin == oldMin && newMax == oldMax)
                continue;
            WriteVector(data, at + BoxMin, newMin);
            WriteVector(data, at + BoxMax, newMax);
            changed = true;
        }
        return changed ? data : null;
    }

    // Offset of each region definition, by name hash.
    private static Dictionary<uint, int> RegionDefs(byte[] data)
    {
        int map = -1;
        for (int at = 0; at + 8 <= data.Length;)
        {
            uint ident = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 4));
            if (ident == MapSection)
                map = at;
            if (length < 8 || at + length > data.Length)
                break;
            at += length;
        }
        if (map < 0)
            throw new InvalidDataException("sp_manifest.ymt : section PMAP absente.");
        var defs = new Dictionary<uint, int>();
        int count = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(map + 12));
        for (int i = 0; i < count; i++)
        {
            int e = map + 16 + i * 16;
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(e)) != RegionDefStructure)
                continue;
            int offset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(e + 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(e + 12));
            if (offset < 0 || offset + length > data.Length)
                throw new InvalidDataException("sp_manifest.ymt : bloc hors du fichier.");
            for (int at = offset; at + RegionDefSize <= offset + length; at += RegionDefSize)
                defs.TryAdd(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)), at);
        }
        return defs;
    }

    private static Vector3 ReadVector(byte[] data, int at) => new(
        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(at)),
        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(at + 8)));

    private static void WriteVector(byte[] data, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(at + 8), v.Z);
    }
}
