// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Utility;

namespace GUO.Pregame3D;

/// <summary>
/// The character being made, drawn as the world draws a mobile: its body's
/// animation frame, then each worn item's equipment frame in the client's
/// layer order (LayerOrder), each hued as the shader would (a full hue, or a
/// partial one on grey pixels only), composed on the CPU from the client's
/// own atlas at runtime into one image per direction and frame. Nothing is
/// shipped: the pixels are the player's install's.
/// </summary>
internal static class Mannequin
{
    /// <summary>The canvas each figure is composed on, in UO pixels; its feet at <see cref="Foot"/>.</summary>
    public static readonly Vector2I Canvas = new(128, 140);
    public static readonly Vector2I Foot = new(64, 128);

    private const byte StandGroup = (byte) PeopleAnimationGroup.Stand;

    private static readonly Dictionary<ulong, Image> _pages = new();

    /// <summary>How many frames the stand animation has for this body and direction (at least 1).</summary>
    public static int FrameCount(PlayerMobile m, byte direction)
    {
        byte dir = direction;
        bool mirror = false;
        Client.Game.UO.Animations.GetAnimDirection(ref dir, ref mirror);
        var frames = Client.Game.UO.Animations.GetAnimationFrames(m.Graphic, StandGroup, dir, out _, out _);

        return Math.Max(1, frames.Length);
    }

    /// <summary>
    /// The figure facing <paramref name="direction"/> (0..7, UO's), stand frame
    /// <paramref name="frame"/>; <paramref name="complete"/> is false when a
    /// layer's art had not reached the atlas yet (ask again next frame).
    /// </summary>
    public static Image Compose(PlayerMobile m, byte direction, int frame, out bool complete)
    {
        complete = true;
        Image canvas = Image.CreateEmpty(Canvas.X, Canvas.Y, false, Image.Format.Rgba8);
        var atlases = new Dictionary<ulong, Image>();
        byte dir = direction;
        bool mirror = false;
        Client.Game.UO.Animations.GetAnimDirection(ref dir, ref mirror);

        complete &= Layer(canvas, atlases, m.Graphic, dir, mirror, frame, m.Hue, false, false, m.Graphic);

        int orderRow = Math.Clamp(direction & 7, 0, 7);

        for (int i = 0; i < Constants.USED_LAYER_COUNT; i++)
        {
            Layer layer = LayerOrder.UsedLayers[orderRow, i];
            Item item = m.FindItemByLayer(layer);

            if (item == null || item.ItemData.AnimID == 0)
            {
                continue;
            }

            ushort graphic = item.ItemData.AnimID;
            ushort hue = item.Hue;

            // An elf's or a gargoyle's own art for the same item (EquipConversions).
            if (Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(m.Graphic, out Dictionary<ushort, EquipConvData> map)
                && map.TryGetValue(item.ItemData.AnimID, out EquipConvData conv))
            {
                graphic = conv.Graphic;

                if (hue == 0)
                {
                    hue = conv.Color;
                }
            }

            bool partial = item.ItemData.IsPartialHue;

            if ((hue & 0x8000) != 0)
            {
                partial = true;
                hue &= 0x7FFF;
            }

            complete &= Layer(canvas, atlases, graphic, dir, mirror, frame, hue, partial, true, m.Graphic);
        }

        return canvas;
    }

    private static bool Layer(Image canvas, Dictionary<ulong, Image> atlases, ushort graphic, byte dir, bool mirror, int frame, ushort hue, bool partial, bool isEquip, ushort body)
    {
        var frames = Client.Game.UO.Animations.GetAnimationFrames(graphic, StandGroup, dir, out ushort hueFromFile, out _, isEquip);

        if (frames.Length == 0)
        {
            return true; // no art for it: nothing to draw, nothing to wait for
        }

        ref var sprite = ref frames[frame % frames.Length];

        if (sprite.Texture == null)
        {
            return false;
        }

        ulong key = sprite.Texture.GetRid().Id;

        // An atlas page read back once and kept (a read-back is the whole page):
        // dropped again when a sprite on it is not in the copy yet.
        if (!atlases.TryGetValue(key, out Image atlas) && !_pages.TryGetValue(key, out atlas))
        {
            atlas = sprite.Texture.GetImage();

            if (atlas == null)
            {
                return false;
            }

            if (atlas.GetFormat() != Image.Format.Rgba8)
            {
                atlas.Convert(Image.Format.Rgba8);
            }

            atlases[key] = atlas;
            _pages[key] = atlas;
        }

        if (hue == 0)
        {
            hue = hueFromFile;
            partial = false;
        }

        int w = sprite.UV.Width, h = sprite.UV.Height;
        Image part = atlas.GetRegion(new Rect2I(sprite.UV.X, sprite.UV.Y, w, h));

        if (part.IsInvisible())
        {
            _pages.Remove(key);
            return false;
        }

        // Where the world would draw it (MobileView.DrawInternal), on our canvas.
        int x = Foot.X - (mirror ? w - sprite.Center.X : sprite.Center.X);
        int y = Foot.Y - (h + sprite.Center.Y);

        for (int py = 0; py < h; py++)
        {
            int cy = y + py;

            if (cy < 0 || cy >= canvas.GetHeight())
            {
                continue;
            }

            for (int px = 0; px < w; px++)
            {
                Color c = part.GetPixel(px, py);

                if (c.A8 == 0)
                {
                    continue;
                }

                int cx = x + (mirror ? w - 1 - px : px);

                if (cx < 0 || cx >= canvas.GetWidth())
                {
                    continue;
                }

                canvas.SetPixel(cx, cy, Hued(c, hue, partial));
            }
        }

        return true;
    }

    /// <summary>A pixel through a hue, as the client's hue shader does it.</summary>
    public static Color Hued(Color c, ushort hue, bool partial)
    {
        if (hue == 0)
        {
            return c;
        }

        HuesLoader hues = Client.Game.UO.FileManager.Hues;

        if (hue >= hues.HuesCount)
        {
            return c;
        }

        if (partial && !(c.R8 == c.G8 && c.R8 == c.B8))
        {
            return c;
        }

        int index = hue - 1;
        ushort c16 = hues.HuesRange[index >> 3].Entries[index % 8].ColorTable[c.R8 >> 3];

        return FromColor32(HuesHelper.Color16To32(c16), c.A);
    }

    /// <summary>One colour standing for a hue (a swatch): its table's middle entry.</summary>
    public static Color Swatch(ushort hue)
    {
        HuesLoader hues = Client.Game.UO.FileManager.Hues;
        hue &= 0x7FFF;

        if (hue == 0 || hue >= hues.HuesCount)
        {
            return new Color(0.5f, 0.5f, 0.5f);
        }

        return FromColor32(hues.GetPolygoneColor(16, hue), 1f);
    }

    private static Color FromColor32(uint cl, float alpha) =>
        new((cl & 0xFF) / 255f, ((cl >> 8) & 0xFF) / 255f, ((cl >> 16) & 0xFF) / 255f, alpha);
}
