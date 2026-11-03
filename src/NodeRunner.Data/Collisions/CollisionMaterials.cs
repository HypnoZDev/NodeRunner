namespace NodeRunner.Data.Collisions;

// Families of collision materials, used for colours and layers.
public enum CollisionSurface
{
    Road,

    Terrain,

    Vegetation,

    Water,
    Glass,

    Building,
}

// Names of the game's collision materials, in the order of the game's list (the index stored with each triangle),
// read from the game's materials.dat.
public sealed class CollisionMaterials
{
    public const string FileName = "materials.dat";

    private readonly string[] _names;
    private readonly CollisionSurface[] _surfaces;

    private CollisionMaterials(string[] names)
    {
        _names = names;
        _surfaces = names.Select(SurfaceOf).ToArray();
    }

    public static CollisionMaterials Empty { get; } = new([]);

    public int Count => _names.Length;

    // Reads materials.dat: one material per line starting with its name; lines starting with '#' are comments, the
    // first line is the version number.
    public static CollisionMaterials Parse(string text)
    {
        var names = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || !char.IsLetter(line[0]))
                continue;
            int end = line.IndexOfAny([' ', '\t', '\r']);
            names.Add(end < 0 ? line : line[..end]);
        }
        return new CollisionMaterials(names.ToArray());
    }

    public string Name(byte index) => index < _names.Length ? _names[index] : $"#{index}";

    public CollisionSurface Surface(byte index) => index < _surfaces.Length ? _surfaces[index] : CollisionSurface.Building;

    public static CollisionSurface SurfaceOf(string name)
    {
        static bool Any(string name, params string[] prefixes) =>
            prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (Any(name, "TARMAC", "CONCRETE", "PAVING", "COBBLESTONE", "BRICK_PAVEMENT", "RUMBLE_STRIP", "METAL_SOLID_ROAD", "ICE_TARMAC", "SNOW_TARMAC"))
            return CollisionSurface.Road;
        if (Any(name, "GRASS", "HAY", "BUSHES", "TWIGS", "LEAVES", "WOODCHIPS", "TREE_BARK"))
            return CollisionSurface.Vegetation;
        if (Any(name, "SAND", "SNOW", "ICE", "GRAVEL", "DIRT", "MUD", "MARSH", "SOIL", "CLAY", "ROCK", "STONE"))
            return CollisionSurface.Terrain;
        if (Any(name, "WATER", "PUDDLE"))
            return CollisionSurface.Water;
        if (Any(name, "GLASS", "PERSPEX", "CAR_GLASS", "EMISSIVE_GLASS"))
            return CollisionSurface.Glass;
        return CollisionSurface.Building;
    }
}
