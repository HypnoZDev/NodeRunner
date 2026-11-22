using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// How the walker sets off, moves, turns and stops on the ground, and the clips that show it. Moving on, the looped
// standing / walking / running / sprinting clips are mixed by her speed. Setting off from standing, stopping and
// turning round are played with the game's own clips, which carry her themselves (their mover track: no sliding
// feet), each entered on the same step as the pose she is in, and the looped clips taken back the same way.
internal sealed class PlayGait
{
    public enum Pace { Walk, Run, Sprint }

    private const float Acceleration = 9f; // Metres per second, each second.
    private const float Deceleration = 12f;
    private const float SteerRate = 9f; // How fast she turns to where she is asked to go (1 / seconds)...
    private const float SprintSteerRate = 4f; // ... less when sprinting: wider turns.
    private const float CrossFade = 0.25f; // Seconds from one clip to the next.
    private const float Standing = 0.4f; // Below this speed she sets off with a clip.
    private const float StopFrom = 0.8f; // Above this speed she stops with a clip.
    private const float TurnRound = 2.36f; // Asked to go more than this far round (135°): she turns round with a clip.
    private const float MaxCorrection = 1.05f; // How far a clip's turn is bent towards where she is asked to go (60°).
    private const float CorrectionRate = 8f;
    private const float StraightTurnTime = 0.4f; // A clip going straight on turns to where she is asked within this.
    private const float StopEntries = 0.5f; // Seconds at the start of a stopping clip where it may be entered.
    private const float TurnLead = 0.6f; // Seconds before its turn where a turning clip may be entered.
    private const float LeaveBeforeEnd = 0.35f; // A clip is left this long before its end, during the cross fade.

    private enum Kind { Start, Stop, Turn }

    private sealed class Action(MoverPath path, Kind kind, float entry, float heading, bool walking)
    {
        public readonly MoverPath Path = path;
        public readonly Kind Kind = kind;
        public readonly float Entry = entry;
        public readonly float Heading = heading; // Hers when it was entered.
        public readonly bool Walking = walking; // Goes on into walking (else running).
        public float Time = entry;
        public float Correction; // Added to its turn, to go where she is asked.
    }

    private readonly MoverPath[] _walkStarts, _runStarts, _walkStops, _runStops, _walkTurns, _runTurns;
    private FootPoses? _feet;
    private Feet? _feetNow;
    private (Vector3 Left, Vector3 Right)? _feetBefore;
    private float _lastStep;

    private Action? _action; // Null: the looped clips.
    private Action? _previous; // Faded out; null with _previousLooped for the looped clips.
    private bool _previousLooped;
    private float _fade = 1f; // 0 to 1: share of what plays now over what played before.
    private int _setOffs; // Alternates the feet of the clips setting off straight on.

    public PlayClips Clips { get; }

    // Horizontal speed (metres per second): hers on the looped clips, the clip's while one carries her.
    public float Speed { get; private set; }

    // Where the walk / run / sprint cycle is (0 to 1), and the time in the standing clip.
    public float Phase { get; private set; }
    public float IdleTime { get; private set; }

    // A clip carries her now.
    public bool IsCarried => _action is not null;

    public PlayGait(PlayClips clips)
    {
        Clips = clips;
        static MoverPath[] Paths(IEnumerable<AnimationClip> list) => list.Select(c => new MoverPath(c)).ToArray();
        (_walkStarts, _runStarts) = (Paths(clips.WalkStarts), Paths(clips.RunStarts));
        (_walkStops, _runStops) = (Paths(clips.WalkStops), Paths(clips.RunStops));
        (_walkTurns, _runTurns) = (Paths(clips.WalkTurns), Paths(clips.RunTurns));
    }

    // Every clip whose feet are compared.
    public IEnumerable<AnimationClip> FeetClips =>
        new[] { Clips.Walk, Clips.Run, Clips.Sprint }
            .Concat(new[] { _walkStarts, _runStarts, _walkStops, _runStops, _walkTurns, _runTurns }.SelectMany(p => p).Select(p => p.Clip));

