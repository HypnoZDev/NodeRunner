using System.Numerics;
using NodeRunner.Data.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// What the player asks for this frame: move (x right, y forward, from the camera), pace, jump, mouse look (pixels)
// and wheel (notches).
internal readonly record struct PlayInput(Vector2 Move, bool Sprint, bool Walk, bool Jump, Vector2 Look, float Wheel);

// Play mode: one ped walked on the map with the movement keys, the camera behind her, with the game's own clips for
// standing, walking, running, sprinting and jumping. No gameplay: only moving around, to see the map as a player. The
// same in every game: what is proper to one (the walker, her clips, the collision) comes from its IPlayGame.
internal sealed class PlayMode : IDisposable
{
    private const float Gravity = 9.81f;
    private const float JumpSpeed = 4.6f; // Upwards, at the start of a jump: about 1 m high.
    private const float StepUp = 0.45f; // Highest step climbed without jumping.
    private const float StepDown = 0.6f; // Lowest step walked down without falling.
    private const float StartRise = 2f; // Ground looked for this far above the point she starts at.
    private const float MaxSubstep = 0.12f; // Metres moved at once, less than the body's radius.
    private const float FallBack = 60f; // Metres fallen with no ground below: back to the start.
    private const float BlendIn = 10f; // Clip changes (jump, landing), 1 / seconds.
    // Swimming: her feet this far under the surface (her head just above it, as the swimming clips hold it). She
    // swims where the water is deeper, and stands up again where it is this shallow.
    private const float SwimDepth = 1.36f;
    private const float StandDepth = 1.2f;
    private const float Surfacing = 4f; // How fast she floats up to the surface (1 / seconds).
    private const float SwimBlend = 6f; // From walking into swimming clips, 1 / seconds.
    private const float WaterReach = 4f; // Water surfaces of the collision looked for up to this far above her.
    // Climbing: from this high, up to the highest clip and a little more; running clips above this speed.
    private const float LowestClimb = 0.6f;
    private const float ClimbMargin = 0.25f;
    private const float RunClimbSpeed = 2.5f;
    private const float ClimbIn = 20f; // Into and out of the climbing clips, 1 / seconds.
    private const float ClimbOut = 8f;
    // Camera: around a point above the feet, at a distance the wheel changes.
    private const float PivotHeight = 1.45f;
    private const float MinDistance = 1.2f;
    private const float MaxDistance = 12f;
    private const float MouseSensitivity = 0.004f;
    private const float CameraMargin = 0.25f; // Kept between the camera and a wall behind it.
    private const float NearestCamera = 0.35f;
    private const float AboveWater = 0.3f; // Lowest the camera goes over a water surface.
    private const float MinPitch = -1.25f;
    private const float MaxPitch = 0.7f;

    private readonly PreviewModel _model = new();
    private readonly PlayCollision _collision = new();
    private IPlayGame? _game;
    // How she moves on the ground and in the water, with her clips.
    private sealed record Gaits(PlayGait Ground, PlayGait? Swim, Climbs Climbs)
    {
        public IEnumerable<AnimationClip> FeetClips => Swim is null ? Ground.FeetClips : Ground.FeetClips.Concat(Swim.FeetClips);
    }

    // The paths of her climbing clips.
    private sealed record Climbs(MoverPath[] Stand, MoverPath[] Run, MoverPath[] Swim, MoverPath? Exit, MoverPath? RunExit)
    {
        public static Climbs Of(PlayClips clips)
        {
            static MoverPath[] Paths(IEnumerable<AnimationClip> list) => list.Select(c => new MoverPath(c)).ToArray();
            return new Climbs(Paths(clips.StandClimbs), Paths(clips.RunClimbs), Paths(clips.SwimClimbs),
                clips.ClimbExit is { } exit ? new MoverPath(exit) : null, clips.RunClimbExit is { } run ? new MoverPath(run) : null);
        }
    }

    private Task<Gaits?>? _clips;
    private PlayClimb? _climb;
    private float _climbWeight;
    private object? _clipsFor;
    private Task<FootPoses?>? _feet;
    private readonly List<ClipLayer> _layers = [];
    private bool _placed; // Put on the ground under where she started, once the collision there is read.
    private Matrix4x4[]? _pose;

    private Vector3 _start;
    private float _startHeading;
    private float _heading;
    private float _verticalSpeed;
    private bool _grounded;
    private bool _waiting; // Collision under her not read yet: she stays where she is.
    private float _fallen;
    private float _jumpTime;
    private float _jumpWeight;
    private bool _jumped;
    private bool _swimming;
    private float _swimWeight;

