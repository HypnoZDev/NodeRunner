using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// What the player asks for this frame: move (x right, y forward, from the camera), pace, jump, mouse look (pixels)
// and wheel (notches).
internal readonly record struct PlayInput(Vector2 Move, bool Sprint, bool Walk, bool Jump, Vector2 Look, float Wheel);

// Play mode: one ped walked on the map with the movement keys, the camera behind her, with the game's own clips for
// standing, walking, running, sprinting and jumping. No gameplay: only moving around, to see the map as a player.
internal sealed class PlayMode : IDisposable
{
    public const string PedName = "ig_agatha";
    private const string MoveDictionary = "move_f@generic";
    private const string JumpDictionary = "move_jump";

    private const float Acceleration = 9f; // Metres per second, each second.
    private const float Deceleration = 12f;
    private const float TurnRate = 10f; // How fast she turns to where she is asked to go (1 / seconds).
    private const float Gravity = 9.81f;
    private const float JumpSpeed = 4.6f; // Upwards, at the start of a jump: about 1 m high.
    private const float StepUp = 0.45f; // Highest step climbed without jumping.
    private const float StepDown = 0.6f; // Lowest step walked down without falling.
    private const float FallBack = 60f; // Metres fallen with no ground below: back to the start.
    private const float BlendIn = 10f; // Clip changes (jump, landing), 1 / seconds.
    // Seconds into jump_launch_l: feet leaving the ground; legs tucked in the air. With JumpSpeed, she lands about
    // when the clip stretches the legs down (1.2 s).
    private const float JumpClipStart = 0.25f;
    private const float FallClipStart = 0.6f;

    // Camera: around a point above the feet, at a distance the wheel changes.
    private const float PivotHeight = 1.45f;
    private const float MinDistance = 1.2f;
    private const float MaxDistance = 12f;
    private const float MouseSensitivity = 0.004f;
    private const float MinPitch = -1.25f;
    private const float MaxPitch = 0.7f;

    private sealed record Clips(AnimationClip Idle, AnimationClip Walk, AnimationClip Run, AnimationClip Sprint, AnimationClip? Jump,
        float WalkSpeed, float RunSpeed, float SprintSpeed);

    private readonly PreviewModel _model = new();
    private readonly PlayCollision _collision = new();
    private Task<Clips?>? _clips;
    private object? _clipsFor;
    private Matrix4x4[]? _pose;

    private Vector3 _start;
    private float _startHeading;
    private float _heading;
    private float _speed;
    private float _verticalSpeed;
    private bool _grounded;
    private bool _waiting; // Collision under her not read yet: she stays where she is.
    private float _fallen;
    private float _phase; // Where the walk / run / sprint cycle is (0 to 1).
    private float _idleTime;
    private float _jumpTime;
    private float _jumpWeight;
    private bool _jumped;

    private float _cameraYaw;
    private float _cameraPitch;
    private float _distance = 3.6f;
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

