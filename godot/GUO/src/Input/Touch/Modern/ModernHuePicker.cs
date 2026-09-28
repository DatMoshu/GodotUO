// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// Modern Options' colour picker: the classic ColorPickerGump's palette as
/// finger-sized cells. 20 × 10 hues, five shades as plates for the classic's
/// slider, a preview of the chosen hue, then "Use this colour" or Back.
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

    private readonly Label _title;
    private readonly Label _number;
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
        _title = UoTheme.Label("", UoTheme.Gold);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.ClipText = true;
        head.AddChild(_title);
        _preview = new ColorRect { CustomMinimumSize = new Vector2(40, 18), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        head.AddChild(_preview);
        _number = UoTheme.Label("", text);
        _number.CustomMinimumSize = new Vector2(44, 0);
        head.AddChild(_number);

        var shades = new HBoxContainer();
        shades.AddThemeConstantOverride("separation", 4);
        AddChild(shades);
        shades.AddChild(UoTheme.Label("Shade", text));

        for (int g = 0; g < Shades; g++)
        {
            int shade = g;
            Button b = Tagged(UoTheme.Button((g + 1).ToString(), 34), $"Shade {g + 1}");
            b.Pressed += () => SetShade(shade);
            shades.AddChild(b);
            _shades[g] = b;
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

        Button use = Tagged(UoTheme.Button("Use this colour", 120), "Use this colour");
        use.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        use.Pressed += () => _chosen?.Invoke(_hue);
        AddChild(use);
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
        _title.Text = title;
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
            _shades[i].AddThemeStyleboxOverride("normal", UoTheme.Plate(i == g ? UoTheme.SelectedShade : 1f));
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
}
