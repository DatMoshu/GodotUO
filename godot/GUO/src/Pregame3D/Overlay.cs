// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using Godot;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// The 2D layer over the diorama: the hint band, status and message cards,
/// the on-screen keyboard, the creation lists. In the UO style
/// (docs/ui/uo_godot_style.md, UoTheme), at a whole-number scale, nearest.
/// Godot never delivers events to it (GameController marks every event
/// handled, ADR-0006): the diorama hit-tests it itself.
/// </summary>
internal static class Overlay
{
    public const ushort Stone = 0x13BE;
    public const ushort Parchment = 0x0BB8;
    public const ushort Dark = 0x2436;

    public static readonly Color Band = new(0f, 0f, 0f, 0.55f);

    public static PanelContainer Card(ushort frame = Parchment)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", UoTheme.Frame(frame));
        return p;
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
internal sealed class OverlayItem : IFocusable
{
    private static readonly StyleBoxFlat Lit = new() { BgColor = new Color(0.878f, 0.69f, 0.314f, 0.42f), ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 0, ContentMarginBottom = 0 };
    private static readonly StyleBoxFlat Unlit = new() { BgColor = new Color(0f, 0f, 0f, 0f), ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 0, ContentMarginBottom = 0 };

    public readonly PanelContainer Control;
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
