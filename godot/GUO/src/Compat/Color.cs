namespace GUO.Compat;

using System;

/// <summary>
/// 8-bit-per-channel RGBA colour, API-compatible with the FNA/XNA struct.
/// </summary>
/// <remarks>
/// <para>
/// This type is byte-based, unlike <see cref="Godot.Color"/> which stores
/// normalised floats. That difference is the reason the shim exists: UO's
/// hue tables, art decoding and gump tinting all work in 8-bit channels and
/// round-trip through packed 32-bit values. Converting the ported code to
/// float colour would change rounding behaviour across the whole renderer.
/// </para>
/// <para>
/// Channels are stored non-premultiplied, matching the original. Scaling a
/// colour by a float multiplies all four channels including alpha.
/// </para>
/// </remarks>
public struct Color : IEquatable<Color>
{
    /// <summary>Channels packed as 0xAABBGGRR (R in the lowest byte).</summary>
    public uint PackedValue;

    public Color(byte r, byte g, byte b, byte a)
    {
        PackedValue = (uint)(r | (g << 8) | (b << 16) | (a << 24));
    }

    public Color(byte r, byte g, byte b)
        : this(r, g, b, (byte)255)
    {
    }

    /// <summary>
    /// Builds a colour from normalised components. Values are clamped to
    /// 0..1 and scaled to 0..255, as the original does.
    /// </summary>
    public Color(float r, float g, float b, float a)
        : this(ToByte(r), ToByte(g), ToByte(b), ToByte(a))
    {
    }

    public Color(float r, float g, float b)
        : this(r, g, b, 1f)
    {
    }

    private static byte ToByte(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        // Round-half-away-from-zero then clamp, so 1.0f lands exactly on 255.
        int scaled = (int)MathF.Round(value * 255f);
        return (byte)Math.Clamp(scaled, 0, 255);
    }

    public byte R
    {
        readonly get => (byte)(PackedValue & 0xFF);
        set => PackedValue = (PackedValue & 0xFFFFFF00u) | value;
    }

    public byte G
    {
        readonly get => (byte)((PackedValue >> 8) & 0xFF);
        set => PackedValue = (PackedValue & 0xFFFF00FFu) | ((uint)value << 8);
    }

    public byte B
    {
        readonly get => (byte)((PackedValue >> 16) & 0xFF);
        set => PackedValue = (PackedValue & 0xFF00FFFFu) | ((uint)value << 16);
    }

    public byte A
    {
        readonly get => (byte)((PackedValue >> 24) & 0xFF);
        set => PackedValue = (PackedValue & 0x00FFFFFFu) | ((uint)value << 24);
    }

    // The handful of named colours the ported code actually reaches for.
    // Deliberately not the full XNA palette: an unused constant is a
    // maintenance cost, and the audit will flag any that turn out to be
    // needed when a file is ported.
    public static Color Transparent => new Color((byte)0, (byte)0, (byte)0, (byte)0);

    public static Color Black => new Color((byte)0, (byte)0, (byte)0);

    public static Color White => new Color((byte)255, (byte)255, (byte)255);

    public static Color Red => new Color((byte)255, (byte)0, (byte)0);

    public static Color Green => new Color((byte)0, (byte)255, (byte)0);

    public static Color Blue => new Color((byte)0, (byte)0, (byte)255);

    public static Color Yellow => new Color((byte)255, (byte)255, (byte)0);

    public static Color Gray => new Color((byte)128, (byte)128, (byte)128);

    /// <summary>Scales every channel, alpha included.</summary>
    public static Color operator *(Color value, float scale) =>
        new Color(
            ToByte(value.R / 255f * scale),
            ToByte(value.G / 255f * scale),
            ToByte(value.B / 255f * scale),
            ToByte(value.A / 255f * scale)
        );

    /// <summary>
    /// Linear interpolation between two colours. <paramref name="amount"/>
    /// is clamped to 0..1.
    /// </summary>
    public static Color Lerp(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Color(
            (byte)(a.R + ((b.R - a.R) * amount)),
            (byte)(a.G + ((b.G - a.G) * amount)),
            (byte)(a.B + ((b.B - a.B) * amount)),
            (byte)(a.A + ((b.A - a.A) * amount))
        );
    }

    public static bool operator ==(Color a, Color b) => a.PackedValue == b.PackedValue;

    public static bool operator !=(Color a, Color b) => !(a == b);

    public bool Equals(Color other) => this == other;

    public override bool Equals(object obj) => obj is Color other && Equals(other);

    public override int GetHashCode() => (int)PackedValue;

    public override string ToString() => $"{{R:{R} G:{G} B:{B} A:{A}}}";

    // --- engine boundary -----------------------------------------------

    public readonly Godot.Color ToGodot() =>
        new Godot.Color(R / 255f, G / 255f, B / 255f, A / 255f);

    public static Color FromGodot(Godot.Color c) => new Color(c.R, c.G, c.B, c.A);
}
