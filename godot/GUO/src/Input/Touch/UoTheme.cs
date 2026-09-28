// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;

namespace GUO.Input.Touch;

/// <summary>
/// The look GUO's own Godot UI shares with the 1997 client (C10): the client's
/// unicode font 1 as a Godot bitmap font, UO's resizepic frames and buttons
/// as style boxes, UO's checkbox, slider and scrollbar art as icons. Every
/// piece is the client's own gump art, drawn at a whole-number scale and
/// sampled nearest-neighbour. The design, and the rules for anyone building
/// Godot UI in GUO, are docs/ui/uo_godot_style.md.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): ClassicUO has no Godot UI; this only styles GUO's
/// own mobile cards (the window menu, the companion tabs, the command bar's
/// slot editor). The theme is built in art pixels: a control tree that uses
/// it is scaled by a whole number (<see cref="PixelScale"/>) and drawn with
/// nearest sampling, so one art pixel is always a square of device pixels.
/// Without the client data (the gumps not loaded yet) the pieces fall back to
/// flat colours of the same palette.
/// </remarks>
internal static class UoTheme
{
    // --- the palette (docs/ui/uo_godot_style.md) ----------------------------

    /// <summary>Text on stone and parchment: UO's ink.</summary>
    public static readonly Color Ink = new("1c1812");

    /// <summary>Secondary text on stone.</summary>
    public static readonly Color Muted = new("4e463c");

    /// <summary>Headings on stone, and lit states: the top bar's hover gold, darkened for stone.</summary>
    public static readonly Color Heading = new("5a3a0c");

    /// <summary>Gold, on dark: the command bar's lit caption.</summary>
    public static readonly Color Gold = new("e0b050");

    /// <summary>Cream, for text on the dark band.</summary>
    public static readonly Color Cream = new("eeeade");

    /// <summary>The destructive action: UO's murderer red, darkened for stone.</summary>
    public static readonly Color Danger = new("8c1c12");

    // --- the art ---------------------------------------------------------------

    /// <summary>A card: the grey stone resizepic (0x13BE..0x13C6).</summary>
    public const ushort StoneFrame = 0x13BE;

    /// <summary>A field or a list: the parchment text entry (0x0BB8..0x0BC0).</summary>
    public const ushort FieldFrame = 0x0BB8;

    /// <summary>A dark panel: the blue-black stone (0x2436..0x243E).</summary>
    public const ushort DarkFrame = 0x2436;

    /// <summary>A button: the top bar's marble plate, 23 art px tall.</summary>
    public const ushort ButtonPlate = 0x098D;

    public const ushort CheckOff = 0x00D2, CheckOn = 0x00D3;
    public const ushort SliderLeft = 0x00D5, SliderMid = 0x00D6, SliderRight = 0x00D7, SliderThumb = 0x00D8;
    public const ushort ScrollTrack = 0x0100, ScrollThumb = 0x00FE;

    /// <summary>
    /// A selected plate (a picked tab, the chosen action): lightly shaded and
    /// captioned in <see cref="Heading"/>. A press is darker still, and only
    /// while the finger is down.
    /// </summary>
    public const float SelectedShade = 0.86f;

    /// <summary>The font's size in art pixels (font 1 is drawn at 1x of this).</summary>
    public const int FontSize = 16;

    /// <summary>The plate's height, and so a button's.</summary>
    public const int ButtonHeight = 23;

    /// <summary>
    /// The whole-number scale for a card on this device, from the screen's
    /// physical density: a plate (23 art px) about 5 mm tall, the command
    /// bar's own size. 3 on the Thor (369 dpi) and the Odin 2 Mini, 1 on a
    /// desktop monitor. Art pixels times this are device pixels. Not the
    /// client's DpiScale, which is 1 on the Thor.
    /// </summary>
    public static int PixelScale
    {
        get
        {
            // GUO_UI_SCALE sets it, so a desktop run can show a handheld's cards.
            if (int.TryParse(OS.GetEnvironment("GUO_UI_SCALE"), out int forced) && forced > 0)
            {
                return forced;
            }

            int dpi = DisplayServer.ScreenGetDpi();
            return Math.Clamp((int)Math.Round(dpi / 130f), 1, 4);
        }
    }

    // --- the font ------------------------------------------------------------

    private static FontFile _font;

