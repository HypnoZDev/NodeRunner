using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// A climb onto a ledge: the clip whose mover climbs closest to the ledge's height, stretched to that height and to the
// wall's distance, then the clip standing her up on top (or running on).
internal sealed class PlayClimb
{
    private const float Beyond = 0.3f; // Where on the top she ends, past the wall's face.
    private const float CrossFade = 0.15f;
    private const float LeaveBeforeEnd = 0.2f;

    private readonly MoverPath _up;
    private readonly MoverPath? _exit;
    private readonly float _heading;
    private readonly float _rise, _reach; // Stretches of the climbing clip's path.
    private float _time, _exitTime;
    private bool _exiting;

    // Leaves her running (else standing).
    public bool Running { get; }

    public bool IsOver { get; private set; }

    public PlayClimb(MoverPath up, MoverPath? exit, float heading, float height, float distance, bool running)
    {
        (_up, _exit, _heading, Running) = (up, exit, heading, running);
        _rise = up.TotalHeight > 0.1f ? height / up.TotalHeight : 1f;
        // Each clip starts its own distance from the wall.
        _reach = up.TotalReach > 0.05f ? Math.Clamp((distance + Beyond) / up.TotalReach, 0.6f, 2.5f) : 1f;
    }

    // The clip climbing closest to a height (null when none climbs).
    public static MoverPath? Closest(IEnumerable<MoverPath> paths, float height) =>
        paths.Where(p => p.TotalHeight > 0.1f).MinBy(p => MathF.Abs(p.TotalHeight - height));

    // One image: how far she goes, and whether walls stop her (not while going over the edge).
    public (Vector3 Move, bool Walls) Step(float step)
    {
        if (!_exiting)
        {
            float before = _time;
            _time = Math.Min(_time + step, _up.Duration);
            var ahead = MoverPath.Turn((_up.PositionAt(_time) - _up.PositionAt(before)) * _reach, _heading);
            var move = new Vector3(ahead, (_up.HeightAt(_time) - _up.HeightAt(before)) * _rise);
            if (_time >= _up.Duration)
            {
                _exiting = true;
                IsOver = _exit is null;
            }
            return (move, false);
        }
        if (_exit is not { } exit)
            return (Vector3.Zero, true);
        float last = _exitTime;
        _exitTime = Math.Min(_exitTime + step, exit.Duration);
        if (_exitTime >= exit.Duration - LeaveBeforeEnd)
            IsOver = true;
        return (new Vector3(MoverPath.Turn(exit.PositionAt(_exitTime) - exit.PositionAt(last), _heading), 0f), true);
    }

    public void Layers(List<ClipLayer> layers, float weight)
    {
        float end = _up.Duration - 0.01f;
        if (!_exiting || _exit is not { } exit)
        {
            layers.Add(new ClipLayer(_up.Clip, Math.Min(_time, end), weight));
            return;
        }
        float share = Math.Clamp(_exitTime / CrossFade, 0f, 1f);
        layers.Add(new ClipLayer(_up.Clip, end, weight * (1f - share)));
        layers.Add(new ClipLayer(exit.Clip, Math.Min(_exitTime, exit.Duration - 0.01f), weight * share));
    }
}