    // Feet of the walker's skeleton along the clips (null: clips entered at their start, the cycle where it was).
    public void UseFeet(FootPoses? feet)
    {
        _feet = feet;
        (_feetNow, _feetBefore) = (null, null);
    }

    // Going on at a speed after another gait (from walking into swimming...), on the looped clips.
    public void Continue(float speed)
    {
        Reset();
        Speed = speed;
    }

    // Standing still, on the looped clips.
    public void Reset()
    {
        (_action, _previous, _previousLooped, _fade) = (null, null, false, 1f);
        (Speed, Phase, IdleTime) = (0f, 0f, 0f);
        (_feetNow, _feetBefore) = (null, null);
    }

    // One image on the ground: goal is the heading she is asked to take (null: none), heading hers now. Returns how
    // far she goes (world, horizontal) and her new heading.
    public (Vector2 Move, float Heading) Step(float? goal, Pace pace, float heading, float step)
    {
        _lastStep = step;
        Fade(step);
        IdleTime += step;
        if (_action is { } carried)
            return Carry(carried, goal, pace, step);

        float target = 0f;
        if (goal is { } wanted)
        {
            float angle = MoverPath.Wrap(wanted - heading);
            if (Speed < Standing && SetOff(angle, pace, heading)
                || Speed >= Standing && MathF.Abs(angle) > TurnRound && TurnRoundNow(angle, heading))
                return Carry(_action!, goal, pace, step);
            float sprinting = Math.Clamp((Speed - Clips.RunSpeed) / (Clips.SprintSpeed - Clips.RunSpeed), 0f, 1f);
            float rate = SteerRate + (SprintSteerRate - SteerRate) * sprinting;
            heading += angle * (1f - MathF.Exp(-rate * step));
            target = SpeedOf(pace);
        }
        else if (Speed > StopFrom && StopNow(heading))
            return Carry(_action!, goal, pace, step);

        Speed += Math.Clamp(target - Speed, -Deceleration * step, Acceleration * step);
        var (_, walk, run, sprint) = Weights(Speed);
        float moving = walk + run + sprint;
        if (moving > 0f)
            Phase = (Phase + step * (walk / Clips.Walk.Duration + run / Clips.Run.Duration + sprint / Clips.Sprint.Duration) / moving) % 1f;
        return (MoverPath.Turn(Vector2.UnitY, heading) * Speed * step, heading);
    }

    // Against a wall: the looped clips slow down with her (actual: the speed she really had).
    public void Blocked(float actual)
    {
        if (_action is null)
            Speed = MathF.Min(Speed, actual + 0.05f);
    }

    // Leaving the ground (jump, fall): back to the looped clips, at the speed she had.
    public void Release()
    {
        if (_action is not null)
            Resume(Speed);
    }

    // Adds the clips of the pose now, counting for weight in all.
    public void Layers(List<ClipLayer> layers, float weight)
    {
        if (_action is { } action)
            Add(layers, action, _fade * weight);
        else
            AddLooped(layers, _fade * weight);
        if (_fade < 1f)
        {
            if (_previous is { } previous)
                Add(layers, previous, (1f - _fade) * weight);
            else if (_previousLooped)
                AddLooped(layers, (1f - _fade) * weight);
        }
    }

    // The pose just drawn: where her feet are, for the next clip to start on the same step.
    public void Observe(Matrix4x4[]? pose)
    {
        if (_feet?.Of(pose) is not var (left, right))
            return;
        if (_feetBefore is var (lastLeft, lastRight) && _lastStep > 0f)
            _feetNow = new Feet(left, right, (left - lastLeft) / _lastStep, (right - lastRight) / _lastStep);
        _feetBefore = (left, right);
    }

