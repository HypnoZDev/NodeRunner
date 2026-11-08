using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A car of the game drawn on the selected parking spot: its model if it has one, otherwise a model of its group, or
// an ordinary car.
internal sealed class CarGeneratorPreview : IDisposable
{
    // Ordinary cars for a spot without model or group.
    private static readonly string[] DefaultModels = ["asea", "premier", "tailgater", "primo", "stanier", "fugitive", "ingot", "washington"];

    private readonly PreviewModel _model = new();

    public string? ModelName => _model.Item?.Name;
    public bool IsLoading => _model.IsLoading;
    public string? Error => _model.Error;

    // How the model was chosen, for the Properties panel.
    public string? Source { get; private set; }

    public void Update(WorldLoader world, ModelRenderer renderer, CarGeneratorSelection? selection)
    {
        if (selection?.Spot is not { } spot || Choose(world, selection, spot) is not { } item)
        {
            if (_model.Item is not null)
                _model.Close();
            Source = null;
            return;
        }
        _model.Open(item, world);
        _model.Update(renderer);
    }

    private ViewerItem? Choose(WorldLoader world, CarGeneratorSelection selection, CarGenerator spot)
    {
        if (world.Models is not { } models)
            return null;
        ViewerItem? Item(string name, uint hash) =>
            models.FragmentPath(hash) is { } path ? new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(path, hash, hash)) : null;

        if (spot.CarModel != 0)
        {
            Source = L("modèle de la place");
            return Item(world.Names?.TryGet(spot.CarModel) ?? $"0x{spot.CarModel:X8}", spot.CarModel);
        }
        // A different model of the list for each spot, always the same one for a given spot.
        var group = world.ModelSets.ModelsOf(spot.PopGroup).Where(m => models.FragmentPath(Joaat.Hash(m)) is not null).ToList();
        if (group.Count > 0)
        {
            Source = L("au hasard dans le groupe");
            string name = group[selection.Index % group.Count];
            return Item(name, Joaat.Hash(name));
        }
        Source = L("exemple (le jeu choisit un véhicule de la zone)");
        string fallback = DefaultModels[selection.Index % DefaultModels.Length];
        return Item(fallback, Joaat.Hash(fallback));
    }

    // Draws the car standing on ground, facing the front of the spot.
    public void Draw(ModelRenderer renderer, CarGenerator spot, float ground, Matrix4x4 viewProjection)
    {
        if (_model.Model is not { } model)
            return;
        var min = _model.BoundsMin;
        var max = _model.BoundsMax;
        // A vehicle faces +Y in its model.
        var world = Matrix4x4.CreateTranslation(-(min.X + max.X) / 2, -(min.Y + max.Y) / 2, -min.Z)
                    * Matrix4x4.CreateRotationZ(spot.Angle - MathF.PI / 2)
                    * Matrix4x4.CreateTranslation(spot.Position.X, spot.Position.Y, ground);
        renderer.Begin(ModelPass.Solid);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.Begin(ModelPass.Translucent);
        renderer.Draw(model, world, viewProjection, Vector3.One);
    }

    public void Dispose() => _model.Dispose();
}
