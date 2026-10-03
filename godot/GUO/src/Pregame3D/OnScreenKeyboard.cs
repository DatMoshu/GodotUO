// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: a Linux handheld (the Deck in Game Mode) has no
// OS keyboard GUO can call up.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// The pregame's own on-screen keyboard (docs/ui/pregame_3d.md, Input): a
/// grid of letters, digits and a few symbols with Shift, Space, Del and Done.
/// D-pad moves, A types, X deletes, Y shifts, Start is Done, B cancels (the
/// field goes back to what it held). A physical keyboard types into the same
/// field while it is open. A password shows as dots.
/// </summary>
internal sealed class OnScreenKeyboard
{
    private static readonly string[] Lower = { "1234567890", "qwertyuiop", "asdfghjkl-", "zxcvbnm.,@" };
    private static readonly string[] Upper = { "!#$%&*()_+", "QWERTYUIOP", "ASDFGHJKL_", "ZXCVBNM:;'" };

    /// <summary>The bottom row's keys and the columns each spans (of 10).</summary>
    private static readonly (string label, int from, int to)[] Specials = { ("Shift", 0, 1), ("Space", 2, 5), ("Del", 6, 7), ("Done", 8, 9) };

    private const int Columns = 10;
    private const int SpecialRow = 4;

    public readonly PanelContainer Root;
    private readonly Label _caption;
    private readonly Label _value;
    private readonly PanelContainer[,] _cells = new PanelContainer[4, Columns];
    private readonly Label[,] _cellLabels = new Label[4, Columns];
    private readonly PanelContainer[] _specialCells = new PanelContainer[Specials.Length];
    private readonly GridContainer _grid;
    private readonly HBoxContainer _specials;
    private readonly Label _help;

    /// <summary>The device's keyboard is up (<see cref="NativeKeyboard"/>): only the field shows here.</summary>
    public bool Native { get; private set; }

    private static readonly StyleBoxFlat KeyLit = new() { BgColor = new Color(0.878f, 0.69f, 0.314f, 0.85f), ContentMarginLeft = 2, ContentMarginRight = 2 };
    private static readonly StyleBoxFlat KeyUnlit = new() { BgColor = new Color(0.86f, 0.8f, 0.66f, 1f), ContentMarginLeft = 2, ContentMarginRight = 2 };

    private string _text = "";
    private string _original = "";
    private bool _secret;
    private int _maxLength = 30;
    private bool _shift;
    private int _row = 1, _col;
    private Action<string> _done;
    private Action<string> _changed;
    private Action _cancel;

    public bool IsOpen { get; private set; }

    public string TextValue => _text;

