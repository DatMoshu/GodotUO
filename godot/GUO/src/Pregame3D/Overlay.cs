// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using Godot;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// The 2D layer over the painting: the hint band, status and message cards,
/// the on-screen keyboard, the creation lists. In the UO style
/// (docs/ui/uo_godot_style.md, UoTheme), at a whole-number scale, nearest.
/// Godot never delivers events to it (GameController marks every event
/// handled, ADR-0006): the pregame hit-tests it itself.
/// </summary>
internal static class Overlay
{
    public const ushort Stone = 0x13BE;
    public const ushort Parchment = 0x0BB8;
    public const ushort Dark = 0x2436;

    public static readonly Color Band = new(0f, 0f, 0f, 0.55f);

    private static readonly System.Collections.Generic.Dictionary<(ushort, int, int), StyleBox> _frames = new();

    /// <summary>
    /// A client ResizePic as a style box (UoTheme.Frame), cached; <paramref name="tint"/>
    /// 1 = lit for focus (the art warmed toward gold), 2 = active (toward red).
    /// </summary>
    public static StyleBox Frame(ushort first, int margin = -1, int tint = 0)
    {
        if (_frames.TryGetValue((first, margin, tint), out StyleBox cached))
        {
            return cached;
        }

        StyleBox box = Compose(first, margin) ?? UoTheme.Frame(first, margin);

        if (tint != 0 && box is StyleBoxTexture t)
        {
            t = (StyleBoxTexture) t.Duplicate();
            t.ModulateColor = tint == 1 ? new Color(1f, 0.86f, 0.52f) : new Color(1f, 0.55f, 0.48f);
            box = t;
        }

        // Only kept once it is the art. Before the gumps load Frame is a flat
        // stand-in: marked, so the screen swaps it for the art when it arrives
        // (ReplaceStandIns) and the probe can tell none is left.
        if (box is StyleBoxTexture)
        {
            _frames[(first, margin, tint)] = box;
        }
        else
        {
            box.SetMeta(StandInMeta, new Vector3I(first, margin, tint));
            StandInsHandedOut = true;
        }

        return box;
    }

    /// <summary>
    /// A ResizePic from the pregame's own gump images (PregameAssets), as
    /// UoTheme.Frame composes it: corners, tiled edges, a tiled centre. Null
    /// while a piece is missing.
    /// </summary>
    private static StyleBox Compose(ushort first, int contentMargin)
    {
        var pieces = new Image[9];

        for (int i = 0; i < 9; i++)
        {
            pieces[i] = PregameAssets.Gump((ushort) (first + i));

            if (pieces[i] == null)
            {
                return null;
            }
        }

        int l = pieces[0].GetWidth(), t = pieces[0].GetHeight();
        int r = pieces[2].GetWidth(), b = pieces[6].GetHeight();
        int cw = pieces[4].GetWidth(), ch = pieces[4].GetHeight();
        Image composite = Image.CreateEmpty(l + cw + r, t + ch + b, false, Image.Format.Rgba8);

        void Put(Image piece, int x, int y, int w, int h)
        {
            for (int yy = 0; yy < h; yy += piece.GetHeight())
            {
                for (int xx = 0; xx < w; xx += piece.GetWidth())
                {
                    composite.BlitRect(piece, new Rect2I(0, 0, Math.Min(piece.GetWidth(), w - xx), Math.Min(piece.GetHeight(), h - yy)), new Vector2I(x + xx, y + yy));
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

    public const string StandInMeta = "pregame_frame_stand_in";

    /// <summary>Whether a flat stand-in has been handed out since the last sweep.</summary>
    public static bool StandInsHandedOut { get; set; }

    /// <summary>Every ResizePic the pregame draws.</summary>
    public static readonly ushort[] FrameIds = { Parchment, Stone, 0x0A28, 0x0DAC };

    /// <summary>Asks for each frame's nine gumps; true once all of them are in the atlas.</summary>
    public static bool FramesReady()
    {
        bool ready = true;

        foreach (ushort first in FrameIds)
        {
            for (int i = 0; i < 9; i++)
            {
                ready &= PregameAssets.Gump((ushort) (first + i)) != null;
            }
        }

        return ready;
    }

    /// <summary>Swaps any stand-in under <paramref name="root"/> for the art, now that it is here; how many are left.</summary>
    public static int ReplaceStandIns(Node root)
    {
        int left = 0;

        void Walk(Node n)
        {
            if (n is Control c && c.HasThemeStyleboxOverride("panel") && c.GetThemeStylebox("panel") is StyleBox sb && sb.HasMeta(StandInMeta))
            {
                Vector3I key = sb.GetMeta(StandInMeta).AsVector3I();
                StyleBox art = Frame((ushort) key.X, key.Y, key.Z);

                if (art is StyleBoxTexture)
                {
                    c.AddThemeStyleboxOverride("panel", art);
                }
                else
                {
                    left++;
                }
            }

            foreach (Node child in n.GetChildren())
            {
                Walk(child);
            }
        }

        Walk(root);
        return left;
    }

    /// <summary>For the probe: the stand-ins still showing under <paramref name="root"/>.</summary>
    public static int CountStandIns(Node root)
    {
        int count = 0;

        void Walk(Node n)
        {
            if (n is Control c && c.IsVisibleInTree() && c.HasThemeStyleboxOverride("panel") && c.GetThemeStylebox("panel") is StyleBox sb && sb.HasMeta(StandInMeta))
            {
                count++;
            }

            foreach (Node child in n.GetChildren())
            {
                Walk(child);
            }
        }

        Walk(root);
        return count;
    }

    public static PanelContainer Card(ushort frame = Parchment)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", Frame(frame));
        return p;
    }

    /// <summary>A row on parchment that lights when focused: its frame warms, its text turns red.</summary>
    public static UiFocus FrameRow(Control content, Label text, string tag, int margin = 4)
    {
        var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(content);
        var f = new UiFocus(row) { Tag = tag };
        f.Shown = on =>
        {
            row.AddThemeStyleboxOverride("panel", Frame(Parchment, margin, on ? 1 : 0));

            if (text != null)
            {
                text.AddThemeColorOverride("font_color", on ? UoTheme.Danger : UoTheme.Ink);
            }
        };
        f.Shown(false);
        return f;
    }

    public static PanelContainer BandPanel(int margin = 4)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Band,
            ContentMarginLeft = margin * 2, ContentMarginRight = margin * 2, ContentMarginTop = margin, ContentMarginBottom = margin,
        });
        return p;
    }

