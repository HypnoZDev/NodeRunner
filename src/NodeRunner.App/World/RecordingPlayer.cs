using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Recordings;
using NodeRunner.Data.Recordings;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Vehicle recordings (.yvr) played together on the map: each chosen recording drives a car along its path, drawn as a
// line, all on the same clock. The files are read in the background.
internal sealed class RecordingPlayer : IDisposable
{
    // The file does not say which vehicle drives a recording.
    public const string DefaultVehicle = "tailgater";

    public sealed record Entry(string Name, string Path, string Group, int Number);

    private static readonly Vector4[] Colors =
    [
        new(0.30f, 0.55f, 1f, 1f), new(1f, 0.55f, 0.15f, 1f), new(0.35f, 0.85f, 0.35f, 1f), new(0.95f, 0.3f, 0.45f, 1f),
        new(0.75f, 0.45f, 1f, 1f), new(0.2f, 0.85f, 0.85f, 1f), new(1f, 0.85f, 0.2f, 1f), new(0.95f, 0.95f, 0.95f, 1f),
    ];

    public sealed class Track(Entry entry, Vector4 color)
    {
        public readonly Entry Entry = entry;
        public readonly Vector4 Color = color;
        // Shared by the tracks driving the same vehicle (null: unknown name, drawn as a box).
        public PreviewModel? Model;
        public Task<VehicleRecording>? Loading;
        public VehicleRecording? Recording;
        public string? Error;
        public string Vehicle = DefaultVehicle;
        public ColorMesh? Trail;
        public Vector3[]? Paint;
        public VehicleRecordingPose Pose;
        // Before its start or after its end, the car waits at its first / last point.
        public Matrix4x4 World;
        // The vehicle name the model was asked for.
        public string? Opened;
    }

    private GameContent? _content;
    private Task<List<Entry>>? _list;
    private readonly List<Track> _tracks = [];
    private List<string>? _reopen;
    private int _colorIndex;
    private readonly Dictionary<string, PreviewModel> _models = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Track> Tracks => _tracks;

    // The time shown (seconds from the start), set by the timeline.
    public float Time { get; set; }

    public float Duration => _tracks.Count == 0 ? 0f : _tracks.Max(t => t.Recording?.Duration ?? 0f);

    // Set when the first recording was just read, for the camera to go to it once.
    public bool JustLoaded { get; set; }

    // The game's recordings (null while they are listed).
    public List<Entry>? Entries(WorldLoader world)
    {
        if (world.Content is not { } content)
            return null;
        if (!ReferenceEquals(content, _content) || _list is null)
        {
            if (_content is not null && _tracks.Count > 0)
            {
                // The map was loaded again: the same recordings are read again from its files.
                _reopen = _tracks.Select(t => t.Entry.Name).ToList();
                Clear();
            }
            _content = content;
            _list = Task.Run(() => content.EnumerateFiles(".yvr")
                .Select(p =>
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(p);
                    var (group, number) = VehicleRecording.SplitName(name);
                    return new Entry(name, p, group, number);
                })
                .OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Number).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        return _list.IsCompletedSuccessfully ? _list.Result : null;
    }

    public Track? Find(Entry entry) => _tracks.Find(t => t.Entry.Path == entry.Path);

    public void Show(Entry entry, bool shown)
    {
        var track = Find(entry);
        if (shown == track is not null || _content is not { } content)
            return;
        if (!shown)
        {
            track!.Trail?.Dispose();
            _tracks.Remove(track);
            ReleaseUnusedModels();
            return;
        }
        var added = new Track(entry, Colors[_colorIndex++ % Colors.Length]);
        added.Loading = Task.Run(() => VehicleRecordingReader.Read(content.ReadFile(entry.Path) ?? throw new InvalidDataException(L("Fichier illisible.")), entry.Name));
        _tracks.Add(added);
    }

    public void Close()
    {
        Clear();
        _reopen = null;
        Time = 0f;
    }

    private void Clear()
    {
        foreach (var track in _tracks)
            track.Trail?.Dispose();
        _tracks.Clear();
        ReleaseUnusedModels();
        _colorIndex = 0;
    }