    public OnScreenKeyboard()
    {
        Root = Overlay.Card(Overlay.Stone);
        Root.Visible = false;
        VBoxContainer col = Overlay.Column(3);
        Root.AddChild(col);

        _caption = Overlay.Text("", UoTheme.Heading);
        col.AddChild(_caption);

        PanelContainer field = _valueCard = Overlay.Card(Overlay.Parchment);
        _value = Overlay.Text("", UoTheme.Ink);
        _value.CustomMinimumSize = new Vector2(260, 0);
        field.AddChild(_value);
        col.AddChild(field);

        var grid = _grid = new GridContainer { Columns = Columns, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 2);
        grid.AddThemeConstantOverride("v_separation", 2);
        col.AddChild(grid);

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                var cell = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(24, 20) };
                cell.AddThemeStyleboxOverride("panel", KeyUnlit);
                Label l = Overlay.Text("", UoTheme.Ink);
                l.HorizontalAlignment = HorizontalAlignment.Center;
                cell.AddChild(l);
                grid.AddChild(cell);
                _cells[r, c] = cell;
                _cellLabels[r, c] = l;
            }
        }

        HBoxContainer specials = _specials = Overlay.Row(2);
        col.AddChild(specials);

        for (int i = 0; i < Specials.Length; i++)
        {
            int span = Specials[i].to - Specials[i].from + 1;
            var cell = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(24 * span + 2 * (span - 1), 20) };
            cell.AddThemeStyleboxOverride("panel", KeyUnlit);
            Label l = Overlay.Text(Specials[i].label, UoTheme.Ink);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            cell.AddChild(l);
            specials.AddChild(cell);
            _specialCells[i] = cell;
        }

        _help = Overlay.Text("", UoTheme.Muted);
        col.AddChild(_help);
        Relabel();
    }

    /// <summary>The field being typed into in place, when it is on screen (null: the card's own line).</summary>
    public Control EditedField { get; private set; }

    /// <summary>The card itself, when it shows (the grid, or a field that is not on screen).</summary>
    public bool CardShown => IsOpen && Root.Visible;

    private Label _inPlace;
    private PanelContainer _valueCard;
    private PanelContainer _inlineHint;
    private Label _inlineHintText;
    private Control _layer;

    /// <summary>Where the in-place hint lives: the overlay, topmost.</summary>
    public void Attach(Control overlay)
    {
        _layer = overlay;
        // On a dark band: readable over the painting's own lettering.
        _inlineHint = Overlay.BandPanel(3);
        _inlineHintText = Overlay.Text("", UoTheme.Cream);
        _inlineHint.AddChild(_inlineHintText);
        _inlineHint.Visible = false;
        overlay.AddChild(_inlineHint);
    }

    /// <summary>
    /// Opens the keyboard on a field. With <paramref name="field"/> (the
    /// field's control, on screen) and <paramref name="fieldText"/> (its text
    /// label) the typing shows in the field itself, with a caret: on a
    /// device keyboard there is no card at all, only a hint just under the
    /// field; on the grid the card stands clear of the field, on top.
    /// </summary>
    public void Open(string caption, string text, bool secret, int maxLength, Action<string> done, Action cancel = null, Action<string> changed = null, Control field = null, Label fieldText = null)
    {
        EditedField = field != null && field.IsVisibleInTree() ? field : null;
        _inPlace = EditedField != null ? fieldText : null;
        _caption.Text = caption;
        _text = text ?? "";
        _original = _text;
        _secret = secret;
        _maxLength = maxLength;
        _done = done;
        _cancel = cancel;
        _changed = changed;
        _shift = false;
        _row = 1;
        _col = 0;
        IsOpen = true;
        Root.Visible = true;

        // The device's own keyboard when it has one; this grid otherwise.
        Native = NativeKeyboard.Available;
        _grid.Visible = _specials.Visible = !Native;
        _help.Text = Native
            ? (NativeKeyboard.Kind == "steam"
                // Steam's keyboard owns the pad while it is up: Start and B never
                // reach us, but its Enter key does (Key: Enter commits and closes it).
                ? "Steam keyboard  -  press Enter when done"
                : "Type on the keyboard  -  Enter when done")
            : "A type   X delete   Y shift   Start done   B cancel";
        GD.Print($"[GUO] pregame3d: text field \"{caption}\" via the {(Native ? NativeKeyboard.Kind + " keyboard" : "pregame's own grid keyboard")}");

        // A device keyboard with the field on screen: no card, the field itself
        // takes the text. Otherwise the card, topmost, clear of the field:
        // at the top when the field is low (and always for Steam's keyboard,
        // which covers the lower half), else at the bottom.
        // Typing in place: the card is only the keys (the field shows the text).
        _caption.Visible = _valueCard.Visible = _inPlace == null;

        bool fieldLow = EditedField != null && EditedField.GetGlobalRect().GetCenter().Y > _layer?.GetViewportRect().Size.Y * 0.5f;
        bool top = Native || fieldLow;
        Root.Visible = !(Native && _inPlace != null);
        Root.ResetSize();
        Root.SetAnchorsAndOffsetsPreset(top ? Control.LayoutPreset.CenterTop : Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, top ? 8 : 28);
        Root.GrowHorizontal = Control.GrowDirection.Both;
        Root.GrowVertical = top ? Control.GrowDirection.End : Control.GrowDirection.Begin;
        Root.GetParent()?.MoveChild(Root, -1);

        if (_inlineHint != null)
        {
            _inlineHint.Visible = Native && _inPlace != null;
            _inlineHintText.Text = _help.Text;

            if (_inlineHint.Visible)
            {
                _layer.MoveChild(_inlineHint, -1);
                PlaceHint();
            }
        }

        if (Native)
        {
            NativeKeyboard.Show(_text);
        }

        Relabel();
        Show();
    }

    /// <summary>The in-place hint just under the field (again each frame: the field can grow as it lays out).</summary>
    public void PlaceHint()
    {
        if (_inlineHint == null || !_inlineHint.Visible || EditedField == null || !GodotObject.IsInstanceValid(EditedField))
        {
            return;
        }

        Rect2 r = EditedField.GetGlobalRect();
        Transform2D toLayer = _layer.GetGlobalTransform().AffineInverse();
        Vector2 at = toLayer * new Vector2(r.Position.X, r.End.Y + 4);
        _inlineHint.ResetSize();
        _inlineHint.Position = new Vector2(Math.Clamp(at.X, 4, Math.Max(4, _layer.Size.X - _inlineHint.Size.X - 4)), Math.Min(at.Y, _layer.Size.Y - _inlineHint.Size.Y - 4));
    }

    public void Close()
    {
        if (IsOpen && Native)
        {
            NativeKeyboard.Hide();
        }

        IsOpen = false;
        Native = false;
        Root.Visible = false;
        EditedField = null;
        _inPlace = null;

        if (_inlineHint != null)
        {
            _inlineHint.Visible = false;
        }
    }

    public bool Command(PadCmd cmd)
    {
        if (!IsOpen)
        {
            return false;
        }

        // The device's keyboard types real keys (Key); the pad only finishes, cancels, deletes.
        if (Native && cmd is not (PadCmd.Start or PadCmd.B or PadCmd.X))
        {
            return true;
        }

        switch (cmd)
        {
            case PadCmd.Up or PadCmd.Down or PadCmd.Left or PadCmd.Right:
                (_row, _col) = Step(_row, _col, cmd);
                Show();
                break;

            case PadCmd.A:
                Activate();
                break;

            case PadCmd.X:
                Backspace();
                break;

            case PadCmd.Y:
                _shift = !_shift;
                Relabel();
                Show();
                break;

            case PadCmd.Start:
                Finish();
                break;

            case PadCmd.B:
                _text = _original;
                _changed?.Invoke(_text);
                Close();
                _cancel?.Invoke();
                break;
        }

        return true;
    }

    /// <summary>A physical key while open: typing, Backspace, Enter (done), Escape (cancel), arrows (move).</summary>
    public bool Key(InputEventKey k)
    {
        if (!IsOpen)
        {
            return false;
        }

        if (!k.Pressed)
        {
            return true;
        }

        switch (k.Keycode)
        {
            case Godot.Key.Backspace: Backspace(); return true;
            case Godot.Key.Enter or Godot.Key.KpEnter: Finish(); return true;
            case Godot.Key.Escape: return Command(PadCmd.B);
            case Godot.Key.Up: return Command(PadCmd.Up);
            case Godot.Key.Down: return Command(PadCmd.Down);
            case Godot.Key.Left: return Command(PadCmd.Left);
            case Godot.Key.Right: return Command(PadCmd.Right);
        }

        if (k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed)
        {
            Type(char.ConvertFromUtf32((int) k.Unicode));
        }

        return true;
    }

    /// <summary>A click on a key: it is typed (or the special pressed).</summary>
    public bool Click(Vector2 window)
    {
        if (!IsOpen)
        {
            return false;
        }

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                if (Overlay.Hit(_cells[r, c], window))
                {
                    _row = r;
                    _col = c;
                    Activate();
                    return true;
                }
            }
        }

        for (int i = 0; i < Specials.Length; i++)
        {
            if (Overlay.Hit(_specialCells[i], window))
            {
                _row = SpecialRow;
                _col = Specials[i].from;
                Activate();
                return true;
            }
        }

        return Overlay.Hit(Root, window);
    }

    /// <summary>
    /// For the probe: the pad presses that type <paramref name="c"/> from
    /// where the cursor is now (moves, a Shift if needed, then A), found by a
    /// search over the same moves the D-pad makes.
    /// </summary>
    public List<PadCmd> PathTo(char c)
    {
        bool needUpper = Find(Upper, c, out int ur, out int uc) && !Find(Lower, c, out _, out _);
        bool found = needUpper || Find(Lower, c, out ur, out uc);
        var path = new List<PadCmd>();

        if (c == ' ')
        {
            ur = SpecialRow;
            uc = 2;
            found = true;
            needUpper = _shift;
        }

        if (!found)
        {
            return path;
        }

        (int r, int col) at = (_row, _col);

        if (needUpper != _shift)
        {
            path.AddRange(Route(at, (SpecialRow, 0)));
            path.Add(PadCmd.Y);
            at = (SpecialRow, 0);
        }

        path.AddRange(Route(at, (ur, uc)));
        path.Add(PadCmd.A);

        return path;
    }

    private List<PadCmd> Route((int r, int c) from, (int r, int c) to)
    {
        // Breadth-first over the grid's moves.
        var prev = new Dictionary<(int, int), ((int, int) at, PadCmd cmd)>();
        var queue = new Queue<(int, int)>();
        queue.Enqueue(from);
        prev[from] = (from, PadCmd.A);
        (int, int) goal = SpecialCell(to);

        while (queue.Count > 0)
        {
            (int r, int c) cur = queue.Dequeue();

            if (SpecialCell(cur) == goal)
            {
                var path = new List<PadCmd>();

                for ((int, int) p = cur; p != from; p = prev[p].at)
                {
                    path.Insert(0, prev[p].cmd);
                }

                return path;
            }

            foreach (PadCmd d in new[] { PadCmd.Up, PadCmd.Down, PadCmd.Left, PadCmd.Right })
            {
                (int, int) next = Step(cur.r, cur.c, d);

                if (!prev.ContainsKey(next))
                {
                    prev[next] = (cur, d);
                    queue.Enqueue(next);
                }
            }
        }

        return new List<PadCmd>();
    }

    /// <summary>A bottom-row position normalised to its key's first column.</summary>
    private static (int, int) SpecialCell((int r, int c) p) => p.r == SpecialRow ? (SpecialRow, Specials[SpecialAt(p.c)].from) : p;

    private static int SpecialAt(int col)
    {
        for (int i = 0; i < Specials.Length; i++)
        {
            if (col >= Specials[i].from && col <= Specials[i].to)
            {
                return i;
            }
        }

        return Specials.Length - 1;
    }

    private static (int, int) Step(int row, int col, PadCmd d)
    {
        switch (d)
        {
            case PadCmd.Up:
                return row > 0 ? (row - 1, row == SpecialRow ? Specials[SpecialAt(col)].from : col) : (row, col);

            case PadCmd.Down:
                return row < SpecialRow ? (row + 1, row + 1 == SpecialRow ? Specials[SpecialAt(col)].from : col) : (row, col);

            case PadCmd.Left:
                if (row == SpecialRow)
                {
                    int i = SpecialAt(col);
                    return i > 0 ? (row, Specials[i - 1].from) : (row, col);
                }

                return col > 0 ? (row, col - 1) : (row, col);

            case PadCmd.Right:
                if (row == SpecialRow)
                {
                    int i = SpecialAt(col);
                    return i + 1 < Specials.Length ? (row, Specials[i + 1].from) : (row, col);
                }

                return col + 1 < Columns ? (row, col + 1) : (row, col);
        }

        return (row, col);
    }

    private static bool Find(string[] rows, char c, out int row, out int col)
    {
        for (row = 0; row < rows.Length; row++)
        {
            col = rows[row].IndexOf(c);

            if (col >= 0)
            {
                return true;
            }
        }

        row = col = -1;
        return false;
    }

    private void Activate()
    {
        if (_row == SpecialRow)
        {
            switch (Specials[SpecialAt(_col)].label)
            {
                case "Shift":
                    _shift = !_shift;
                    Relabel();
                    break;
                case "Space":
                    Type(" ");
                    break;
                case "Del":
                    Backspace();
                    break;
                case "Done":
                    Finish();
                    return;
            }

            Show();
            return;
        }

        Type((_shift ? Upper : Lower)[_row][_col].ToString());
    }

    private void Type(string s)
    {
        if (_text.Length + s.Length > _maxLength)
        {
            return;
        }

        _text += s;
        _changed?.Invoke(_text);
        Show();
    }

    private void Backspace()
    {
        if (_text.Length > 0)
        {
            _text = _text.Substring(0, _text.Length - 1);
            _changed?.Invoke(_text);
        }

        Show();
    }

    private void Finish()
    {
        Close();
        _done?.Invoke(_text);
    }

    private void Relabel()
    {
        string[] rows = _shift ? Upper : Lower;

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cellLabels[r, c].Text = rows[r][c].ToString();
            }
        }
    }

    private void Show()
    {
        _value.Text = (_secret ? new string('*', _text.Length) : _text) + "_";

        if (_inPlace != null && GodotObject.IsInstanceValid(_inPlace))
        {
            _inPlace.Text = _value.Text;
            _inPlace.AddThemeColorOverride("font_color", UoTheme.Ink);
        }

        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c].AddThemeStyleboxOverride("panel", r == _row && c == _col ? KeyLit : KeyUnlit);
            }
        }

        int lit = _row == SpecialRow ? SpecialAt(_col) : -1;

        for (int i = 0; i < Specials.Length; i++)
        {
            bool on = i == lit || (Specials[i].label == "Shift" && _shift);
            _specialCells[i].AddThemeStyleboxOverride("panel", i == lit ? KeyLit : on ? new StyleBoxFlat { BgColor = new Color(0.878f, 0.69f, 0.314f, 0.4f) } : KeyUnlit);
        }
    }
}