    /// <summary>
    /// The client's unicode font 1 as a Godot bitmap font at <see cref="FontSize"/>,
    /// white (colour comes from the theme), scaling by whole numbers only.
    /// Glyphs are drawn by the same FontsLoader call RenderedText makes, one
    /// character at a time. Characters the font lacks fall back to Godot's.
    /// </summary>
    public static Font Font
    {
        get
        {
            if (_font == null)
            {
                _font = BuildFont();
            }

            return (Font)_font ?? ThemeDB.FallbackFont;
        }
    }

    private const byte UoFont = 1;

    /// <summary>Printable ASCII and the few symbols GUO's UI uses.</summary>
    private static IEnumerable<char> Charset()
    {
        for (char c = (char)33; c < (char)127; c++)
        {
            yield return c;
        }

        foreach (char c in "‹›…·–—’“”éèàäöüß")
        {
            yield return c;
        }
    }

    private static FontFile BuildFont()
    {
        FontsLoader fonts = Client.Game?.UO?.FileManager?.Fonts;

        if (fonts == null || !fonts.UnicodeFontExists(UoFont))
        {
            return null;
        }

        var glyphs = new List<(char c, Image image, int advance)>();

        foreach (char c in Charset())
        {
            FontsLoader.FontInfo fi = fonts.GenerateUnicode(UoFont, c.ToString(), 0, 30, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0);

            if (fi.Data == null || fi.Width <= 0 || fi.Height <= 0)
            {
                continue;
            }

            // A uint here is 0xAABBGGRR: R,G,B,A in memory. White, keeping the
            // coverage, so the theme's colour tints it.
            var rgba = new byte[fi.Width * fi.Height * 4];
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(new ReadOnlySpan<uint>(fi.Data, 0, fi.Width * fi.Height))
                .CopyTo(rgba);

            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i + 3] != 0)
                {
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                    rgba[i + 3] = 255;
                }
            }

            // The advance is the client's own: offset + width + 1 (GetWidthUnicode).
            glyphs.Add((c, Image.CreateFromData(fi.Width, fi.Height, false, Image.Format.Rgba8, rgba), fonts.GetWidthUnicode(UoFont, c.ToString())));
        }

        if (glyphs.Count == 0)
        {
            return null;
        }

        // One atlas, rows of glyphs with a pixel between.
        const int atlasWidth = 512;
        int x = 0, y = 0, row = 0, lineHeight = 0;
        var places = new List<(char c, Image image, int x, int y, int advance)>();

        foreach ((char c, Image image, int advance) in glyphs)
        {
            if (x + image.GetWidth() + 1 > atlasWidth)
            {
                x = 0;
                y += row + 1;
                row = 0;
            }

            places.Add((c, image, x, y, advance));
            x += image.GetWidth() + 1;
            row = Math.Max(row, image.GetHeight());
            lineHeight = Math.Max(lineHeight, image.GetHeight());
        }

        int atlasHeight = (int)NextPow2(y + row + 1);
        Image atlas = Image.CreateEmpty(atlasWidth, atlasHeight, false, Image.Format.Rgba8);

        foreach ((char c, Image image, int px, int py, int _) in places)
        {
            atlas.BlitRect(image, new Rect2I(0, 0, image.GetWidth(), image.GetHeight()), new Vector2I(px, py));
        }

        // Each glyph's image starts at the line's top, so every glyph sits
        // the same distance above the baseline: the ascent.
        int ascent = Math.Max(1, lineHeight - 3);
        var size = new Vector2I(FontSize, 0);
        var font = new FontFile
        {
            FixedSize = FontSize,
            FixedSizeScaleMode = TextServer.FixedSizeScaleMode.IntegerOnly,
            Antialiasing = TextServer.FontAntialiasing.None,
            GenerateMipmaps = false,
            Fallbacks = new Godot.Collections.Array<Font> { ThemeDB.FallbackFont },
        };

        font.SetCacheAscent(0, FontSize, ascent);
        font.SetCacheDescent(0, FontSize, lineHeight - ascent);
        font.SetTextureImage(0, size, 0, atlas);

        foreach ((char c, Image image, int px, int py, int advance) in places)
        {
            int g = c;
            font.SetGlyphAdvance(0, FontSize, g, new Vector2(advance > 0 ? advance : image.GetWidth(), 0));
            font.SetGlyphOffset(0, size, g, new Vector2(0, -ascent));
            font.SetGlyphSize(0, size, g, new Vector2(image.GetWidth(), image.GetHeight()));
            font.SetGlyphUVRect(0, size, g, new Rect2(px, py, image.GetWidth(), image.GetHeight()));
            font.SetGlyphTextureIdx(0, size, g, 0);
        }

        // A space as the client spaces words (FontsLoader's UNICODE_SPACE_WIDTH).
        font.SetGlyphAdvance(0, FontSize, ' ', new Vector2(8, 0));
        font.SetGlyphSize(0, size, ' ', Vector2.Zero);
        font.SetGlyphUVRect(0, size, ' ', new Rect2());
        font.SetGlyphTextureIdx(0, size, ' ', -1);

        return font;
    }

    private static long NextPow2(int v)
    {
        long p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    // --- gump images -----------------------------------------------------------

    private static readonly Dictionary<ushort, Image> _images = new();
    private static readonly Dictionary<ushort, Texture2D> _textures = new();

    /// <summary>A gump as an image of its own (a copy out of the atlas), or null.</summary>
    public static Image GumpImage(ushort id)
    {
        if (_images.TryGetValue(id, out Image cached))
        {
            return cached;
        }

        if (Client.Game?.UO?.Gumps == null)
        {
            return null;
        }

        ref readonly var info = ref Client.Game.UO.Gumps.GetGump(id);

        if (info.Texture == null)
        {
            return null;
        }

        Image atlas = info.Texture.GetImage();

        if (atlas == null)
        {
            return null;
        }

        if (atlas.GetFormat() != Image.Format.Rgba8)
        {
            atlas.Convert(Image.Format.Rgba8);
        }

        Image image = atlas.GetRegion(new Rect2I(info.UV.X, info.UV.Y, info.UV.Width, info.UV.Height));

        // A read before the atlas upload has landed comes back all
        // transparent: not kept, so the next call reads again (GUOWeb's fix
        // on its branch, the same shape).
        if (image.IsInvisible())
        {
            return null;
        }

        _images[id] = image;

        return image;
    }

    /// <summary>A gump as a texture of its own, or null.</summary>
    public static Texture2D GumpTexture(ushort id)
    {
        if (_textures.TryGetValue(id, out Texture2D cached))
        {
            return cached;
        }

        Image image = GumpImage(id);

        if (image == null)
        {
            return null;
        }

        Texture2D texture = ImageTexture.CreateFromImage(image);
        _textures[id] = texture;

        return texture;
    }

    // --- style boxes --------------------------------------------------------------

    /// <summary>
    /// A UO resizepic (nine gumps from <paramref name="first"/>: corners,
    /// edges, centre) as one style box whose edges and centre tile, as the
    /// client draws a resizepic.
    /// </summary>
    public static StyleBox Frame(ushort first, int contentMargin = -1)
    {
        var pieces = new Image[9];

        for (int i = 0; i < 9; i++)
        {
            pieces[i] = GumpImage((ushort)(first + i));

            if (pieces[i] == null)
            {
                return FlatFrame(first);
            }
        }

        int l = pieces[0].GetWidth(), t = pieces[0].GetHeight();
        int r = pieces[2].GetWidth(), b = pieces[6].GetHeight();
        int cw = pieces[4].GetWidth(), ch = pieces[4].GetHeight();
        Image composite = Image.CreateEmpty(l + cw + r, t + ch + b, false, Image.Format.Rgba8);

        void Put(Image piece, int x, int y, int w, int h)
        {
            // Tile the piece over the rectangle, as the client does.
            for (int yy = 0; yy < h; yy += piece.GetHeight())
            {
                for (int xx = 0; xx < w; xx += piece.GetWidth())
                {
                    int pw = Math.Min(piece.GetWidth(), w - xx), ph = Math.Min(piece.GetHeight(), h - yy);
                    composite.BlitRect(piece, new Rect2I(0, 0, pw, ph), new Vector2I(x + xx, y + yy));
                }
            }
        }

        Put(pieces[0], 0, 0, l, t);
        Put(pieces[1], l, 0, cw, t);
        Put(pieces[2], l + cw, 0, r, t);
        Put(pieces[3], 0, t, l, ch);
        Put(pieces[4], l, t, cw, ch);
        Put(pieces[5], l + cw, t, r, ch);
        Put(pieces[6], 0, t + ch, l, b);
        Put(pieces[7], l, t + ch, cw, b);
        Put(pieces[8], l + cw, t + ch, r, b);

        int m = contentMargin < 0 ? Math.Max(l, t) + 4 : contentMargin;

        return new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(composite),
            TextureMarginLeft = l, TextureMarginTop = t, TextureMarginRight = r, TextureMarginBottom = b,
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
            AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
            ContentMarginLeft = m, ContentMarginRight = m, ContentMarginTop = m, ContentMarginBottom = m,
        };
    }

    private static StyleBox FlatFrame(ushort first) => new StyleBoxFlat
    {
        BgColor = first == FieldFrame ? new Color("e8d8b0") : first == DarkFrame ? new Color("141a24") : new Color("8a857c"),
        BorderColor = new Color("2a2620"),
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        ContentMarginLeft = 12, ContentMarginTop = 12, ContentMarginRight = 12, ContentMarginBottom = 12,
    };

    /// <summary>
    /// A button: the marble plate, its ends kept and its middle tiled, at its
    /// own height. Pressed and hover are the plate darkened (the client's
    /// buttons swap to a darker gump).
    /// </summary>
    public static StyleBox Plate(float shade = 1f)
    {
        Image plate = GumpImage(ButtonPlate);

        if (plate == null)
        {
            return new StyleBoxFlat
            {
                BgColor = new Color(0.62f, 0.58f, 0.52f) * shade, BorderColor = new Color("c8a45c"),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 2, ContentMarginBottom = 3,
            };
        }

        return new StyleBoxTexture
        {
            Texture = GumpTexture(ButtonPlate),
            TextureMarginLeft = 12, TextureMarginRight = 12, TextureMarginTop = 0, TextureMarginBottom = 0,
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
            ModulateColor = new Color(shade, shade, shade),
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 2, ContentMarginBottom = 3,
        };
    }

    /// <summary>A slider's bar: its two end caps and its middle tiled.</summary>
    private static StyleBox SliderBar()
    {
        Image left = GumpImage(SliderLeft), mid = GumpImage(SliderMid), right = GumpImage(SliderRight);

        if (left == null || mid == null || right == null)
        {
            return new StyleBoxFlat { BgColor = new Color("2c2620"), ContentMarginTop = 3, ContentMarginBottom = 3 };
        }

        Image bar = Image.CreateEmpty(left.GetWidth() + mid.GetWidth() + right.GetWidth(), mid.GetHeight(), false, Image.Format.Rgba8);
        bar.BlitRect(left, new Rect2I(0, 0, left.GetWidth(), left.GetHeight()), Vector2I.Zero);
        bar.BlitRect(mid, new Rect2I(0, 0, mid.GetWidth(), mid.GetHeight()), new Vector2I(left.GetWidth(), 0));
        bar.BlitRect(right, new Rect2I(0, 0, right.GetWidth(), right.GetHeight()), new Vector2I(left.GetWidth() + mid.GetWidth(), 0));

        return new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(bar),
            TextureMarginLeft = left.GetWidth(), TextureMarginRight = right.GetWidth(),
            AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
            ContentMarginTop = mid.GetHeight() / 2, ContentMarginBottom = mid.GetHeight() / 2,
        };
    }

    // --- the theme ---------------------------------------------------------------

    private static Theme _theme;

    /// <summary>
    /// Whether the client's gumps and fonts are loaded, so the theme is UO art
    /// and not its flat fallback. Build a card once this is true.
    /// </summary>
    public static bool Ready => Client.Game?.UO?.FileManager?.Fonts != null && GumpImage(StoneFrame) != null;

    /// <summary>
    /// The shared theme, in art pixels. Scale the control tree that uses it by
    /// <see cref="PixelScale"/> and set its texture filter to nearest. Kept
    /// only once it is built from the art (<see cref="Ready"/>); before that a
    /// flat stand-in is built each time.
    /// </summary>
    public static Theme Theme => _theme ?? (Ready ? _theme = Build() : Build());

    /// <summary>Forget the built theme and font (the client data changed, or it was not loaded yet).</summary>
    public static void Reset()
    {
        _theme = null;
        _font = null;
        _images.Clear();
        _textures.Clear();
    }

    private static Theme Build()
    {
        var theme = new Theme { DefaultFont = Font, DefaultFontSize = FontSize };

        theme.SetStylebox("panel", "PanelContainer", Frame(StoneFrame));
        theme.SetStylebox("panel", "Panel", Frame(StoneFrame));

        foreach (string control in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetStylebox("normal", control, Plate());
            theme.SetStylebox("hover", control, Plate(0.88f));
            theme.SetStylebox("pressed", control, Plate(0.70f));
            theme.SetStylebox("hover_pressed", control, Plate(0.70f));
            theme.SetStylebox("disabled", control, Plate(0.55f));
            theme.SetStylebox("focus", control, new StyleBoxEmpty());
            theme.SetColor("font_color", control, Ink);
            theme.SetColor("font_hover_color", control, Ink);
            theme.SetColor("font_pressed_color", control, Heading);
            theme.SetColor("font_hover_pressed_color", control, Heading);
            theme.SetColor("font_focus_color", control, Ink);
            theme.SetColor("font_disabled_color", control, new Color(Ink, 0.5f));
            theme.SetConstant("h_separation", control, 4);
        }

        // Check boxes and switches: UO's own box (0x00D2 / 0x00D3), no plate.
        Texture2D off = GumpTexture(CheckOff), on = GumpTexture(CheckOn);

        foreach (string control in new[] { "CheckBox", "CheckButton" })
        {
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            {
                theme.SetStylebox(state, control, new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2 });
            }

            if (off != null && on != null)
            {
                theme.SetIcon("unchecked", control, off);
                theme.SetIcon("checked", control, on);
                theme.SetIcon("unchecked_disabled", control, off);
                theme.SetIcon("checked_disabled", control, on);
            }

            theme.SetColor("font_color", control, Ink);
            theme.SetColor("font_hover_color", control, Ink);
            theme.SetColor("font_pressed_color", control, Ink);
            theme.SetColor("font_hover_pressed_color", control, Ink);
            theme.SetColor("font_focus_color", control, Ink);
            theme.SetConstant("h_separation", control, 6);
        }

        theme.SetColor("font_color", "Label", Ink);

        // Fields: the parchment text entry.
        StyleBox field = Frame(FieldFrame, 5);
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("focus", "LineEdit", field);
        theme.SetStylebox("read_only", "LineEdit", field);
        theme.SetColor("font_color", "LineEdit", Ink);
        theme.SetColor("font_placeholder_color", "LineEdit", new Color(Ink, 0.45f));
        theme.SetColor("caret_color", "LineEdit", Ink);
        theme.SetColor("selection_color", "LineEdit", new Color(Gold, 0.5f));

        // Sliders: UO's bar and knob.
        theme.SetStylebox("slider", "HSlider", SliderBar());
        theme.SetStylebox("grabber_area", "HSlider", new StyleBoxEmpty());
        theme.SetStylebox("grabber_area_highlight", "HSlider", new StyleBoxEmpty());
        Texture2D thumb = GumpTexture(SliderThumb);

        if (thumb != null)
        {
            theme.SetIcon("grabber", "HSlider", thumb);
            theme.SetIcon("grabber_highlight", "HSlider", thumb);
        }

        // Scrollbars: UO's track and thumb, no arrows (a finger drags the list).
        Texture2D track = GumpTexture(ScrollTrack), knob = GumpTexture(ScrollThumb);
        theme.SetStylebox("scroll", "VScrollBar", track == null
            ? new StyleBoxFlat { BgColor = new Color("3a342c") }
            : new StyleBoxTexture { Texture = track, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile, ContentMarginLeft = 7, ContentMarginRight = 7 });
        theme.SetStylebox("grabber", "VScrollBar", knob == null
            ? new StyleBoxFlat { BgColor = Heading }
            : new StyleBoxTexture { Texture = knob, TextureMarginTop = 6, TextureMarginBottom = 6, ContentMarginLeft = 6, ContentMarginRight = 6 });
        theme.SetStylebox("grabber_highlight", "VScrollBar", theme.GetStylebox("grabber", "VScrollBar"));
        theme.SetStylebox("grabber_pressed", "VScrollBar", theme.GetStylebox("grabber", "VScrollBar"));

        // A rule: two art pixels of dark stone.
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color("5c554a"), Thickness = 1 });
        theme.SetConstant("separation", "HSeparator", 6);

        return theme;
    }

    /// <summary>A label in the theme's font at <paramref name="scale"/> times its size, in a colour.</summary>
    public static Label Label(string text, Color color, int scale = 1)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeColorOverride("font_color", color);

        if (scale != 1)
        {
            l.AddThemeFontSizeOverride("font_size", FontSize * scale);
        }

        return l;
    }

    /// <summary>A button in the theme, at the plate's height (a finger's, once scaled).</summary>
    public static Button Button(string text, float minWidth = 0f)
    {
        // Never taller than the plate: its rounded ends do not stretch.
        return new Button
        {
            Text = text, CustomMinimumSize = new Vector2(minWidth, ButtonHeight), ClipText = false,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
    }
}
