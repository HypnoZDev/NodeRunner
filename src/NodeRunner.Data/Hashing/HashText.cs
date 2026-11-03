using System.Globalization;

namespace NodeRunner.Data.Hashing;

/// <summary>Hashes written as text, as tools and scripts show them.</summary>
public static class HashText
{
    /// <summary>
    /// Reads a hash: hexadecimal with "0x" (0x3073333B), or a decimal number, unsigned (812790587) or signed as Lua
    /// scripts print it (-1234567, the same 32 bits). Null when the text is not a hash.
    /// </summary>
    public static uint? TryParse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text.Length is > 2 and <= 10 && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hex)
                ? hex : null;
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint unsigned))
            return unsigned;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int signed))
            return unchecked((uint)signed);
        return null;
    }

    /// <summary>Text that can be a name of the game (letters, digits, _ - .): its hash may be looked for.</summary>
    public static bool IsName(string text) =>
        text.Length >= 2 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
