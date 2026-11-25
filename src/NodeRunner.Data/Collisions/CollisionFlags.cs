namespace NodeRunner.Data.Collisions;

// The 16 options a collision surface can have besides its material (stored with it, bit by bit), in the game's order.
public static class CollisionFlags
{
    public const ushort Stairs = 1 << 0;
    public const ushort NotClimbable = 1 << 1;
    public const ushort SeeThrough = 1 << 2;
    public const ushort ShootThrough = 1 << 3;
    public const ushort NotCover = 1 << 4;
    public const ushort WalkablePath = 1 << 5;
    public const ushort NoCameraCollision = 1 << 6;
    public const ushort ShootThroughEffects = 1 << 7;
    public const ushort NoDecal = 1 << 8;
    public const ushort NoNavMesh = 1 << 9;
    public const ushort NoRagdoll = 1 << 10;
    public const ushort VehicleWheel = 1 << 11;
    public const ushort NoParticles = 1 << 12;
    public const ushort TooSteepForPlayer = 1 << 13;
    public const ushort NoNetworkSpawn = 1 << 14;
    public const ushort NoCameraCollisionAllowClipping = 1 << 15;

    // Bit -> the game's name of the option.
    public static IReadOnlyList<(ushort Bit, string Name)> All { get; } =
    [
        (Stairs, "STAIRS"),
        (NotClimbable, "NOT_CLIMBABLE"),
        (SeeThrough, "SEE_THROUGH"),
        (ShootThrough, "SHOOT_THROUGH"),
        (NotCover, "NOT_COVER"),
        (WalkablePath, "WALKABLE_PATH"),
        (NoCameraCollision, "NO_CAM_COLLISION"),
        (ShootThroughEffects, "SHOOT_THROUGH_FX"),
        (NoDecal, "NO_DECAL"),
        (NoNavMesh, "NO_NAVMESH"),
        (NoRagdoll, "NO_RAGDOLL"),
        (VehicleWheel, "VEHICLE_WHEEL"),
        (NoParticles, "NO_PTFX"),
        (TooSteepForPlayer, "TOO_STEEP_FOR_PLAYER"),
        (NoNetworkSpawn, "NO_NETWORK_SPAWN"),
        (NoCameraCollisionAllowClipping, "NO_CAM_COLLISION_ALLOW_CLIPPING"),
    ];
}
