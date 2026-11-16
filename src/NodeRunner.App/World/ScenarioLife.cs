using System.Numerics;
using NodeRunner.Data.Models;
using NodeRunner.Data.Scenarios;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// The scenario points near the camera that the game would use at the hour shown, each with a ped, animal or vehicle
// standing still on it. Models are read in the background a few at a time and shared between points.
internal sealed class ScenarioLife : IDisposable
{
    public const float Radius = 150f;
    public const int MaxShown = 60;
    private const int MaxModels = 24;
    private const int MaxLoading = 2;
    private const int Outfits = 2;
    private const float MoveBeforeRescan = 10f;
    // Points within Radius + Margin are kept aside, so that a rescan does not go through the whole map.
    private const float Margin = 100f;

    private sealed class Slot(ViewerItem item)
    {
        public readonly ViewerItem Item = item;
        public readonly PreviewModel Model = new();
        public bool Opened;
        public long LastUsed;
    }

    private sealed class Spawn(ScenarioPoint point, Slot slot, Vector3[]? paint, int variant)
    {
        public readonly ScenarioPoint Point = point;
        public readonly Slot Slot = slot;
        public readonly Vector3[]? Paint = paint;
        public readonly int Variant = variant;
        // The clip a ped or animal plays (null while read, or none), and where its bones are this frame.
        public ScenarioClip? Clip;
        public Matrix4x4[]? Pose;
        public bool Posed;
        public bool Seated;
        // The object in its hand, this frame (null while read or for none).
        public GpuModel? Prop;

        // Shift in the clip, so that neighbours do not move together.
        public float Phase => (Variant & 0xFFFF) / 65535f * 60f;
    }

    private readonly Dictionary<string, Slot> _slots = new();
    private List<Spawn> _spawns = [];
    private (ScenarioSet? Set, int Revision, int Hour, bool Online, int Visible, object? Models) _for;
    private Vector3 _centre = new(float.NaN);
    private List<ScenarioPoint> _area = [];
    private (ScenarioSet? Set, int Revision) _areaFor;
    private Vector3 _areaCentre = new(float.NaN);
    private long _frame;

    // Points used within the radius (at most MaxShown), and those whose model is drawn.
    public int Active => _spawns.Count;
    public int Shown { get; private set; }
    public bool IsLoading { get; private set; }

    public void Update(WorldLoader world, ModelRenderer renderer, ScenarioClips clips, Vector3 camera, int hour, bool online,
        Func<ScenarioCategory, bool> isVisible, bool enabled)
    {
        if (!enabled || world.Scenarios is not { } scenarios || world.Models is null)
        {
            Clear();
            return;
        }
        _frame++;
        int visible = 0;
        foreach (var category in ScenarioLayer.Categories)
        {
            if (isVisible(category))
                visible |= 1 << (int)category;
        }
        var key = (scenarios, scenarios.Revision, hour, online, visible, (object?)world.Models);
        if (!ReferenceEquals(_for.Models, world.Models))
            Clear(); // Another game or installation: its models are other ones.
        if (key != _for || !(Vector3.DistanceSquared(camera, _centre) < MoveBeforeRescan * MoveBeforeRescan))
        {
            (_for, _centre) = (key, camera);
            Rescan(world, scenarios, camera, hour, online, visible);
        }

        // Also the models no point uses now, so that one still being read gets finished.
        foreach (var slot in _slots.Values)
            slot.Model.Update(renderer);
        // Nearest first: their models are read before the others.
        int loading = _slots.Values.Count(s => s.Model.IsLoading);
        int shown = 0;
        foreach (var spawn in _spawns)
        {
            var slot = spawn.Slot;
            slot.LastUsed = _frame;
            if (!slot.Opened && loading < MaxLoading)
            {
                slot.Model.Open(slot.Item, world);
                slot.Opened = true;
                loading++;
            }
            if (slot.Model.Model is not { } model)
                continue;
            shown++;
            if (spawn.Clip is null && model.IsSkinned && slot.Item.Kind == ViewerKind.Ped)
                spawn.Clip = clips.Get(world, spawn.Point.TypeHash, slot.Item.Name, spawn.Variant);
            spawn.Prop = spawn.Clip?.Prop is { } prop ? clips.PropModel(world, prop) : null;
        }
        Shown = shown;
        IsLoading = _spawns.Any(s => !s.Slot.Opened || s.Slot.Model.IsLoading);
    }