    private float _cameraYaw;
    private float _cameraPitch;
    private float _distance = 3.6f; // Asked with the wheel.
    private float _shownDistance = 3.6f; // Shorter while a wall is behind her.
    private float _pivotZ;
    private (Vector3 Position, float Yaw, float Pitch, float NearPlane) _saved;

    public bool IsActive { get; private set; }

    // Where her feet are.
    public Vector3 Position { get; private set; }

    // What still keeps her from being shown or moved, for the hints; null when ready.
    public string? Waiting =>
        _model.Error is { } error ? L($"Personnage introuvable : {error}")
        : _model.Model is null || _clips is not { IsCompleted: true } ? L("Chargement du personnage...")
        : !_clips.IsCompletedSuccessfully || _clips.Result is null ? L("Animations de déplacement introuvables.")
        : _waiting ? L("Lecture des collisions...")
        : null;

    // Starts at a point of the map (put on the ground below once its collision is read), facing the way the camera
    // looks.
    public void Start(IPlayGame game, Camera camera, Vector3 at)
    {
        if (game.Data is not { } data)
            return;
        _game = game;
        _saved = (camera.Position, camera.Yaw, camera.Pitch, camera.NearPlane);
        Position = _start = at;
        _heading = _startHeading = camera.Yaw - MathF.PI / 2f; // The model faces +Y.
        (_verticalSpeed, _grounded, _waiting, _fallen, _placed) = (0f, true, true, 0f, false);
        (_jumpTime, _jumpWeight, _jumped, _swimming, _swimWeight) = (0f, 0f, false, false, 0f);
        (_climb, _climbWeight) = (null, 0f);
        _cameraYaw = camera.Yaw;
        _cameraPitch = -0.25f;
        _pivotZ = Position.Z + PivotHeight;
        _shownDistance = _distance;
        game.OpenCharacter(_model);
        if (!ReferenceEquals(_clipsFor, data))
        {
            _clipsFor = data;
            _clips = Task.Run(() => game.ReadClips() is { } read
                ? new Gaits(new PlayGait(read), read.Swimming is { } swimming ? new PlayGait(swimming) : null, Climbs.Of(read))
                : null);
        }
        // The model is read again: its skeleton too.
        _feet = null;
        if (_clips is { IsCompletedSuccessfully: true, Result: { } gaits })
        {
            foreach (var gait in new[] { gaits.Ground, gaits.Swim }.OfType<PlayGait>())
            {
                gait.Reset();
                gait.UseFeet(null);
            }
        }
        _collision.Clear();
        IsActive = true;
        // Close enough for her to be seen whole.
        camera.NearPlane = 0.1f;
        PlaceCamera(camera, 1f);
    }

    // Back to the free camera, where it was before.
    public void Stop(Camera camera)
    {
        if (!IsActive)
            return;
        IsActive = false;
        (camera.Position, camera.Yaw, camera.Pitch, camera.NearPlane) = _saved;
        _model.Close();
        _pose = null;
    }

    public void Update(ModelRenderer renderer, Camera camera, PlayInput input, bool invertY, float sensitivity, float step)
    {
        if (!IsActive || _game is not { } game)
            return;
        _model.Update(renderer);
        _collision.Update(game, Position);
        _cameraYaw -= input.Look.X * MouseSensitivity * sensitivity;
        _cameraPitch = Math.Clamp(_cameraPitch - input.Look.Y * MouseSensitivity * sensitivity * (invertY ? -1f : 1f), MinPitch, MaxPitch);
        _distance = Math.Clamp(_distance * MathF.Pow(0.88f, input.Wheel), MinDistance, MaxDistance);

        var gaits = _clips is { IsCompletedSuccessfully: true } read ? read.Result : null;
        if (gaits is not null && _feet is null && _model.Data?.Skeleton is { } skeleton)
        {
            var clips = gaits.FeetClips.ToList();
            _feet = Task.Run(() => FootPoses.Build(skeleton, clips));
        }
        if (gaits is not null && _feet is { IsCompleted: true } feet)
        {
            var poses = feet.IsCompletedSuccessfully ? feet.Result : null;
            gaits.Ground.UseFeet(poses);
            gaits.Swim?.UseFeet(poses);
            _feet = Task.FromResult<FootPoses?>(null); // Given: not again.
        }
        _waiting = !_collision.IsReady(Position);
        if (!_waiting && !_placed)
        {
            // Started at the point looked at: on the surface just below it (a step or less above: the same).
            _placed = true;
            if (_collision.Ground(new Vector2(Position.X, Position.Y), Position.Z + StartRise) is { } ground)
                Position = _start = Position with { Z = ground };
        }
        if (gaits is not null && _model.Model is not null && !_waiting)
            Move(game, gaits, input, step);
        PlaceCamera(camera, step);
    }