    private (Vector2, float) Carry(Action action, float? goal, Pace pace, float step)
    {
        var path = action.Path;
        float before = action.Time;
        action.Time = Math.Min(before + step, path.Duration);
        float entryYaw = path.YawAt(action.Entry);
        if (action.Kind != Kind.Stop && goal is { } wanted)
        {
            // The clip's own turn, bent towards where she is asked to go.
            float asked = MoverPath.Wrap(wanted - action.Heading);
            float correction = Math.Clamp(MoverPath.Wrap(asked - (path.TotalYaw - entryYaw)), -MaxCorrection, MaxCorrection);
            action.Correction += (correction - action.Correction) * (1f - MathF.Exp(-CorrectionRate * step));
        }
        float turned = path.YawAt(action.Time) - entryYaw;
        float whole = path.TotalYaw - entryYaw;
        float done = path.Turns && MathF.Abs(whole) > 1e-3f
            ? Math.Clamp(turned / whole, 0f, 1f)
            : Math.Clamp((action.Time - action.Entry) / StraightTurnTime, 0f, 1f);
        float heading = action.Heading + turned + action.Correction * done;
        // The clip's path, in its first image's frame, turned to the world.
        var move = MoverPath.Turn(path.PositionAt(action.Time) - path.PositionAt(before), heading - path.YawAt(action.Time));
        Speed = path.SpeedAt(action.Time);

        bool ending = action.Time >= path.Duration - LeaveBeforeEnd;
        switch (action.Kind)
        {
            case Kind.Start:
            case Kind.Turn:
                if (goal is null)
                {
                    // Let go at once: still nearly standing, she stands again; else she stops on her steps.
                    if (Speed < StopFrom || !StopNow(heading))
                        Resume(Speed < StopFrom ? 0f : Speed);
                }
                else if (ending || done >= 0.98f && action.Time >= (action.Kind == Kind.Turn ? path.TurnEnd + 0.2f : action.Entry + 0.5f)
                         && Speed >= 0.85f * (action.Walking ? Clips.WalkSpeed : Clips.RunSpeed))
                    Resume(Speed);
                break;
            case Kind.Stop:
                if (goal is { } again)
                {
                    // Asked to go again: from nearly standing, setting off; else on, the looped clips turning her.
                    if (Speed >= Standing + 0.2f || !SetOff(MoverPath.Wrap(again - heading), pace, heading))
                        Resume(Speed);
                }
                else if (ending)
                    Resume(0f);
                break;
        }
        return (move, heading);
    }

    // Setting off from standing: the clip turning her closest to where she is asked to go.
    private bool SetOff(float angle, Pace pace, float heading)
    {
        bool walking = pace == Pace.Walk;
        var paths = walking ? _walkStarts : _runStarts;
        if (paths.Length == 0)
            return false;
        float best = paths.Min(p => MathF.Abs(angle - p.TotalYaw));
        // Those as good (left or right foot first, straight on): one after the other.
        var equal = paths.Where(p => MathF.Abs(angle - p.TotalYaw) <= best + 0.1f).ToList();
        Begin(equal[_setOffs++ % equal.Count], Kind.Start, 0f, heading, walking);
        return true;
    }

    private bool StopNow(float heading)
    {
        bool walking = Speed < (Clips.WalkSpeed + Clips.RunSpeed) * 0.5f;
        var paths = walking ? _walkStops : _runStops;
        if (paths.Length == 0)
            return false;
        var (path, entry) = OnTheSameStep(paths, p => (0f, Math.Min(StopEntries, p.Duration * 0.3f)));
        Begin(path, Kind.Stop, entry, heading, walking);
        return true;
    }

    private bool TurnRoundNow(float angle, float heading)
    {
        bool walking = Speed < (Clips.WalkSpeed + Clips.RunSpeed) * 0.5f;
        var paths = (walking ? _walkTurns : _runTurns).Where(p => p.Turns).ToArray();
        if (paths.Length == 0)
            return false;
        float best = paths.Min(p => MathF.Abs(angle - p.TotalYaw));
        paths = paths.Where(p => MathF.Abs(angle - p.TotalYaw) <= best + 0.1f).ToArray();
        var (path, entry) = OnTheSameStep(paths, p => (Math.Max(0f, p.TurnStart - TurnLead), p.TurnStart));
        Begin(path, Kind.Turn, entry, heading, walking);
        return true;
    }