    // Starts at a point of the map (on the ground when known), facing the way the camera looks.
    public void Start(WorldLoader world, Camera camera, Vector3 at, float? ground)
    {
        if (world.Models is not { } models)
            return;
        _saved = (camera.Position, camera.Yaw, camera.Pitch, camera.NearPlane);
        Position = _start = at with { Z = ground ?? at.Z };
        _heading = _startHeading = camera.Yaw - MathF.PI / 2f; // The model faces +Y.
        (_speed, _verticalSpeed, _grounded, _waiting, _fallen) = (0f, 0f, true, true, 0f);
        (_phase, _idleTime, _jumpTime, _jumpWeight, _jumped) = (0f, 0f, 0f, 0f, false);
        _cameraYaw = camera.Yaw;
        _cameraPitch = -0.25f;
        _pivotZ = Position.Z + PivotHeight;
        _model.Open(ViewerCatalog.Ped(models, PedName, null), world);
        if (!ReferenceEquals(_clipsFor, world.Content) && world.Content is { } content)
        {
            _clipsFor = content;
            _clips = Task.Run(() => ReadClips(content));
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

    public void Update(WorldLoader world, CollisionEdits edits, ModelRenderer renderer, Camera camera, PlayInput input, bool invertY,
        float sensitivity, float step)
    {
        if (!IsActive)
            return;
        _model.Update(renderer);
        _collision.Update(world, edits, Position);
        _cameraYaw -= input.Look.X * MouseSensitivity * sensitivity;
        _cameraPitch = Math.Clamp(_cameraPitch - input.Look.Y * MouseSensitivity * sensitivity * (invertY ? -1f : 1f), MinPitch, MaxPitch);
        _distance = Math.Clamp(_distance * MathF.Pow(0.88f, input.Wheel), MinDistance, MaxDistance);

        var clips = _clips is { IsCompletedSuccessfully: true } read ? read.Result : null;
        _waiting = !_collision.IsReady(Position);
        if (clips is not null && _model.Model is not null && !_waiting)
            Move(clips, input, step);
        PlaceCamera(camera, step);
    }

    private void Move(Clips clips, PlayInput input, float step)
    {
        // Where she is asked to go, from the camera's point of view.
        var forward = new Vector2(MathF.Cos(_cameraYaw), MathF.Sin(_cameraYaw));
        var right = new Vector2(MathF.Sin(_cameraYaw), -MathF.Cos(_cameraYaw));
        var wanted = forward * input.Move.Y + right * input.Move.X;
        float target = 0f;
        if (wanted.LengthSquared() > 1e-4f && _grounded)
        {
            float goal = MathF.Atan2(-wanted.X, wanted.Y);
            _heading += Wrap(goal - _heading) * (1f - MathF.Exp(-TurnRate * step));
            target = input.Sprint ? clips.SprintSpeed : input.Walk ? clips.WalkSpeed : clips.RunSpeed;
        }
        if (_grounded)
            _speed += Math.Clamp(target - _speed, -Deceleration * step, Acceleration * step);

        // Ahead, the way she faces (she turns in an arc, as in the game).
        var facing = new Vector2(-MathF.Sin(_heading), MathF.Cos(_heading));
        var flat = new Vector2(Position.X, Position.Y) + facing * _speed * step;
        float z = Position.Z;

        if (_grounded && input.Jump)
        {
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
        if (_fallen > FallBack)
        {
            // Through the map, or off its edge: back to where play started.
            Position = _start;
            _heading = _startHeading;
            (_speed, _verticalSpeed, _grounded, _fallen) = (0f, 0f, true, 0f);
        }

        // Clips: the cycle goes on at the pace of the clips mixed now.
        var (idle, walk, run, sprint) = Weights(clips, _speed);
        float moving = walk + run + sprint;
        if (moving > 0f)
            _phase = (_phase + step * (walk / clips.Walk.Duration + run / clips.Run.Duration + sprint / clips.Sprint.Duration) / moving) % 1f;
        _idleTime += step;
        _jumpTime += step;
        bool inAir = !_grounded && (_jumped || _fallen > 0.4f);
        _jumpWeight += ((inAir ? 1f : 0f) - _jumpWeight) * (1f - MathF.Exp(-BlendIn * step));
    }

    // Shares of the standing, walking, running and sprinting clips at a speed.
    private static (float Idle, float Walk, float Run, float Sprint) Weights(Clips clips, float speed)
    {
        if (speed <= clips.WalkSpeed)
        {
            float t = speed / clips.WalkSpeed;
            return (1f - t, t, 0f, 0f);
        }
        if (speed <= clips.RunSpeed)
        {
            float t = (speed - clips.WalkSpeed) / (clips.RunSpeed - clips.WalkSpeed);
            return (0f, 1f - t, t, 0f);
        }
        float s = Math.Clamp((speed - clips.RunSpeed) / (clips.SprintSpeed - clips.RunSpeed), 0f, 1f);
        return (0f, 0f, 1f - s, s);
    }

    private void PlaceCamera(Camera camera, float step)
    {
        // The point followed rises smoothly up steps.
        _pivotZ += (Position.Z + PivotHeight - _pivotZ) * (1f - MathF.Exp(-15f * step));
        var pivot = Position with { Z = _pivotZ };
        var forward = new Vector3(MathF.Cos(_cameraPitch) * MathF.Cos(_cameraYaw), MathF.Cos(_cameraPitch) * MathF.Sin(_cameraYaw), MathF.Sin(_cameraPitch));
        camera.Position = pivot - forward * _distance;
        camera.Yaw = _cameraYaw;
        camera.Pitch = _cameraPitch;
    }

    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection)
    {
        if (!IsActive || _model.Model is not { } model)
            return;
        var clips = _clips is { IsCompletedSuccessfully: true } read ? read.Result : null;
        bool posed = false;
        if (clips is not null && model.IsSkinned && _model.Data?.Skeleton is { } skeleton)
        {
            if (_pose is null || _pose.Length != skeleton.Count)
                _pose = new Matrix4x4[skeleton.Count];
            var (idle, walk, run, sprint) = Weights(clips, _speed);
            float ground = 1f - (clips.Jump is null ? 0f : _jumpWeight);
            Span<ClipLayer> layers =
            [
                new(clips.Idle, _idleTime, idle * ground),
                new(clips.Walk, _phase * clips.Walk.Duration, walk * ground),
                new(clips.Run, _phase * clips.Run.Duration, run * ground),
                new(clips.Sprint, _phase * clips.Sprint.Duration, sprint * ground),
                // From the push off the ground (its crouch is skipped: she leaves at once), held on its last pose
                // (legs down for the landing); a fall without a jump starts with the legs tucked.
                new(clips.Jump ?? clips.Idle, Math.Min(_jumpTime + (_jumped ? JumpClipStart : FallClipStart), (clips.Jump?.Duration ?? 1f) - 0.02f),
                    1f - ground),
            ];
            SkeletonAnimator.Pose(skeleton, layers, _pose);
            posed = true;
        }
        // The model's feet (lowest point) on her position, facing her heading.
        var world = Matrix4x4.CreateTranslation(0f, 0f, -_model.BoundsMin.Z) * Matrix4x4.CreateRotationZ(_heading)
                    * Matrix4x4.CreateTranslation(Position);
        foreach (var pass in (ReadOnlySpan<ModelPass>)[ModelPass.Solid, ModelPass.Translucent])
        {
            renderer.Begin(pass);
            renderer.BonePose = posed ? _pose : null;
            renderer.Draw(model, world, viewProjection, Vector3.One);
        }
        renderer.BonePose = null;
    }

    private static Clips? ReadClips(GameContent content)
    {
        try
        {
            var moves = Read(content, MoveDictionary);
            var jumps = Read(content, JumpDictionary);
            if (Find(moves, "idle") is not { } idle || Find(moves, "walk") is not { } walk || Find(moves, "run") is not { } run
                || Find(moves, "sprint") is not { } sprint)
                return null;
            return new Clips(idle, walk, run, sprint, Find(jumps, "jump_launch_l"),
                SpeedOf(walk, 1.8f), SpeedOf(run, 3.7f), SpeedOf(sprint, 5.8f));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<uint, AnimationClip> Read(GameContent content, string dictionary)
    {
        // The last one listed: a DLC's version over the base game's.
        string? path = content.EnumerateFiles(".ycd")
            .LastOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(dictionary, StringComparison.OrdinalIgnoreCase));
        if (path is null || content.ReadFile(path) is not { } data || content.GetResourceHeader(path) is not { } header)
            return new Dictionary<uint, AnimationClip>();
        return ClipDictionaryReader.Read(data, header);
    }

    private static AnimationClip? Find(IReadOnlyDictionary<uint, AnimationClip> clips, string name) =>
        clips.GetValueOrDefault(Joaat.Hash(name))
        ?? clips.Values.FirstOrDefault(c => c.Name is { } full
            && Path.GetFileNameWithoutExtension(full.Replace('\\', '/').Split('/')[^1]).Equals(name, StringComparison.OrdinalIgnoreCase));

    // How fast a looping clip moves its ped forward, from its mover track (metres per second).
    private static float SpeedOf(AnimationClip clip, float fallback)
    {
        if (clip.Duration <= 0f)
            return fallback;
        var a = clip.Root(0f, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation).Position;
        var b = clip.Root(clip.Duration - 0.001f, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation).Position;
        float speed = a is { } from && b is { } to ? (to - from).Length() / clip.Duration : 0f;
        return speed is > 0.5f and < 12f ? speed : fallback;
    }

    private static float Wrap(float angle)
    {
        angle %= MathF.Tau;
        return angle > MathF.PI ? angle - MathF.Tau : angle < -MathF.PI ? angle + MathF.Tau : angle;
    }

    public void Dispose() => _model.Dispose();
}
