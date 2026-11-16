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

    // The clip the ped or animal plays (null while read, or none).
    public AnimationClip? Clip { get; private set; }
    private Matrix4x4[]? _pose;

    public void Update(WorldLoader world, ModelRenderer renderer, ScenarioClips clips, ScenarioPoint? point)
    {
        if (point is null || world.Scenarios is null || world.Models is null)
        {
            if (_model.Item is not null)
                _model.Close();
            (_item, _for, Source, Clip) = (null, null, null, null);
            return;
        }
        // Chosen again only when the point or its type changes, not while it is moved.
        var key = (point, point.TypeHash, point.Index, point.Region);
        if (_for != key)
        {
            _for = key;
            _item = Choose(world, point);
            Clip = null;
        }
        if (_item is null)
        {
            if (_model.Item is not null)
                _model.Close();
            return;
        }
        _model.Open(_item, world);
        _model.Update(renderer);
        if (Clip is null && _model.Model is { IsSkinned: true } && _item.Kind == ViewerKind.Ped)
            Clip = clips.Get(world, point.TypeHash, _item.Name, Variant(point));
    }

    private ViewerItem? Choose(WorldLoader world, ScenarioPoint point)
    {
        int variant = Variant(point);
        var chosen = Choose(world, point, variant, variant);
        (Source, _paint) = (chosen?.Source, chosen?.Paint);
        return chosen?.Item;
    }

    // A number proper to the point: the same point always shows the same model.
    public static int Variant(ScenarioPoint point) => (int)(Joaat.Hash(point.Region?.Name ?? "") ^ (uint)point.Index * 0x9E3779B1u);

    // The model for the point: variant picks it among those the point can take, outfit the clothes of a ped.
    public static (ViewerItem Item, Vector3[]? Paint, ScenarioModelSource Source)? Choose(WorldLoader world, ScenarioPoint point, int variant, int outfit)
    {
        var models = world.Models!;
        bool vehicle = point.Category == ScenarioCategory.Vehicle;
        Func<string, bool> exists = vehicle
            ? name => models.FragmentPath(Joaat.Hash(name)) is not null
            : name => models.HasPed(Joaat.Hash(name));
        if (ScenarioModels.Choose(point, world.Scenarios!, world.PedSets, world.VehicleSets, exists, variant) is not { } choice)
            return null;
        var (name, source) = choice;
        uint hash = Joaat.Hash(name);
        if (vehicle)
        {
            Vector3[]? paint = null;
            if (world.Vehicles is { } vehicles)
            {
                var sets = vehicles.Paints.VariationOf(hash)?.ColorSets;
                paint = VehiclePaintColors.From(vehicles.Paints,
                    sets is { Count: > 0 } ? sets[(int)((uint)variant % (uint)sets.Count)].Indices : VehiclePaintColors.Default);
            }
            return (new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(models.FragmentPath(hash)!, hash, hash)), paint, source);
        }
        string? dictionary = models.DictionaryPath(hash);
        var (parts, textures) = models.PedParts(hash);
        // Peds stored one part per file are the story characters and some animals: their usual clothes.
        var files = new PedFiles(dictionary, models.FragmentPath(hash), parts, textures, dictionary is null ? null : outfit);
        return (new ViewerItem(name, ViewerKind.Ped, new ModelLocation(dictionary ?? parts.FirstOrDefault() ?? "", hash, hash)) { Ped = files },
            null, source);
    }

    // Draws the model standing on the point, facing its heading.
    public void Draw(ModelRenderer renderer, ScenarioPoint point, Matrix4x4 viewProjection)
    {
        if (_model.Model is not { } model || _model.Item is not { } item)
            return;
        var world = Placement(_model, item.Kind, point);
        renderer.VehiclePaint = item.Kind == ViewerKind.Vehicle ? _paint : null;
        renderer.BonePose = model.IsSkinned && ScenarioClips.Pose(_model.Data, Clip, 0f, ref _pose) ? _pose : null;
        renderer.Begin(ModelPass.Solid);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.Begin(ModelPass.Translucent);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.VehiclePaint = null;
        renderer.BonePose = null;
    }

    // Peds and vehicles face +Y in their model; a ped stays on its root bone (between its feet), a vehicle is centred.
    public static Matrix4x4 Placement(PreviewModel model, ViewerKind kind, ScenarioPoint point)
    {
        var min = model.BoundsMin;
        var max = model.BoundsMax;
        var centre = kind == ViewerKind.Vehicle ? new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2) : Vector2.Zero;
        return Matrix4x4.CreateTranslation(-centre.X, -centre.Y, -min.Z)
               * Matrix4x4.CreateRotationZ(point.Heading)
               * Matrix4x4.CreateTranslation(point.Position);
    }

    public void Dispose() => _model.Dispose();
}
