using System.Numerics;
using NodeRunner.Data.Models;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// The walker's clips: standing, walking, running and sprinting (looped, played at a common phase) with the speeds they
// move at, and jumping (null: she keeps her running pose in the air). JumpStart: seconds into the jump clip where the
// feet leave the ground; FallStart: where a fall without a jump starts it.
internal sealed record PlayClips(AnimationClip Idle, AnimationClip Walk, AnimationClip Run, AnimationClip Sprint, AnimationClip? Jump,
    float WalkSpeed, float RunSpeed, float SprintSpeed, float JumpStart = 0f, float FallStart = 0f)
{
    // Clips that carry the walker themselves (their mover track), any number of each, chosen by the angle they turn her
    // and by her feet: setting off from standing (straight on, to a side, turning round), stopping, and turning round
    // while walking or running. None: she speeds up, slows down and turns without them.
    public IReadOnlyList<AnimationClip> WalkStarts { get; init; } = [];
    public IReadOnlyList<AnimationClip> RunStarts { get; init; } = [];
    public IReadOnlyList<AnimationClip> WalkStops { get; init; } = [];
    public IReadOnlyList<AnimationClip> RunStops { get; init; } = [];
    public IReadOnlyList<AnimationClip> WalkTurns { get; init; } = [];
    public IReadOnlyList<AnimationClip> RunTurns { get; init; } = [];
}

// What play mode takes from a game: who walks, how she moves and what she bumps into. Moving, the camera, the walls
// and the drawing are the same in every game: adding a game to play mode is writing one of these.
internal interface IPlayGame
{
    // Changes when the game's data is loaded again (the walker, her clips and the collision are then read again);
    // null while it is not loaded.
    object? Data { get; }

    // Changes when the collision is edited: it is read again.
    int CollisionRevision { get; }

    // Opens the walker's model, read in the background by the preview.
    void OpenCharacter(PreviewModel model);

    // Read in the background; null when the game has no such clips.
    PlayClips? ReadClips();

    // Called on the main thread when an area is to be read; the function returned then reads it in the background
    // (never two at once): the collision placed between two corners, map files and objects alike.
    Func<Vector3, Vector3, IEnumerable<PlacedCollision>> CollisionReader();
}
