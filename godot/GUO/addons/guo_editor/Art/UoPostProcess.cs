#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using Godot;

/// <summary>
/// The one UO post-process every import path shares (ADR-0029 decision 6): the
/// transparency key (UO has one-bit transparency: alpha at or above half is opaque),
/// the isometric diamond mask for land, trimming to UO's conventions, and reduction
/// to UO's 16-bit colour. Nearest neighbour wherever it scales: never filtered.
/// </summary>
/// <remarks>
/// Trimming a static removes empty rows at the top (the client anchors a static at
/// its bottom edge, so rows above never move it) and the same number of empty columns
/// from both sides (it centres on the width), so a trimmed image is drawn where the
/// untrimmed one was. Rows at the bottom and uneven columns are kept for that reason.
/// </remarks>
public static class UoPostProcess
{
    public sealed class Result
    {
        public Image Image;
        public string Error;
        public List<string> Notes = new();
    }

    public static Result Run(Image source, AssetKind kind)
    {
        var r = new Result();
        if (source == null || source.IsEmpty())
        {
            r.Error = "no image";
            return r;
        }

        Image img = (Image)source.Duplicate();
        if (img.IsCompressed())
        {
            img.Decompress();
        }

        img.Convert(Image.Format.Rgba8);

        // A land tile may arrive larger (an upscaled edit); a square one is brought back by nearest sampling.
        if (kind == AssetKind.Land && (img.GetWidth() != 44 || img.GetHeight() != 44))
        {
            if (img.GetWidth() != img.GetHeight() || img.GetWidth() % 44 != 0)
            {
                r.Error = $"land art is 44x44; this image is {img.GetWidth()}x{img.GetHeight()} and is not a whole multiple of it";
                return r;
            }

            r.Notes.Add($"scaled {img.GetWidth()}x{img.GetHeight()} to 44x44 (nearest)");
            img.Resize(44, 44, Image.Interpolation.Nearest);
        }

        int w = img.GetWidth(), h = img.GetHeight();
        int max = kind == AssetKind.Gump ? AssetOverlay.MaxGumpSize : AssetOverlay.MaxStaticSize;
        if (kind != AssetKind.Land && (w > max || h > max))
        {
            r.Error = $"{(kind == AssetKind.Gump ? "a gump" : "static art")} is at most {max}x{max}; this image is {w}x{h}";
            return r;
        }

        // The transparency key.
        int partial = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = img.GetPixel(x, y);
                int a = c.A8;
                if (a > 0 && a < 255)
                {
                    partial++;
                }

                bool opaque = a >= 128;
                if (kind == AssetKind.Land)
                {
                    opaque = AssetOverlay.InDiamond(x, y);
                }

                img.SetPixel(x, y, opaque ? new Color(c.R, c.G, c.B, 1f) : new Color(0, 0, 0, 0));
            }
        }

        if (partial > 0 && kind != AssetKind.Land)
        {
            r.Notes.Add($"{partial} partly transparent pixels keyed at 50%");
        }

        if (kind == AssetKind.Static)
        {
            img = TrimStatic(img, r);
        }

        // Reduce to UO's 15-bit colour exactly as the overlay stores it.
        ushort[] px = AssetOverlay.ToUo(img, kind == AssetKind.Land);
        r.Image = AssetOverlay.FromUo(px, img.GetWidth(), img.GetHeight(), kind == AssetKind.Land);
        r.Notes.Add("reduced to UO 16-bit (ARGB1555)");
        return r;
    }

    private static Image TrimStatic(Image img, Result r)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        int top = 0;
        while (top < h - 1 && RowEmpty(img, top))
        {
            top++;
        }

        int left = 0;
        while (left < w - 1 && ColumnEmpty(img, left))
        {
            left++;
        }

        int right = 0;
        while (right < w - 1 - left && ColumnEmpty(img, w - 1 - right))
        {
            right++;
        }

        int side = System.Math.Min(left, right);
        if (top == 0 && side == 0)
        {
            return img;
        }

        r.Notes.Add($"trimmed {top} empty rows from the top and {side} columns from each side");
        return img.GetRegion(new Rect2I(side, top, w - 2 * side, h - top));
    }

    private static bool RowEmpty(Image img, int y)
    {
        for (int x = 0; x < img.GetWidth(); x++)
        {
            if (img.GetPixel(x, y).A8 != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ColumnEmpty(Image img, int x)
    {
        for (int y = 0; y < img.GetHeight(); y++)
        {
            if (img.GetPixel(x, y).A8 != 0)
            {
                return false;
            }
        }

        return true;
    }
}
#endif