    private void Move(IPlayGame game, Gaits gaits, PlayInput input, float step)
    {
        // Where she is asked to go, from the camera's point of view.
        var forward = new Vector2(MathF.Cos(_cameraYaw), MathF.Sin(_cameraYaw));
        var right = new Vector2(MathF.Sin(_cameraYaw), -MathF.Cos(_cameraYaw));
        var wanted = forward * input.Move.Y + right * input.Move.X;
        float? goal = wanted.LengthSquared() > 1e-4f ? MathF.Atan2(-wanted.X, wanted.Y) : null;
        var pace = input.Sprint ? PlayGait.Pace.Sprint : input.Walk ? PlayGait.Pace.Walk : PlayGait.Pace.Run;

        if (_climb is { IsOver: false } climb)
            Climb(gaits, climb, step);
        else if (_swimming && gaits.Swim is { } swim)
            Swim(game, gaits, swim, goal, pace, input.Jump, step);
        else
            Walk(game, gaits, goal, pace, input.Jump, step);

        _jumpTime += step;
        bool inAir = !_grounded && !_swimming && (_jumped || _fallen > 0.4f);
        _jumpWeight += ((inAir ? 1f : 0f) - _jumpWeight) * (1f - MathF.Exp(-BlendIn * step));
        _swimWeight += ((_swimming ? 1f : 0f) - _swimWeight) * (1f - MathF.Exp(-SwimBlend * step));
        bool climbing = _climb is { IsOver: false };
        _climbWeight += ((climbing ? 1f : 0f) - _climbWeight) * (1f - MathF.Exp(-(climbing ? ClimbIn : ClimbOut) * step));
        if (!climbing && _climbWeight < 0.01f)
            _climb = null;
    }

    // Space facing a ledge (walking, running or swimming): she climbs onto it if one of her clips climbs about that
    // high. False when there is none: she jumps.
    private bool TryClimb(Gaits gaits, float? goal, bool swimming)
    {
        var climbs = gaits.Climbs;
        float heading = goal ?? _heading;
        var ahead = MoverPath.Turn(Vector2.UnitY, heading);
        bool running = !swimming && gaits.Ground.Speed > RunClimbSpeed && climbs.Run.Length > 0;
        var paths = swimming ? climbs.Swim : running ? climbs.Run : climbs.Stand;
        if (paths.Length == 0)
            return false;
        float highest = paths.Max(p => p.TotalHeight) + ClimbMargin;
        // Out of the water: the quay's face is felt just under the surface.
        var ledge = swimming
            ? _collision.Ledge(Position, ahead, SwimDepth - 0.3f, SwimDepth - 0.3f, highest)
            : _collision.Ledge(Position, ahead, 0.5f, LowestClimb, highest);
        if (ledge is not var (top, distance) || PlayClimb.Closest(paths, top - Position.Z) is not { } up)
            return false;
        bool runOn = running && goal is not null && climbs.RunExit is not null;
        (swimming ? gaits.Swim : gaits.Ground)?.Release();
        _climb = new PlayClimb(up, runOn ? climbs.RunExit : climbs.Exit, heading, top - Position.Z, distance, runOn);
        _heading = heading;
        (_swimming, _grounded, _jumped, _verticalSpeed, _fallen) = (false, false, false, 0f, 0f);
        return true;
    }

    // Carried up by the climbing clip, then standing up on top (walls stop her again there).
    private void Climb(Gaits gaits, PlayClimb climb, float step)
    {
        var (move, walls) = climb.Step(step);
        if (walls)
        {
            var flat = Slide(new Vector2(move.X, move.Y));
            Position = new Vector3(flat.X, flat.Y, Position.Z + move.Z);
        }
        else
            Position += move;
        if (!climb.IsOver)
            return;
        if (_collision.Ground(new Vector2(Position.X, Position.Y), Position.Z + StepUp) is { } top)
            Position = Position with { Z = top };
        (_grounded, _verticalSpeed, _fallen) = (true, 0f, 0f);
        if (climb.Running)
            gaits.Ground.Continue(gaits.Ground.Clips.RunSpeed);
        else
            gaits.Ground.Reset();
    }

