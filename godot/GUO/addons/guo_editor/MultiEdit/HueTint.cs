#if TOOLS
namespace GUO.Editor;

using Godot;
using GUO.Assets;

/// <summary>Hues an image on the CPU with upstream's own path (<see cref="HuesLoader"/>), as the Hues panel does.</summary>
public static class HueTint
{
    public static Image Apply(EditorData data, Image src, ushort hue, bool partial)
    {
        HuesLoader hues = data.Files.Hues;
        Image dst = (Image)src.Duplicate();
        for (int y = 0; y < src.GetHeight(); y++)
        {
            for (int x = 0; x < src.GetWidth(); x++)
            {
                Color c = src.GetPixel(x, y);
                if (c.A == 0)
                {
                    continue;
                }

                // Back to UO's 5-bit channels; the hue table is indexed by red.
                ushort c16 = (ushort)((To5(c.R8) << 10) | (To5(c.G8) << 5) | To5(c.B8));
                uint rgb = partial ? hues.GetPartialHueColor(c16, hue) : hues.GetColor(c16, hue);
                dst.SetPixel(x, y, Color.Color8((byte)(rgb & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)((rgb >> 16) & 0xFF), 0xFF));
            }
        }

        return dst;
    }

    private static int To5(int c8) => (c8 * 31 + 127) / 255;
}
#endif