    private void Rescan(WorldLoader world, ScenarioSet scenarios, Vector3 camera, int hour, bool online, int visible)
    {
        if (_areaFor != (scenarios, scenarios.Revision) || !(Vector3.DistanceSquared(camera, _areaCentre) < Margin * Margin))
        {
            (_areaFor, _areaCentre) = ((scenarios, scenarios.Revision), camera);
            float reach = (Radius + Margin) * (Radius + Margin);
            _area = scenarios.AllPoints.Where(p => Vector3.DistanceSquared(p.Position, camera) <= reach).ToList();
        }
        var near = new List<(ScenarioPoint Point, float Distance)>();
        foreach (var point in _area)
        {
            float d = Vector3.DistanceSquared(point.Position, camera);
            if (d <= Radius * Radius && (visible & 1 << (int)point.Category) != 0 && ScenarioSchedule.IsUsed(point, scenarios, hour, online))
                near.Add((point, d));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var spawns = new List<Spawn>();
        var used = new HashSet<Slot>();
        foreach (var (point, _) in near)
        {
            if (spawns.Count >= MaxShown)
                break;
            int variant = ScenarioPreview.Variant(point);
            int outfit = (int)((uint)variant % Outfits);
            if (ScenarioPreview.Choose(world, point, variant, outfit) is not { } choice)
                continue;
            string id = $"{choice.Item.Kind}:{choice.Item.Name}:{(choice.Item.Kind == ViewerKind.Ped ? outfit : 0)}";
            if (!_slots.TryGetValue(id, out var slot))
            {
                // No room for another model: the point waits until one is freed.
                if (used.Count >= MaxModels)
                    continue;
                _slots[id] = slot = new Slot(choice.Item);
            }
            else if (!used.Contains(slot) && used.Count >= MaxModels)
                continue;
            used.Add(slot);
            // A point still shown keeps its clip and pose.
            var kept = _spawns.Find(s => s.Point == point && s.Slot == slot);
            spawns.Add(kept ?? new Spawn(point, slot, choice.Paint, variant));
        }
        _spawns = spawns;

        // Models no point uses any more, the oldest first, until there are few enough.
        foreach (var (id, slot) in _slots.Where(p => !used.Contains(p.Value)).OrderBy(p => p.Value.LastUsed).ToList())
        {
            if (_slots.Count <= MaxModels)
                break;
            slot.Model.Dispose();
            _slots.Remove(id);
        }
    }

    // skip: a point drawn by the preview of the selection.
    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection, ScenarioPoint? skip)
    {
        if (Shown == 0)
            return;
        foreach (var spawn in _spawns)
        {
            spawn.Posed = spawn.Slot.Model.Model is { IsSkinned: true }
                && ScenarioClips.Pose(spawn.Slot.Model.Data, spawn.Clip?.Clip, spawn.Phase, ref spawn.Pose);
            spawn.Seated = spawn.Posed && ScenarioClips.Seated(spawn.Slot.Model.Data, spawn.Pose);
        }
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            foreach (var spawn in _spawns)
            {
                if (spawn.Point == skip || spawn.Slot.Model.Model is not { } model)
                    continue;
                var kind = spawn.Slot.Item.Kind;
                renderer.VehiclePaint = kind == ViewerKind.Vehicle ? spawn.Paint : null;
                renderer.BonePose = spawn.Posed ? spawn.Pose : null;
                var placement = ScenarioPreview.Placement(spawn.Slot.Model, kind, spawn.Point, spawn.Seated);
                renderer.Draw(model, placement, viewProjection, Vector3.One);
                if (spawn.Posed && spawn.Prop is { } prop && ScenarioClips.Hand(spawn.Slot.Model.Data, spawn.Pose, spawn.Clip!.LeftHand) is { } hand)
                {
                    renderer.BonePose = null;
                    renderer.Draw(prop, hand * placement, viewProjection, Vector3.One);
                }
            }
        }
        renderer.VehiclePaint = null;
        renderer.BonePose = null;
    }

    // The point of the ped, animal or vehicle drawn at pixel (x, y) of the view (as drawn by the last Draw: same pose),
    // and where it was hit; null when none is there.
    public (ScenarioPoint Point, Vector3 Hit)? Pick(ModelRenderer renderer, PickBuffer buffer, Matrix4x4 viewProjection, int width, int height,
        int x, int y, ScenarioPoint? skip)
    {
        if (Shown == 0)
            return null;
        buffer.Begin(width, height);
        renderer.BeginPick();
        for (int i = 0; i < _spawns.Count; i++)
        {
            var spawn = _spawns[i];
            if (spawn.Point == skip || spawn.Slot.Model.Model is not { } model)
                continue;
            renderer.BonePose = spawn.Posed ? spawn.Pose : null;
            renderer.DrawPick(model, ScenarioPreview.Placement(spawn.Slot.Model, spawn.Slot.Item.Kind, spawn.Point, spawn.Seated), viewProjection, (uint)(i + 1));
        }
        renderer.BonePose = null;
        uint number = buffer.Read(x, y);
        if (number < 1 || number > _spawns.Count)
            return null;
        float depth = buffer.ReadDepth(x, y);
        var hit = _spawns[(int)number - 1].Point.Position;
        if (depth > 0f && Matrix4x4.Invert(viewProjection, out var inverse))
        {
            var world = Vector4.Transform(new Vector4((x + 0.5f) / width * 2f - 1f, 1f - (y + 0.5f) / height * 2f, depth, 1f), inverse);
            if (Math.Abs(world.W) > 1e-12f)
                hit = new Vector3(world.X, world.Y, world.Z) / world.W;
        }
        return (_spawns[(int)number - 1].Point, hit);
    }

    private void Clear()
    {
        foreach (var slot in _slots.Values)
            slot.Model.Dispose();
        _slots.Clear();
        _spawns = [];
        _area = [];
        (_for, _areaFor) = (default, default);
        _areaCentre = new Vector3(float.NaN);
        _centre = new Vector3(float.NaN);
        (Shown, IsLoading) = (0, false);
    }

    public void Dispose() => Clear();
}
