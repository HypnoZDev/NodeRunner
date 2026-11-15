using System.Security.Cryptography;
using NodeRunner.Data.Native.Archives;

namespace NodeRunner.Data.Native.Gta4;

// Encryption of the GTA IV archive tables: AES-256 ECB applied 16 times to each whole 16-byte block (a trailing
// partial block is left as is).
public sealed class Gta4Cipher(byte[] key)
{
    private const int Rounds = 16;

    // Fingerprint of the key in the game's executable (the key itself is read from the user's own game).
    private const ulong QuickFingerprint = 0x42E45E160822858C;
    private static readonly byte[] Sha256Fingerprint = Convert.FromHexString(
        "41C72CE6F112A278B89F42CD34FB9DE38074DE03CDA89D367CEF381D087A27EC");

    public static Gta4Cipher? FromExecutable(ReadOnlySpan<byte> exe)
    {
        byte[]? key = AesKeyFinder.Find(exe, QuickFingerprint, Sha256Fingerprint);
        return key is null ? null : new Gta4Cipher(key);
    }

    public void Decrypt(Span<byte> data) => Apply(data, encrypt: false);

    public void Encrypt(Span<byte> data) => Apply(data, encrypt: true);

    private void Apply(Span<byte> data, bool encrypt)
    {
        var blocks = data[..(data.Length & ~15)];
        if (blocks.IsEmpty)
            return;
        using var aes = Aes.Create();
        aes.Key = key;
        for (int i = 0; i < Rounds; i++)
        {
            if (encrypt)
                aes.EncryptEcb(blocks, blocks, PaddingMode.None);
            else
                aes.DecryptEcb(blocks, blocks, PaddingMode.None);
        }
    }
}
