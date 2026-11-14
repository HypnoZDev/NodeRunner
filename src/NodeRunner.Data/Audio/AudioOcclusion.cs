using System.Numerics;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Audio;

// How sound goes through one portal of an interior (naOcclusionPortalInfoMetadata). Rooms and portals are numbered as
// in the interior's archetype; PortalIndex counts the portals of RoomIndex only.
public sealed record AudioPortalLink(uint InteriorProxyHash, int PortalIndex, int RoomIndex, uint DestInteriorProxyHash,
    int DestRoomIndex, IReadOnlyList<AudioPortalEntity> Entities);

// A door or window in a portal, that muffles the sound while closed.
public readonly record struct AudioPortalEntity(uint LinkType, float MaxOcclusion, uint ModelHash, bool IsDoor, bool IsGlass);

// One step of the precomputed sound paths between rooms.
public sealed record AudioPathNode(uint Key, IReadOnlyList<AudioPathChild> Children);

public readonly record struct AudioPathChild(uint ParentKey, int PortalLinkIndex);

// Audio occlusion of one placed interior (<proxy hash>.ymt, naOcclusionInteriorMetadata).
public sealed record AudioOcclusion(IReadOnlyList<AudioPortalLink> Links, IReadOnlyList<AudioPathNode> Paths);

public interface IAudioOcclusionReader
{
    AudioOcclusion Read(byte[] data);
}

public static class AudioOcclusionHash
{
    // Name of the file the game looks for, from the interior instance: archetype hash xor each coordinate x 100,
    // truncated (checked on 754 of the game's interiors).
    public static uint Of(uint archetypeHash, Vector3 position) =>
        archetypeHash ^ (uint)(int)(position.X * 100) ^ (uint)(int)(position.Y * 100) ^ (uint)(int)(position.Z * 100);
}

// The audio occlusion files of the game and of the project (named by proxy hash), read one at a time when asked for.
public sealed class AudioOcclusionCatalog
{
    private readonly GameContent _content;
    private readonly IAudioOcclusionReader _reader;
    private readonly Dictionary<uint, string> _paths;
    private readonly Dictionary<uint, (AudioOcclusion? Occlusion, string? Error)> _read = new();

    private AudioOcclusionCatalog(GameContent content, IAudioOcclusionReader reader, Dictionary<uint, string> paths)
    {
        _content = content;
        _reader = reader;
        _paths = paths;
    }

    public int Count => _paths.Count;

    public static AudioOcclusionCatalog Load(GameContent content, IAudioOcclusionReader reader)
    {
        var paths = new Dictionary<uint, string>();
        // Some packs do not declare their occlusion archive: files of undeclared archives are taken too (the name
        // depends on where the interior is placed, so a file can only match its own interior).
        foreach (var path in content.EnumerateFiles(".ymt", withInactiveFallback: true))
            if (uint.TryParse(Path.GetFileNameWithoutExtension(path), out uint hash))
                paths[hash] = path;
        return new AudioOcclusionCatalog(content, reader, paths);
    }

    public string? PathOf(uint proxyHash) => _paths.GetValueOrDefault(proxyHash);

    // The file's content, or the reason it could not be read; (null, null) when there is no such file.
    public (AudioOcclusion? Occlusion, string? Error) Get(uint proxyHash)
    {
        if (!_paths.TryGetValue(proxyHash, out var path))
            return (null, null);
        lock (_read)
        {
            if (_read.TryGetValue(proxyHash, out var done))
                return done;
        }
        (AudioOcclusion?, string?) result;
        try
        {
            result = _content.ReadFile(path) is { } data ? (_reader.Read(data), null) : (null, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException
            or IndexOutOfRangeException or OverflowException)
        {
            result = (null, ex.Message);
        }
        lock (_read)
            _read[proxyHash] = result;
        return result;
    }
}
