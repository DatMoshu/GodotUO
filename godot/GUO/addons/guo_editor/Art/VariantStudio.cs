#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using GUO.Assets;

/// <summary>
/// Saves generated gallery images as variant-atlas PNGs (one home for the
/// Art and StaticStudio docks): <c>&lt;dir&gt;/&lt;theme&gt;/static_0x0E77.png</c>
/// plus a theme entry mapping the original graphic at it. The variant is
/// fitted to the bound art's own pixel size so it drops in 1:1 at render.
/// Originals are never touched; the atlas only adds.
/// </summary>
public static class VariantStudio
{
    /// <summary>What happens to a generated image's background before it is saved or previewed.</summary>
    public enum BgMode
    {
        /// <summary>Keep every pixel as generated.</summary>
        Keep = 0,

        /// <summary>Pixels at or under the threshold go transparent (near-black model backdrops).</summary>
        BlackThreshold = 1,

        /// <summary>Keep generated pixels only where the bound art is opaque (its silhouette).</summary>
        OriginalMask = 2,
    }

    /// <summary>
    /// Strips a generated image's background in place (Keep is a no-op).
    /// BlackThreshold clears pixels whose every channel is at or under the
    /// threshold; OriginalMask keeps generated RGB only where the bound art
    /// (same pixel size) is opaque.
    /// </summary>
    public static void ExtractBackground(Image variant, Image bound, BgMode mode, int threshold)
    {
        if (variant == null || mode == BgMode.Keep)
        {
            return;
        }

        int w = variant.GetWidth(), h = variant.GetHeight();
        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (mode == BgMode.BlackThreshold)
        {
            byte t = (byte)Math.Clamp(threshold, 0, 255);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = variant.GetPixel(x, y);
                    if (c.R8 <= t && c.G8 <= t && c.B8 <= t)
                    {
                        variant.SetPixel(x, y, new Color(c.R, c.G, c.B, 0f));
                    }
                }
            }

            return;
        }

        if (bound == null)
        {
            return;
        }

        Image mask = bound;
        Image owned = null;
        if (bound.GetWidth() != w || bound.GetHeight() != h)
        {
            // A mask must stay crisp: nearest, never bilinear.
            owned = (Image)bound.Duplicate();
            owned.Resize(w, h, Image.Interpolation.Nearest);
            mask = owned;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask.GetPixel(x, y).A8 == 0)
                {
                    Color c = variant.GetPixel(x, y);
                    variant.SetPixel(x, y, new Color(c.R, c.G, c.B, 0f));
                }
            }
        }

        owned?.Dispose();
    }

    /// <summary>Null on success, else the reason it did not save.</summary>
    public static string SaveAtlasVariant(
        string theme, AssetKind kind, int id, Image bound, Image image,
        string tool, string workflow, long seed, string inputName,
        BgMode bgMode = BgMode.Keep, int bgThreshold = 12)
    {
        theme = (theme ?? "").Trim();
        if (theme.Length == 0)
        {
            return "name the theme first";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            if (theme.Contains(c))
            {
                return $"theme name cannot contain '{c}'";
            }
        }

        if (kind != AssetKind.Land && kind != AssetKind.Static)
        {
            return "variants cover land and statics";
        }

        if (bound == null || image == null)
        {
            return "nothing to save: bind art and generate an image first";
        }

        string dir = VariantAtlas.ResolveDir();
        if (string.IsNullOrWhiteSpace(dir))
        {
            return "no variant folder (GUO_VARIANT_DIR)";
        }

        int w = bound.GetWidth(), h = bound.GetHeight();
        if (w <= 0 || h <= 0)
        {
            return "the bound art has no pixels";
        }

        Image fit = FitTo(image, w, h);
        ExtractBackground(fit, bound, bgMode, bgThreshold);

        VariantKind vk = kind == AssetKind.Land ? VariantKind.Land : VariantKind.Static;
        string file = VariantAtlas.ImageFile(vk, id);
        string folder = Path.Combine(dir, theme);
        try
        {
            Directory.CreateDirectory(folder);
            using (fit)
            {
                if (fit.SavePng(Path.Combine(folder, file)) != Error.Ok)
                {
                    return $"could not write {file}";
                }
            }

            var sidecar = new JsonObject
            {
                ["tool"] = tool ?? "",
                ["workflow"] = workflow ?? "",
                ["input"] = inputName ?? "",
                ["replaces"] = $"0x{id:X4}",
                ["size"] = $"{w}x{h}",
            };
            if (seed >= 0)
            {
                sidecar["seed"] = seed;
            }

            File.WriteAllText(Path.Combine(folder, file + ".json"),
                sidecar.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            return $"could not write {file}: {ex.Message}";
        }

        try
        {
            string path = VariantAtlas.ThemePath(dir, theme);
            GUO.Game.Managers.Theme data = File.Exists(path)
                ? GUO.Game.Managers.Theme.Load(path)
                : new GUO.Game.Managers.Theme { Name = theme };
            data.Name = theme;
            string prefix = vk == VariantKind.Land ? "land_" : "static_";
            data.Entries.RemoveAll(e => e.Match.Contains((ushort)id)
                && (e.Image ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!data.Entries.Exists(e => e.Match.Contains((ushort)id) && e.Image == file))
            {
                data.Entries.Add(new GUO.Game.Managers.ThemeEntry
                {
                    Match = new System.Collections.Generic.List<ushort> { (ushort)id },
                    Image = file,
                });
            }

            GUO.Game.Managers.Theme.Save(path, data);
        }
        catch (Exception ex)
        {
            return $"saved {file} but the theme file failed: {ex.Message}";
        }

        try
        {
            VariantAtlas.LoadTheme(dir, theme);
        }
        catch (Exception ex)
        {
            return $"saved {file} but it did not load: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// A copy of an image fitted to exact pixel dimensions (the atlas draws
    /// variants 1:1 over the bound art). Same size in, same object out.
    /// </summary>
    public static Image FitTo(Image image, int w, int h)
    {
        var fit = (Image)image.Duplicate();
        if (fit.GetWidth() != w || fit.GetHeight() != h)
        {
            fit.Resize(w, h, Image.Interpolation.Bilinear);
        }

        return fit;
    }
}
#endif
