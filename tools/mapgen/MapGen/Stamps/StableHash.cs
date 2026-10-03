using System.Text;

namespace CentrED.MapGen.Stamps;

/// <summary>
/// Process-independent hashing. <see cref="string.GetHashCode()"/> is randomised per
/// process in .NET, so seeding an RNG from it makes "same seed" runs differ between
/// two invocations of the CLI. Every pass that mixes a name into a seed uses this
/// instead (FNV-1a over UTF-8).
/// </summary>
public static class StableHash
{
    private const ulong FnvOffset64 = 0xCBF29CE484222325UL;
    private const ulong FnvPrime64 = 0x100000001B3UL;

    /// <summary>64-bit FNV-1a of the UTF-8 bytes of <paramref name="s"/>.</summary>
    public static ulong Fnv1a64(string s)
    {
        ulong h = FnvOffset64;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= FnvPrime64;
        }
        return h;
    }

    /// <summary>Mixes extra 64-bit values into an FNV-1a state (little-endian byte order).</summary>
    public static ulong Mix(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(value >> (i * 8));
            h *= FnvPrime64;
        }
        return h;
    }

    /// <summary>Deterministic name-based GUID (RFC 4122 version 5 layout over FNV-1a), stable across processes.</summary>
    public static Guid Guid(string name)
    {
        ulong a = Fnv1a64(name);
        ulong b = Fnv1a64("guid:" + name);
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), a);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), b);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new System.Guid(bytes);
    }
}