    // At the surface: her swimming gait moves her and she floats at the swimming height; she stands up where the
    // water gets shallow (or falls where the water files end).
    private void Swim(IPlayGame game, Gaits gaits, PlayGait swim, float? goal, PlayGait.Pace pace, bool climb, float step)
    {
        if (climb && TryClimb(gaits, goal, swimming: true))
            return;
        var ground = gaits.Ground;
        (var ahead, _heading) = swim.Step(goal, pace, _heading, step);
        var flat = Slide(ahead);
        if (step > 0f)
            swim.Blocked(Vector2.Distance(flat, new Vector2(Position.X, Position.Y)) / step);
        float z = Position.Z;
        float? surface = WaterAt(game, flat, Position.Z, inside: true);
        float? bottom = surface is { } level ? _collision.Ground(flat, level) : null;
        if (surface is not { } water || bottom is { } floor && floor >= water - StandDepth)
        {
            // Footing again (or no water there any more): walking out, at her swimming speed.
            swim.Release();
            ground.Continue(MathF.Min(swim.Speed, ground.Clips.RunSpeed));
            (_swimming, _grounded, _verticalSpeed, _jumped) = (false, bottom is not null, 0f, false);
            Position = new Vector3(flat.X, flat.Y, bottom ?? z);
            return;
        }
        z += (water - SwimDepth - z) * (1f - MathF.Exp(-Surfacing * step));
        if (bottom is { } seabed && z < seabed)
            z = seabed;
        Position = new Vector3(flat.X, flat.Y, z);
    }

    // The water surface at a point, for her (or the camera) at a height: the game's flat water (sea, lakes) or that of
    // the collision (canals, rivers, pools), with no floor between it and that height (a pool on a terrace overhead,
    // the sea over a tunnel: none) unless she is inside it already (a step of the bottom is no floor over her).
    private float? WaterAt(IPlayGame game, Vector2 point, float height, bool inside = false)
    {
        float? water = game.WaterAt(point);
        if (_collision.WaterAt(point, height + WaterReach) is { } surface && (water is null || surface > water))
            water = surface;
        if (!inside && water is { } level && level > height && _collision.Ground(point, level - 0.05f) is { } floor && floor > height + StepUp)
            return null;
        return water;
    }

    // Moved ahead in short steps so that no wall is crossed: the walls push her back, and she slides along them.
    private Vector2 Slide(Vector2 ahead)
    {
        var feet = Position;
        int steps = Math.Max(1, (int)MathF.Ceiling(ahead.Length() / MaxSubstep));
        for (int i = 0; i < steps; i++)
            feet = _collision.PushOut(feet + new Vector3(ahead / steps, 0f));
        return new Vector2(feet.X, feet.Y);
    }

    private void Walk(IPlayGame game, Gaits gaits, float? goal, PlayGait.Pace pace, bool jump, float step)
    {
        var gait = gaits.Ground;
        // On the ground, her gait moves her; in the air, she keeps her speed and heading.
        Vector2 ahead;
        if (_grounded)
            (ahead, _heading) = gait.Step(goal, pace, _heading, step);
        else
            ahead = MoverPath.Turn(Vector2.UnitY, _heading) * gait.Speed * step;

        float z = Position.Z;
        var flat = Slide(ahead);
        if (_grounded && step > 0f)
            gait.Blocked(Vector2.Distance(flat, new Vector2(Position.X, Position.Y)) / step);

        // Space at a ledge climbs onto it, elsewhere jumps.
        if (_grounded && jump && TryClimb(gaits, goal, swimming: false))
            return;
        if (_grounded && jump)
        {
            gait.Release();
            _grounded = false;
            _jumped = true;
            _verticalSpeed = JumpSpeed;
            _jumpTime = 0f;
        }

        if (_grounded)
        {
            if (_collision.Ground(flat, z + StepUp) is { } ground && ground >= z - StepDown)
                z = ground;
            else
            {
                // Off an edge: falling, without a jump.
                gait.Release();
                _grounded = false;
                _jumped = false;
                _verticalSpeed = 0f;
                _jumpTime = 0f;
            }
        }
        else
        {
            _verticalSpeed -= Gravity * step;
            float next = z + _verticalSpeed * step;
            if (_verticalSpeed <= 0f && _collision.Ground(flat, z + StepUp) is { } ground && next <= ground)
            {
                z = ground;
                _grounded = true;
                _verticalSpeed = 0f;
                _fallen = 0f;
            }
            else
            {
                _fallen += MathF.Max(z - next, 0f);
                z = next;
            }
        }
        Position = new Vector3(flat.X, flat.Y, z);

        // Deep water under her (walked in, or fallen in): she swims from there, floating up to the surface.
        if (gaits.Swim is { } swim && WaterAt(game, flat, z) is { } water && water - z > SwimDepth && (_grounded || _verticalSpeed <= 0f))
        {
            gait.Release();
            swim.Continue(MathF.Min(gait.Speed, swim.Clips.RunSpeed));
            (_swimming, _grounded, _jumped, _verticalSpeed, _fallen) = (true, false, false, 0f, 0f);
            return;
        }

        if (_fallen > FallBack)
        {
            // Through the map, or off its edge: back to where play started.
            Position = _start;
            _heading = _startHeading;
            (_verticalSpeed, _grounded, _fallen) = (0f, true, 0f);
            gait.Reset();
        }
    }

