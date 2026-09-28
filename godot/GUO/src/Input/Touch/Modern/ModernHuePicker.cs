// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// Modern Options' colour picker: the classic ColorPickerGump's palette as
/// finger-sized cells. 20 × 10 hues, five shades as plates for the classic's
/// slider, a preview of the chosen hue, then "Use this colour" or Back.
/// The header row holds Back, the preview and "Use this colour", above the
/// grid, so a phone never scrolls to confirm (the grid is taller than a
/// 1080p card at 3x). The current shade is lit: the style guide's selected
/// plate (86%, Heading caption) with a gold bar under it.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. The palette is ColorPickerBox's own
/// (CreateTexture): cell k at shade g is hue g + 2 + 5k, and a hue's shade is
/// (hue + 3) % 5, as its SelectHue finds it. A cell's colour is the hue's
/// middle entry (GetPolygoneColor, the colour text is drawn in).
/// </remarks>
internal sealed partial class ModernHuePicker : VBoxContainer
{
    public const int Columns = 20, Rows = 10, Shades = 5;
    private const int CellHeight = 22;

    private readonly Label _number;
    private readonly Button _use;
    private readonly ColorRect[] _lit = new ColorRect[Shades];
    private readonly ColorRect _preview;
    private readonly GridContainer _grid;
    private readonly Button[] _shades = new Button[Shades];
    private readonly Button[] _cells = new Button[Columns * Rows];
    private int _shade;
    private ushort _hue;
    private Action<ushort> _chosen;
    private Action _back;

    public ModernHuePicker(Color text)
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 4);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        AddChild(head);
        Button back = Tagged(UoTheme.Button("‹ Back", 60), "hue back");
        back.Pressed += () => _back?.Invoke();
        head.AddChild(back);
        // The page title above already names the setting; the row is for acting.
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var frame = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var edge = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = UoTheme.Cream };
        edge.SetBorderWidthAll(1);
        frame.AddThemeStyleboxOverride("panel", edge);
        _preview = new ColorRect { CustomMinimumSize = new Vector2(40, 16) };
        frame.AddChild(_preview);
        head.AddChild(frame);
        _number = UoTheme.Label("", text);
        _number.CustomMinimumSize = new Vector2(40, 0);
        head.AddChild(_number);
        // The primary action, captioned in Heading as the style guide's primary is.
        _use = Tagged(UoTheme.Button("Use this colour", 120), "Use this colour");
        _use.AddThemeColorOverride("font_color", UoTheme.Heading);
        _use.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _use.Pressed += () => _chosen?.Invoke(_hue);
        head.AddChild(_use);

        var shades = new HBoxContainer();
        shades.AddThemeConstantOverride("separation", 4);
        AddChild(shades);
        shades.AddChild(UoTheme.Label("Shade", text));

        for (int g = 0; g < Shades; g++)
        {
            int shade = g;
            var holder = new VBoxContainer();
            holder.AddThemeConstantOverride("separation", 1);
            Button b = Tagged(UoTheme.Button((g + 1).ToString(), 34), $"Shade {g + 1}");
            b.Pressed += () => SetShade(shade);
            holder.AddChild(b);
            // Lit: a gold bar under the current shade (gold means lit on the dark band).
            var lit = new ColorRect { Color = UoTheme.Gold, CustomMinimumSize = new Vector2(0, 2) };
            holder.AddChild(lit);
            shades.AddChild(holder);
            _shades[g] = b;
            _lit[g] = lit;
        }

        _grid = new GridContainer { Columns = Columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 1);
        _grid.AddThemeConstantOverride("v_separation", 1);
        AddChild(_grid);

        for (int k = 0; k < _cells.Length; k++)
        {
            int cell = k;
            var b = new Button { CustomMinimumSize = new Vector2(0, CellHeight), SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
            b.SetMeta("setting", $"hue cell {k}");
            b.Pressed += () => Select(HueAt(cell, _shade));
            _grid.AddChild(b);
            _cells[k] = b;
        }
    }

    private static Button Tagged(Button b, string tag)
    {
        b.SetMeta("setting", tag);
        return b;
    }

    /// <summary>ColorPickerBox.CreateTexture's hue for cell <paramref name="k"/> at shade <paramref name="g"/>.</summary>
    public static ushort HueAt(int k, int g) => (ushort)(g + 2 + 5 * k);

    /// <summary>The colour a hue draws text in: its middle entry, as a swatch.</summary>
    public static Color ColourOf(ushort hue)
    {
        var hues = Client.Game?.UO?.FileManager?.Hues;

        if (hue == 0 || hues == null)
        {
            return new Color(0.75f, 0.75f, 0.75f);
        }

        uint v = hues.GetPolygoneColor(16, hue); // 0x00BBGGRR (HuesHelper.Color16To32)
        return Color.Color8((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
    }

    /// <summary>Opens on <paramref name="current"/>, at its shade.</summary>
    public void Open(string title, ushort current, Action<ushort> chosen, Action back)
    {
        _chosen = chosen;
        _back = back;
        _shade = current >= 2 ? (current + 3) % Shades : 0;
        SetShade(_shade);
        Select(current);
    }

    private void SetShade(int g)
    {
        _shade = g;

        for (int i = 0; i < Shades; i++)
        {
            bool on = i == g;
            _shades[i].AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            _shades[i].AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
            _lit[i].Color = on ? UoTheme.Gold : Colors.Transparent;
        }

        for (int k = 0; k < _cells.Length; k++)
        {
            Paint(k);
        }
    }

    private void Select(ushort hue)
    {
        _hue = hue;
        _preview.Color = ColourOf(hue);
        _number.Text = hue.ToString();

        for (int k = 0; k < _cells.Length; k++)
        {
            Paint(k);
        }
    }

    private void Paint(int k)
    {
        ushort hue = HueAt(k, _shade);
        bool selected = hue == _hue;
        var box = new StyleBoxFlat
        {
            BgColor = ColourOf(hue),
            BorderColor = selected ? Colors.White : new Color(0, 0, 0, 0.6f),
        };
        box.SetBorderWidthAll(selected ? 2 : 1);

        foreach (string state in new[] { "normal", "hover", "pressed", "focus" })
        {
            _cells[k].AddThemeStyleboxOverride(state, box);
        }
    }

    /// <summary>For the probe: the hue selected now.</summary>
    public ushort Selected => _hue;

    /// <summary>For the probe: the shade lit now (0-based).</summary>
    public int LitShade => _shade;

    /// <summary>For the probe: "Use this colour" sits above the grid, so it needs no scroll.</summary>
    public bool UseAboveGrid => _use.GetGlobalRect().End.Y <= _grid.GetGlobalRect().Position.Y;
}