    private void ReleaseUnusedModels()
    {
        foreach (string name in _models.Keys.Where(n => !_tracks.Any(t => string.Equals(t.Opened, n, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _models[name].Dispose();
            _models.Remove(name);
        }
    }

    // Once a frame: takes what was read, puts the models and paths on the graphics card, places the cars at Time.
    public void Update(WorldLoader world, ModelRenderer renderer, ColorRenderer lines)
    {
        if (_reopen is { } names && Entries(world) is { } entries)
        {
            _reopen = null;
            foreach (var entry in entries.Where(e => names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
                Show(entry, true);
        }
        foreach (var track in _tracks)
        {
            if (track.Loading is { IsCompleted: true } loading)
            {
                track.Loading = null;
                if (loading.IsCompletedSuccessfully)
                {
                    track.Recording = loading.Result;
                    if (_tracks.Count(t => t.Recording is not null) == 1)
                        JustLoaded = true;
                }
                else
                    track.Error = loading.Exception?.InnerException?.Message ?? L("Lecture impossible.");
            }
            if (track.Recording is not { } recording)
                continue;
            if (track.Trail is null && recording.Frames.Count > 1)
                track.Trail = TrailOf(lines, recording, track.Color);
            OpenVehicle(world, track);
            track.Pose = recording.Sample(Time);
            track.World = Matrix4x4.CreateFromQuaternion(track.Pose.Rotation) * Matrix4x4.CreateTranslation(track.Pose.Position);
        }
        foreach (var model in _models.Values)
            model.Update(renderer);
    }

    private static ColorMesh? TrailOf(ColorRenderer lines, VehicleRecording recording, Vector4 color)
    {
        var batch = new LineBatch();
        var lift = new Vector3(0f, 0f, 0.3f);
        for (int i = 1; i < recording.Frames.Count; i++)
            batch.AddLine(recording.Frames[i - 1].Position + lift, recording.Frames[i].Position + lift, color);
        return lines.CreateMesh(batch);
    }

    // Reads the track's vehicle when it changed; an unknown name leaves a box in its colour.
    private void OpenVehicle(WorldLoader world, Track track)
    {
        if (track.Opened == track.Vehicle || world.Models is not { } models)
            return;
        track.Opened = track.Vehicle;
        ReleaseUnusedModels();
        track.Model = null;
        track.Paint = null;
        uint hash = Joaat.Hash(track.Vehicle);
        if (models.FragmentPath(hash) is not { } path)
            return;
        if (!_models.TryGetValue(track.Vehicle, out var model))
        {
            _models[track.Vehicle] = model = new PreviewModel();
            model.Open(new ViewerItem(track.Vehicle, ViewerKind.Vehicle, new ModelLocation(path, hash, hash)), world);
        }
        track.Model = model;
        if (world.Vehicles is { } vehicles)
        {
            var sets = vehicles.Paints.VariationOf(hash)?.ColorSets;
            int index = _tracks.IndexOf(track);
            track.Paint = VehiclePaintColors.From(vehicles.Paints, sets is { Count: > 0 } ? sets[index % sets.Count].Indices : VehiclePaintColors.Default);
        }
    }

    public void Draw(ModelRenderer renderer, ColorRenderer lines, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (_tracks.Count == 0)
            return;
        foreach (var track in _tracks)
        {
            if (track.Trail is { } trail)
                lines.DrawXRay(trail, viewProjection, cameraPosition, 0f, 0.3f);
        }
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            foreach (var track in _tracks)
            {
                if (track.Recording is null || track.Model?.Model is not { } model)
                    continue;
                renderer.VehiclePaint = track.Paint;
                renderer.Draw(model, track.World, viewProjection, Vector3.One);
            }
        }
        renderer.VehiclePaint = null;
        // A car without a model: a box of a car's size.
        foreach (var track in _tracks)
        {
            if (track.Recording is not null && track.Model?.Model is null)
                lines.AddBox(new Vector3(-0.9f, -2.3f, -0.7f), new Vector3(0.9f, 2.3f, 0.7f), track.World, track.Color);
        }
        if (lines.PendingLineCount > 0)
            lines.Flush(viewProjection, cameraPosition);
    }

    public void Dispose()
    {
        Close();
        foreach (var model in _models.Values)
            model.Dispose();
        _models.Clear();
    }
}
