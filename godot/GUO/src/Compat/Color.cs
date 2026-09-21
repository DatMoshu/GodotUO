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

    // The named colours the ported code actually reaches for, and only those:
    // an unused constant is a maintenance cost, and the bulk porter flags any
    // that turn out to be missing when a file lands.
    //
    // Every value below is taken from FNA's Color.cs, which documents each one
    // as R:_,G:_,B:_,A:_. They are NOT guessed from the name, because two of
    // them would have been guessed wrong: XNA's Green is (0,128,0) -- the CSS
    // "green" -- and (0,255,0) is Lime. This shim had Green as (0,255,0) until
    // the values were checked against the source, which would have rendered
    // three call sites in the wrong colour with nothing to point at why.
    public static Color Transparent => new Color((byte)0, (byte)0, (byte)0, (byte)0);

    public static Color Black => new Color((byte)0, (byte)0, (byte)0);

    public static Color White => new Color((byte)255, (byte)255, (byte)255);

    public static Color Red => new Color((byte)255, (byte)0, (byte)0);

    public static Color Green => new Color((byte)0, (byte)128, (byte)0);

    public static Color Blue => new Color((byte)0, (byte)0, (byte)255);

    public static Color Yellow => new Color((byte)255, (byte)255, (byte)0);

    public static Color Gray => new Color((byte)128, (byte)128, (byte)128);

    public static Color Silver => new Color((byte)192, (byte)192, (byte)192);

    public static Color DarkGray => new Color((byte)169, (byte)169, (byte)169);

    public static Color DimGray => new Color((byte)105, (byte)105, (byte)105);

    public static Color Lime => new Color((byte)0, (byte)255, (byte)0);

    public static Color LimeGreen => new Color((byte)50, (byte)205, (byte)50);

    public static Color YellowGreen => new Color((byte)154, (byte)205, (byte)50);

    public static Color Cyan => new Color((byte)0, (byte)255, (byte)255);

    public static Color DodgerBlue => new Color((byte)30, (byte)144, (byte)255);

    public static Color DeepSkyBlue => new Color((byte)0, (byte)191, (byte)255);

    public static Color CornflowerBlue => new Color((byte)100, (byte)149, (byte)237);

    public static Color Purple => new Color((byte)128, (byte)0, (byte)128);

    public static Color Orange => new Color((byte)255, (byte)165, (byte)0);

    public static Color Wheat => new Color((byte)245, (byte)222, (byte)179);

    public static Color Bisque => new Color((byte)255, (byte)228, (byte)196);

    public static Color Beige => new Color((byte)245, (byte)245, (byte)220);

    public static Color Aquamarine => new Color((byte)127, (byte)255, (byte)212);

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
