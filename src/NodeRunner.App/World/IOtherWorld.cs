using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;
using NodeRunner.Data.Water;

namespace NodeRunner.App.World;

// A game other than GTA V (GTA IV, the first trilogy), shown read only: its map in the form the 3D view draws, read on
// a worker thread.
internal interface IOtherWorld
{
    bool IsBusy { get; }

    string Status { get; }

    string? Error { get; }

    bool IsLoaded { get; }

    MapLibrary? Library { get; }

    ModelCatalog? Models { get; }

    TextureCatalog? Textures { get; }

    HashNames? Names { get; }

    IModelReader ModelReader { get; }

    ITextureReader TextureReader { get; }

    WaterMap? Water { get; }

    // Short name for messages ("GTA IV", "San Andreas").
    string GameName { get; }

    // Name of a placement file kind ("WPL", "IPL").
    string MapFileKind { get; }
}