    public static Label Text(string text, Color color, int scale = 1, bool wrap = false)
    {
        Label l = UoTheme.Label(text, color, scale);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (wrap)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }

        return l;
    }

    public static VBoxContainer Column(int separation = 2)
    {
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    public static HBoxContainer Row(int separation = 4)
    {
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", separation);
        return h;
    }

    /// <summary>Whether a window-space point is over <paramref name="c"/> (visible).</summary>
    public static bool Hit(Control c, Vector2 window) => c != null && c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(window);
}

/// <summary>
/// A focusable overlay row: a caption, an optional value with ◄ ► when it
/// cycles, a gold selection when focused (the style's field selection).
/// </summary>
internal sealed class OverlayItem : IOverlayFocusable
{
    private static readonly StyleBoxFlat Lit = new() { BgColor = new Color(0.878f, 0.69f, 0.314f, 0.42f), ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 0, ContentMarginBottom = 0 };
    private static readonly StyleBoxFlat Unlit = new() { BgColor = new Color(0f, 0f, 0f, 0f), ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 0, ContentMarginBottom = 0 };

    public PanelContainer Control { get; }

    Control IOverlayFocusable.Control => Control;
    private readonly Label _caption;
    private readonly Label _value;
    private readonly Color _ink;

    public OverlayItem(string caption, Action activated = null, Color? ink = null)
    {
        _ink = ink ?? UoTheme.Ink;
        Activated = activated;
        Control = new PanelContainer { MouseFilter = Godot.Control.MouseFilterEnum.Ignore };
        Control.AddThemeStyleboxOverride("panel", Unlit);
        HBoxContainer row = Overlay.Row(8);
        _caption = Overlay.Text(caption, _ink);
        _caption.SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill;
        _value = Overlay.Text("", UoTheme.Heading);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(_caption);
        row.AddChild(_value);
        Control.AddChild(row);
    }

    public string Caption
    {
        get => _caption.Text;
        set => _caption.Text = value;
    }

    public string Value
    {
        set => _value.Text = Cycle != null && !string.IsNullOrEmpty(value) ? $"<  {value}  >" : value ?? "";
    }

    public bool Enabled { get; set; } = true;

    public bool CanFocus => Enabled && Control.IsVisibleInTree();

    public IFocusable Up { get; set; }
    public IFocusable Down { get; set; }
    public IFocusable Left { get; set; }
    public IFocusable Right { get; set; }
    public Action<int> Cycle { get; set; }
    public Action Activated { get; set; }

    public void SetFocused(bool focused)
    {
        Control.AddThemeStyleboxOverride("panel", focused ? Lit : Unlit);
        _caption.AddThemeColorOverride("font_color", focused ? UoTheme.Heading : _ink);
    }

    public void Press()
    {
        if (Enabled)
        {
            Activated?.Invoke();
        }
    }
}

/// <summary>A focusable thing of the overlay: the pointer hit-tests its control.</summary>
internal interface IOverlayFocusable : IFocusable
{
    Control Control { get; }
}

/// <summary>Any overlay control made focusable: how it shows focus and what a press does are the caller's.</summary>
internal sealed class UiFocus : IOverlayFocusable
{
    public UiFocus(Control control, Action<bool> shown = null, Action pressed = null)
    {
        Control = control;
        Shown = shown;
        Pressed = pressed;
    }

    public Control Control { get; }
    public Action<bool> Shown { get; set; }
    public Action Pressed { get; set; }
    public object Tag { get; set; }
    public bool Enabled { get; set; } = true;
    public bool CanFocus => Enabled && Control.IsVisibleInTree();
    public IFocusable Up { get; set; }
    public IFocusable Down { get; set; }
    public IFocusable Left { get; set; }
    public IFocusable Right { get; set; }
    public Action<int> Cycle { get; set; }

    public void SetFocused(bool focused) => Shown?.Invoke(focused);

    public void Press()
    {
        if (Enabled)
        {
            Pressed?.Invoke();
        }
    }
}
