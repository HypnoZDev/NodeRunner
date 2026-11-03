namespace NodeRunner.Data.Hashing;

// Jenkins "one-at-a-time" hash, used by the game to identify names (models, scenario types, files...). The game
// lower-cases names before hashing.
public static class Joaat
{
    public static uint Hash(string text)
    {
        uint hash = 0;
        foreach (char c in text)
        {
            hash += (byte)char.ToLowerInvariant(c);
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}
