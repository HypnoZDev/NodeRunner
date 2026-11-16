using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.App.World;

// Reads some texture dictionaries from data kept in memory (changed in the workshop, not saved yet), every other file
// as usual.
internal sealed class PatchedTextureReader(ITextureReader inner, IReadOnlyDictionary<string, (ResourceHeader Header, byte[] Content)> files)
    : ITextureReader
{
    public IReadOnlyDictionary<uint, TextureData> ReadTextures(string path) =>
        files.TryGetValue(path, out var file) ? YtdReader.Read(file.Content, file.Header) : inner.ReadTextures(path);
}
