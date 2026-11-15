using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Scenarios;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A ped, animal or vehicle the game could spawn on the selected scenario point, standing still on it.
internal sealed class ScenarioPreview : IDisposable
{
    private readonly PreviewModel _model = new();
    private ViewerItem? _item;
    private (ScenarioPoint Point, uint Type, int Index, ScenarioRegion? Region)? _for;
    private Vector3[]? _paint;

    public string? ModelName => _model.Item?.Name;
    public bool IsLoading => _model.IsLoading;
    public string? Error => _model.Error;
    public ScenarioModelSource? Source { get; private set; }

    public void Update(WorldLoader world, ModelRenderer renderer, ScenarioPoint? point)
    {
        if (point is null || world.Scenarios is null || world.Models is null)
        {
            if (_model.Item is not null)
                _model.Close();
            (_item, _for, Source) = (null, null, null);
            return;
        }
        // Chosen again only when the point or its type changes, not while it is moved.
        var key = (point, point.TypeHash, point.Index, point.Region);
        if (_for != key)
        {
            _for = key;
            _item = Choose(world, point);
        }
        if (_item is null)
        {
            if (_model.Item is not null)
                _model.Close();
            return;
        }
        _model.Open(_item, world);
        _model.Update(renderer);
    }

    private ViewerItem? Choose(WorldLoader world, ScenarioPoint point)
    {
        var models = world.Models!;
        bool vehicle = point.Category == ScenarioCategory.Vehicle;
        // A number proper to the point: the same point always shows the same model.
        int variant = (int)(Joaat.Hash(point.Region?.Name ?? "") ^ (uint)point.Index * 0x9E3779B1u);
        Func<string, bool> exists = vehicle
            ? name => models.FragmentPath(Joaat.Hash(name)) is not null
            : name => models.HasPed(Joaat.Hash(name));
        if (ScenarioModels.Choose(point, world.Scenarios!, world.PedSets, world.VehicleSets, exists, variant) is not { } choice)
        {
            Source = null;
            return null;
        }
        var (name, source) = choice;
        Source = source;
        uint hash = Joaat.Hash(name);
        _paint = null;
        if (vehicle)
        {
            if (world.Vehicles is { } vehicles)
            {
                var sets = vehicles.Paints.VariationOf(hash)?.ColorSets;
                _paint = VehiclePaintColors.From(vehicles.Paints,
                    sets is { Count: > 0 } ? sets[(int)((uint)variant % (uint)sets.Count)].Indices : VehiclePaintColors.Default);
            }
            return new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(models.FragmentPath(hash)!, hash, hash));
        }
        string? dictionary = models.DictionaryPath(hash);
        var (parts, textures) = models.PedParts(hash);
        // Peds stored one part per file are the story characters and some animals: their usual clothes.
        var files = new PedFiles(dictionary, models.FragmentPath(hash), parts, textures, dictionary is null ? null : variant);
        return new ViewerItem(name, ViewerKind.Ped, new ModelLocation(dictionary ?? parts.FirstOrDefault() ?? "", hash, hash)) { Ped = files };
    }

    // Draws the model standing on the point, facing its heading (peds and vehicles face +Y in their model).
    public void Draw(ModelRenderer renderer, ScenarioPoint point, Matrix4x4 viewProjection)
    {
        if (_model.Model is not { } model || _model.Item is not { } item)
            return;
        var min = _model.BoundsMin;
        var max = _model.BoundsMax;
        // A ped stays on its root bone (between its feet); a vehicle is centred.
        var centre = item.Kind == ViewerKind.Vehicle ? new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2) : Vector2.Zero;
        var world = Matrix4x4.CreateTranslation(-centre.X, -centre.Y, -min.Z)
                    * Matrix4x4.CreateRotationZ(point.Heading)
                    * Matrix4x4.CreateTranslation(point.Position);
        renderer.VehiclePaint = item.Kind == ViewerKind.Vehicle ? _paint : null;
        renderer.Begin(ModelPass.Solid);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.Begin(ModelPass.Translucent);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.VehiclePaint = null;
    }

    public void Dispose() => _model.Dispose();
}
