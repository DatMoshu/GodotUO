#if TOOLS
namespace GUO.Editor;

using System;

internal enum HueRangeOperation { Spread, Exponential, Reverse, ShiftRgb }

/// <summary>Pure palette operations measured against the pinned Fiddler hue editor.</summary>
internal static class HueRange
{
    internal static void Apply(HueDocument document, int first, int last, HueRangeOperation operation, int red = 0, int green = 0, int blue = 0)
    {
        Validate(first, last, operation, red, green, blue);
        document.TransformPalette(colors => Transform(colors, first, last, operation, red, green, blue));
    }

    private static void Validate(int first, int last, HueRangeOperation operation, int red, int green, int blue)
    {
        if (first < 0 || first > 31 || last < 0 || last > 31) throw new ArgumentException("Palette range must use indices 0–31.");
        if (!Enum.IsDefined(operation)) throw new ArgumentException("Choose a supported palette operation.");
        if (red < -255 || red > 255 || green < -255 || green > 255 || blue < -255 || blue > 255)
            throw new ArgumentException("RGB shifts must be between -255 and 255.");
    }

    internal static ushort[] Transform(ushort[] colors, int first, int last, HueRangeOperation operation, int red = 0, int green = 0, int blue = 0)
    {
        Validate(first, last, operation, red, green, blue);
        if (colors == null || colors.Length != 32) throw new ArgumentException("A hue palette needs exactly 32 colors.");
        ushort[] result = (ushort[])colors.Clone();
        int start = Math.Min(first, last), end = Math.Max(first, last), distance = end - start;
        if (operation == HueRangeOperation.Reverse) { Array.Reverse(result, start, distance + 1); return result; }
        if (operation == HueRangeOperation.ShiftRgb)
        {
            for (int i = start; i <= end; i++) result[i] = Pack(Math.Clamp(Channel(colors[i], 10) + red, 0, 255),
                Math.Clamp(Channel(colors[i], 5) + green, 0, 255), Math.Clamp(Channel(colors[i], 0) + blue, 0, 255));
            return result;
        }
        if (distance <= 1) return result;
        int Interpolate(int shift, int position)
        {
            int a = Channel(colors[start], shift), b = Channel(colors[end], shift);
            double value;
            if (operation == HueRangeOperation.Spread)
            {
                float step = (float)(b - a) / (distance - 1);
                value = a + position * step;
            }
            else
            {
                double exponent = Math.Log(Math.Abs(b - a)) / Math.Log(distance);
                value = a + (b < a ? -1 : 1) * Math.Pow(position, exponent);
            }
            int channel = double.IsFinite(value) ? (int)value : -1;
            if (channel < 0 || channel > 255)
                throw new ArgumentException("Exponential gradient exceeds RGB limits for these endpoints. Choose different endpoints or use Spread gradient. No colors changed.");
            return channel;
        }
        for (int i = 1; i < distance; i++) result[start + i] = Pack(Interpolate(10, i), Interpolate(5, i), Interpolate(0, i));
        return result;
    }

    private static int Channel(ushort color, int shift)
    { int value = (color >> shift) & 31; return (value << 3) | (value >> 2); }
    private static ushort Pack(int red, int green, int blue)
    {
        int value = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
        return (ushort)(value == 0 && (red | green | blue) != 0 ? 1 : value);
    }
}
#endif
