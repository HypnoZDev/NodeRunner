using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;

namespace NodeRunner.App.World;

// Reads one model file from data kept in memory (a model being changed in the workshop, not saved yet), every other
// file as usual.
internal sealed class PatchedModelReader(IModelReader inner, string path, ResourceHeader header, byte[] content) : IModelReader
{
    public IReadOnlyDictionary<uint, ModelData> ReadModels(string file) => ReadModels(file, null);

    public IReadOnlyDictionary<uint, ModelData> ReadModels(string file, string? skeletonPath) =>
        string.Equals(file, path, StringComparison.OrdinalIgnoreCase)
            ? DrawableReader.Read(content, header, Path.GetFileNameWithoutExtension(file), Path.GetExtension(file))
            : inner.ReadModels(file, skeletonPath);
}