    private void PlaceCamera(Camera camera, float step)
    {
        // The point followed rises smoothly up steps.
        _pivotZ += (Position.Z + PivotHeight - _pivotZ) * (1f - MathF.Exp(-15f * step));
        var pivot = Position with { Z = _pivotZ };
        var forward = new Vector3(MathF.Cos(_cameraPitch) * MathF.Cos(_cameraYaw), MathF.Cos(_cameraPitch) * MathF.Sin(_cameraYaw), MathF.Sin(_cameraPitch));
        // A wall or the ground between her and the camera: the camera comes in front of it at once, and goes back
        // out smoothly.
        float room = _distance;
        if (_collision.Hit(pivot, pivot - forward * (_distance + CameraMargin)) is { } share)
            room = Math.Max(share * (_distance + CameraMargin) - CameraMargin, NearestCamera);
        _shownDistance = room < _shownDistance ? room : _shownDistance + (room - _shownDistance) * (1f - MathF.Exp(-4f * step));
        camera.Position = pivot - forward * _shownDistance;
        camera.Yaw = _cameraYaw;
        camera.Pitch = _cameraPitch;
        // Never under the water (nothing shows what is under it from below): above the surface, still looking at her.
        if (_game is { } game && WaterAt(game, new Vector2(camera.Position.X, camera.Position.Y), camera.Position.Z) is { } water && camera.Position.Z < water + AboveWater)
        {
            camera.Position = camera.Position with { Z = water + AboveWater };
            camera.LookAt(pivot);
        }
    }

    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection)
    {
        if (!IsActive || _model.Model is not { } model)
            return;
        var gaits = _clips is { IsCompletedSuccessfully: true } read ? read.Result : null;
        bool posed = false;
        if (gaits is not null && model.IsSkinned && _model.Data?.Skeleton is { } skeleton)
        {
            if (_pose is null || _pose.Length != skeleton.Count)
                _pose = new Matrix4x4[skeleton.Count];
            var clips = gaits.Ground.Clips;
            float climb = _climb is null ? 0f : _climbWeight;
            float swim = (1f - climb) * (gaits.Swim is null ? 0f : _swimWeight);
            float air = (1f - climb - swim) * (clips.Jump is null ? 0f : _jumpWeight);
            _layers.Clear();
            _climb?.Layers(_layers, climb);
            gaits.Ground.Layers(_layers, 1f - climb - swim - air);
            gaits.Swim?.Layers(_layers, swim);
            // From where the feet leave the ground, held on its last pose (legs down for the landing).
            _layers.Add(new ClipLayer(clips.Jump ?? clips.Idle,
                Math.Min(_jumpTime + (_jumped ? clips.JumpStart : clips.FallStart), (clips.Jump?.Duration ?? 1f) - 0.02f), air));
            SkeletonAnimator.Pose(skeleton, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_layers), _pose);
            gaits.Ground.Observe(_pose);
            gaits.Swim?.Observe(_pose);
            posed = true;
        }
        var world = Placement();
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            renderer.BonePose = posed ? _pose : null;
            renderer.Draw(model, world, viewProjection, Vector3.One);
        }
        renderer.BonePose = null;
    }

    // Her shadow in one of the sun's images, in the pose of the last image drawn.
    public void DrawShadow(ModelRenderer renderer, Matrix4x4 lightViewProjection)
    {
        if (!IsActive || _model.Model is not { } model)
            return;
        renderer.BonePose = _pose;
        renderer.DrawShadow(model, Placement(), lightViewProjection);
        renderer.BonePose = null;
    }

    // The model's feet (lowest point) on her position, facing her heading.
    private Matrix4x4 Placement() =>
        Matrix4x4.CreateTranslation(0f, 0f, -_model.BoundsMin.Z) * Matrix4x4.CreateRotationZ(_heading) * Matrix4x4.CreateTranslation(Position);

    public void Dispose() => _model.Dispose();
}