    // The clip, and its time within its window, whose feet are closest to hers now.
    private (MoverPath Path, float Entry) OnTheSameStep(MoverPath[] paths, Func<MoverPath, (float From, float To)> window)
    {
        if (_feet is not { } feet || _feetNow is not { } now)
            return (paths[0], window(paths[0]).From);
        var best = (Path: paths[0], Entry: window(paths[0]).From, Distance: float.MaxValue);
        foreach (var path in paths)
        {
            var (from, to) = window(path);
            float entry = feet.Closest(path.Clip, now, from, to, out float distance);
            if (distance < best.Distance)
                best = (path, entry, distance);
        }
        return (best.Path, best.Entry);
    }

    private void Begin(MoverPath path, Kind kind, float entry, float heading, bool walking)
    {
        (_previous, _previousLooped) = (_action, _action is null);
        _action = new Action(path, kind, entry, heading, walking);
        _fade = 0f;
    }

    // Back to the looped clips at a speed, the cycle on the step she is on.
    private void Resume(float speed)
    {
        (_previous, _previousLooped) = (_action, false);
        _action = null;
        _fade = 0f;
        Speed = speed;
        var cycle = speed <= (Clips.WalkSpeed + Clips.RunSpeed) * 0.5f ? Clips.Walk : speed <= (Clips.RunSpeed + Clips.SprintSpeed) * 0.5f ? Clips.Run : Clips.Sprint;
        if (speed > 0.1f && _feet is { } feet && _feetNow is { } now && cycle.Duration > 0f)
            Phase = feet.Closest(cycle, now, 0f, cycle.Duration, out _) / cycle.Duration;
        if (speed <= 0.1f)
            IdleTime = 0f;
    }

    private void Fade(float step)
    {
        _fade = Math.Min(1f, _fade + step / CrossFade);
        if (_previous is { } previous)
            previous.Time = Math.Min(previous.Time + step, previous.Path.Duration);
        if (_fade >= 1f)
            (_previous, _previousLooped) = (null, false);
    }

    private float SpeedOf(Pace pace) => pace switch
    {
        Pace.Walk => Clips.WalkSpeed,
        Pace.Sprint => Clips.SprintSpeed,
        _ => Clips.RunSpeed,
    };

    private static void Add(List<ClipLayer> layers, Action action, float weight) =>
        layers.Add(new ClipLayer(action.Path.Clip, Math.Min(action.Time, action.Path.Duration - 0.01f), weight));

    private void AddLooped(List<ClipLayer> layers, float weight)
    {
        var (idle, walk, run, sprint) = Weights(Speed);
        layers.Add(new ClipLayer(Clips.Idle, IdleTime, idle * weight));
        layers.Add(new ClipLayer(Clips.Walk, Phase * Clips.Walk.Duration, walk * weight));
        layers.Add(new ClipLayer(Clips.Run, Phase * Clips.Run.Duration, run * weight));
        layers.Add(new ClipLayer(Clips.Sprint, Phase * Clips.Sprint.Duration, sprint * weight));
    }

    // Shares of the standing, walking, running and sprinting clips at a speed.
    private (float Idle, float Walk, float Run, float Sprint) Weights(float speed)
    {
        if (speed <= Clips.WalkSpeed)
        {
            float t = speed / Clips.WalkSpeed;
            return (1f - t, t, 0f, 0f);
        }
        if (speed <= Clips.RunSpeed)
        {
            float t = (speed - Clips.WalkSpeed) / (Clips.RunSpeed - Clips.WalkSpeed);
            return (0f, 1f - t, t, 0f);
        }
        float s = Math.Clamp((speed - Clips.RunSpeed) / (Clips.SprintSpeed - Clips.RunSpeed), 0f, 1f);
        return (0f, 0f, 1f - s, s);
    }
}
