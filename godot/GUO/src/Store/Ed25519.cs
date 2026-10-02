// SPDX-License-Identifier: BSD-2-Clause
// Ed25519 signature verification (RFC 8032), for signed catalogue indexes (ADR-0026).
// The RFC's reference algorithm, line for line with tools/asset_store/ed25519.py,
// on System.Numerics so every platform (including the browser build, which has
// no signature APIs) verifies the same way. Verify only: the client never signs.
using System;
using System.Numerics;
using System.Security.Cryptography;

namespace GUO.Store;

internal static class Ed25519
{
    public const string Prefix = "ed25519:";
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) G = Base();

    private static BigInteger Mod(BigInteger x)
    {
        var r = x % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger Inv(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

    private static (BigInteger, BigInteger, BigInteger, BigInteger) Base()
    {
        var y = Mod(4 * Inv(5));
        var x = RecoverX(y, 0).Value;
        return (x, y, 1, Mod(x * y));
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Add(
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) a, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) b)
    {
        var aa = Mod((a.Y - a.X) * (b.Y - b.X));
        var bb = Mod((a.Y + a.X) * (b.Y + b.X));
        var cc = Mod(2 * a.T * b.T * D);
        var dd = Mod(2 * a.Z * b.Z);
        BigInteger e = bb - aa, f = dd - cc, g = dd + cc, h = bb + aa;
        return (Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Mul(
        BigInteger s, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) point)
    {
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) result = (0, 1, 1, 0);
        while (s.Sign > 0)
        {
            if (!s.IsEven) result = Add(result, point);
            point = Add(point, point);
            s >>= 1;
        }
        return result;
    }

    private static bool Equal((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) a, (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) b) =>
        Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inv(D * y * y + 1));
        if (x2.IsZero) return sign != 0 ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
        if (!Mod(x * x - x2).IsZero) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T)? Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length != 32) return null;
        var y = new BigInteger(data, isUnsigned: true, isBigEndian: false);
        int sign = (int)(y >> 255);
        y &= (BigInteger.One << 255) - 1;
        var x = RecoverX(y, sign);
        return x == null ? null : (x.Value, y, 1, Mod(x.Value * y));
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var a = Decompress(publicKey);
        var r = Decompress(signature[..32]);
        if (a == null || r == null) return false;
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= Q) return false;
        byte[] input = new byte[64 + message.Length];
        signature[..32].CopyTo(input);
        publicKey.CopyTo(input.AsSpan(32));
        message.CopyTo(input.AsSpan(64));
        var h = new BigInteger(SHA512.HashData(input), isUnsigned: true, isBigEndian: false) % Q;
        return Equal(Mul(s, G), Add(r.Value, Mul(h, a.Value)));
    }

    /// <summary>"ed25519:" + standard base64, as tools/asset_store/ed25519.py writes keys and signatures.</summary>
    public static byte[] Decode(string text, int length)
    {
        text = text?.Trim();
        StorePack.Require(text != null && text.StartsWith(Prefix, StringComparison.Ordinal), "Expected an ed25519: value.");
        byte[] raw = null;
        try { raw = Convert.FromBase64String(text[Prefix.Length..]); }
        catch (FormatException) { StorePack.Require(false, "An ed25519: value is not valid base64."); }
        StorePack.Require(raw.Length == length, $"An ed25519: value must be {length} bytes.");
        return raw;
    }

    /// <summary>What a person compares: the first 16 hex digits of the key's SHA-256, in groups of four.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        string hex = Convert.ToHexString(SHA256.HashData(publicKey))[..16];
        return string.Join(' ', hex[..4], hex[4..8], hex[8..12], hex[12..16]);
    }
}
