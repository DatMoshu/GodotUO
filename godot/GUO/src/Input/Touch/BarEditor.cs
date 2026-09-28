// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Input.Touch;

/// <summary>
/// The command bar's slot editor (C10): what one slot does on a tap, and its
/// two alternates in the hold popup, picked from the catalogue by group or by
/// search, with a speech action's words editable. Opened from the popup's
/// Edit. A card in the client's own look (UoTheme, docs/ui/uo_godot_style.md).
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO; touch builds only. Built like the
/// window menu: Godot controls in a SubViewport at a whole-number scale, shown
/// on the main screen by a CanvasLayer, with the pointer pushed in by
/// <see cref="HandleInput"/> (GameController marks every event handled
/// before a Godot control could see it). A drag over the list scrolls it; a
/// tap presses. Keys go to its search and words fields while it is open.
/// Saves to the profile by action name (TouchGumpBar.SetSlot).
/// </remarks>
internal sealed partial class BarEditor : Node
{
    private const float MaxWidth = 600f, MaxHeight = 340f; // art pixels
    private const string All = "All";

    private static BarEditor _instance;

    public static bool IsOpen => _instance != null && GodotObject.IsInstanceValid(_instance) && _instance._slot >= 0;

    /// <summary>The slot being edited (row 1 first), or -1.</summary>
    public static int Slot => IsOpen ? _instance._slot : -1;

    /// <summary>For the probe: the three picks as they stand (tap, hold 1, hold 2).</summary>
    public static string[] Picked => IsOpen ? (string[])_instance._picked.Clone() : null;

    private int _slot = -1;
    private readonly string[] _picked = new string[3];
    private int _which; // 0 the tap, 1 and 2 the alternates
    private string _category = BarCatalogue.Windows;
    private readonly Dictionary<string, string> _words = new();

    private SubViewport _viewport;
    private Control _root;
    private PanelContainer _card;
    private CanvasLayer _layer;
    private TextureRect _view;
    private Label _title;
    private readonly Button[] _pickers = new Button[3];
    private readonly List<Button> _categoryButtons = new();
    private LineEdit _search;
    private ScrollContainer _scroll;
    private GridContainer _grid;
    private HBoxContainer _wordsRow;
    private Label _wordsLabel;
    private LineEdit _wordsField;
    private string _wordsFor;

    private float _scale = 1f;
    private Rect2 _rect; // client pixels

    private bool _pressing;
    private bool _dragged;
    private Vector2 _pressAt;
    private Vector2 _lastAt;

    public static void Open(int slot)
    {
        if (Client.Game == null || slot < 0)
        {
            return;
        }

        if (_instance == null || !GodotObject.IsInstanceValid(_instance))
        {
            _instance = new BarEditor();
            Client.Game.AddChild(_instance);
        }

        _instance.Show(slot);
    }

    public static void Close()
    {
        if (IsOpen)
        {
            _instance.Hide();
        }
    }

    public override void _Ready()
    {
        _scale = UoTheme.PixelScale;

        _viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
        };
        AddChild(_viewport);

        _root = new Control { Scale = new Vector2(_scale, _scale), MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);

        _card = new PanelContainer { Theme = UoTheme.Theme };
        _root.AddChild(_card);
        Build();

        _layer = new CanvasLayer { Layer = 101 };
        AddChild(_layer);
        _view = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            StretchMode = TextureRect.StretchModeEnum.Keep,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Visible = false,
        };
        _layer.AddChild(_view);
    }

    // --- building -------------------------------------------------------------

    private void Build()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        _card.AddChild(col);

        // Header: which slot, and a close button.
        var header = new HBoxContainer();
        col.AddChild(header);
        _title = UoTheme.Label("Command bar", UoTheme.Heading, 2);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_title);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Hide;
        header.AddChild(close);

        // The three things a slot does: its tap and its two hold alternates.
        var pickers = new HBoxContainer();
        pickers.AddThemeConstantOverride("separation", 4);
        col.AddChild(pickers);

        for (int i = 0; i < 3; i++)
        {
            int which = i;
            Button b = UoTheme.Button("", 60);
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.Pressed += () => { _which = which; Refresh(); };
            pickers.AddChild(b);
            _pickers[i] = b;
        }

        col.AddChild(new HSeparator());

        // Groups on the left; the search and the actions on the right.
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 6);
        col.AddChild(body);

        var groups = new VBoxContainer();
        groups.AddThemeConstantOverride("separation", 2);
        body.AddChild(groups);

        foreach (string c in BarCatalogue.Categories)
        {
            string category = c;
            Button b = UoTheme.Button(ShortCategory(c), 96);
            b.Pressed += () => { _category = category; _search.Text = ""; Refresh(); };
            b.SetMeta("category", c);
            groups.AddChild(b);
            _categoryButtons.Add(b);
        }

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 4);
        body.AddChild(right);

        _search = new LineEdit { PlaceholderText = "Search all actions", ClearButtonEnabled = false, CustomMinimumSize = new Vector2(0, 18) };
        _search.TextChanged += _ => Refresh();
        right.AddChild(_search);

        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _scroll.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        right.AddChild(_scroll);

        _grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 3);
        _grid.AddThemeConstantOverride("v_separation", 3);
        _scroll.AddChild(_grid);

        // A speech action's words.
        _wordsRow = new HBoxContainer { Visible = false };
        _wordsRow.AddThemeConstantOverride("separation", 6);
        col.AddChild(_wordsRow);
        _wordsLabel = UoTheme.Label("Says:", UoTheme.Heading);
        _wordsRow.AddChild(_wordsLabel);
        _wordsField = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18) };
        _wordsField.TextChanged += t => { if (_wordsFor != null) _words[_wordsFor] = t; };
        _wordsRow.AddChild(_wordsField);

        // The footer: what holding does, and the two ways out.
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 4);
        col.AddChild(footer);
        Label hint = UoTheme.Label("Hold a bar button for its two extra actions.", UoTheme.Muted);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        footer.AddChild(hint);
        Button cancel = UoTheme.Button("Cancel", 70);
        cancel.Pressed += Hide;
        footer.AddChild(cancel);
        Button save = UoTheme.Button("Save", 70);
        save.AddThemeColorOverride("font_color", UoTheme.Heading);
        save.Pressed += Save;
        footer.AddChild(save);
    }

    private static string ShortCategory(string c) => c switch
    {
        BarCatalogue.Magic => "Magic, Skills",
        BarCatalogue.Speech => "Pets, Speech",
        _ => c,
    };

    // --- state ------------------------------------------------------------------

    private new void Show(int slot)
    {
        _slot = slot;
        _which = 0;
        _words.Clear();
        _search.Text = "";

        string[] slots = TouchGumpBar.Slots;
        (string a, string b) = TouchGumpBar.Alternates(slot);
        _picked[0] = slots[slot];
        _picked[1] = a;
        _picked[2] = b;
        _category = BarCatalogue.Get(_picked[0])?.Category ?? BarCatalogue.Windows;
        _title.Text = $"Row {slot / TouchGumpBar.PerRow + 1}, slot {slot % TouchGumpBar.PerRow + 1}";

        Refresh();
        Place();
        _view.Visible = true;
        GD.Print($"[GUO] bar editor: open for slot {slot} ({_picked[0]} | {_picked[1]} | {_picked[2]})");
    }

    private new void Hide()
    {
        if (_slot >= 0)
        {
            GD.Print("[GUO] bar editor: closed");
        }

        _slot = -1;
        _pressing = false;

        if (_view != null)
        {
            _view.Visible = false;
        }

        _viewport?.GuiReleaseFocus();
        DisplayServer.VirtualKeyboardHide();
    }

    private void Save()
    {
        if (_slot < 0)
        {
            return;
        }

        foreach (KeyValuePair<string, string> kv in _words)
        {
            BarCatalogue.SetWords(kv.Key, kv.Value);
        }

        TouchGumpBar.SetSlot(_slot, _picked[0], _picked[1], _picked[2]);
        GD.Print($"[GUO] bar editor: saved slot {_slot} = {_picked[0]} | {_picked[1]} | {_picked[2]}");
        Hide();
    }

    /// <summary>Choose an action for the picker being edited (null: none, for an alternate).</summary>
    private void Choose(string id)
    {
        if (_which == 0 && id == null)
        {
            return;
        }

        _picked[_which] = id;
        Refresh();
    }

    /// <summary>Redraw the pickers, the groups, the list and the words for the current state.</summary>
    private void Refresh()
    {
        string[] prefixes = { "Tap", "Hold 1", "Hold 2" };

        for (int i = 0; i < 3; i++)
        {
            string name = _picked[i] == null ? "none" : BarCatalogue.Get(_picked[i])?.Short ?? _picked[i];
            _pickers[i].Text = $"{prefixes[i]}: {name}";
            bool on = i == _which;
            _pickers[i].AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            _pickers[i].AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
        }

        string query = _search.Text.Trim();
        bool searching = query.Length > 0;

        foreach (Button b in _categoryButtons)
        {
            bool on = !searching && (string)b.GetMeta("category") == _category;
            b.AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            b.AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
        }

        foreach (Node n in _grid.GetChildren())
        {
            _grid.RemoveChild(n);
            n.QueueFree();
        }

        // An alternate may be empty.
        if (_which > 0)
        {
            AddChoice(null, "None");
        }

        foreach (BarAction a in BarCatalogue.All)
        {
            bool shown = searching
                ? a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Short.Contains(query, StringComparison.OrdinalIgnoreCase)
                : a.Category == _category;

            if (shown)
            {
                AddChoice(a.Id, a.Short);
            }
        }

        _scroll.ScrollVertical = 0;

        // The words, for a speech action in the picker being edited.
        BarAction picked = BarCatalogue.Get(_picked[_which]);
        _wordsFor = picked != null && picked.IsSpeech ? picked.Id : null;
        _wordsRow.Visible = _wordsFor != null;

        if (_wordsFor != null)
        {
            _wordsLabel.Text = $"{picked.Short} says:";
            _wordsField.Text = _words.TryGetValue(_wordsFor, out string w) ? w : BarCatalogue.WordsFor(_wordsFor);
        }
    }

    private void AddChoice(string id, string caption)
    {
        Button b = UoTheme.Button(caption, 110);
        b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bool on = id == _picked[_which];

        if (on)
        {
            b.AddThemeStyleboxOverride("normal", UoTheme.Plate(UoTheme.SelectedShade));
            b.AddThemeColorOverride("font_color", UoTheme.Heading);
        }

        b.SetMeta("action", id ?? "");
        b.Pressed += () => Choose(id);
        _grid.AddChild(b);
    }

    public override void _Process(double delta)
    {
        if (_slot >= 0 && !(Client.Game?.UO?.World?.InGame ?? false))
        {
            Hide();
        }
    }

    // --- placement ----------------------------------------------------------------

    private static float Dpi => Math.Max(0.01f, Client.Game?.DpiScale ?? 1f);

    /// <summary>Centred on the main screen, as large as fits up to its maximum.</summary>
    private void Place()
    {
        Compat.Rectangle main = Client.Game.ClientBounds;
        float k = _scale / Dpi; // art to client pixels
        float w = Math.Min(MaxWidth, main.Width / k - 16);
        float h = Math.Min(MaxHeight, main.Height / k - 12);

        // Never shorter than its content: the footer (Save) must be on it.
        _card.CustomMinimumSize = Vector2.Zero;
        _card.ResetSize();
        Vector2 least = _card.GetCombinedMinimumSize();
        w = Math.Max(w, least.X);
        h = Math.Max(h, least.Y);
        _card.Position = Vector2.Zero;
        _card.Size = new Vector2(w, h);
        _card.CustomMinimumSize = new Vector2(w, h);

        _rect = new Rect2((main.Width - w * k) / 2f, (main.Height - h * k) / 2f, w * k, h * k);
        var px = new Vector2I((int)Math.Ceiling(w * _scale), (int)Math.Ceiling(h * _scale));
        _viewport.Size = px;
        _view.Position = _rect.Position * Dpi;
        _view.Size = px;
    }

    // --- input ----------------------------------------------------------------------

    /// <summary>
    /// Route an event while the editor is open: a tap presses what is under
    /// it, a drag over the list scrolls it, keys go to the focused field.
    /// Everything is consumed while it is open: it is modal.
    /// </summary>
    public static bool HandleInput(InputEvent e)
    {
        if (!IsOpen)
        {
            return false;
        }

        BarEditor m = _instance;

        if (e is InputEventKey key)
        {
            m._viewport.PushInput(key, true);
            return true;
        }

        Vector2? window = e switch
        {
            InputEventScreenTouch t => t.Position,
            InputEventScreenDrag d => d.Position,
            InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left => mb.Position,
            InputEventMouseMotion mm => mm.Position,
            _ => null,
        };

        if (window == null)
        {
            return true;
        }

        TouchOverlay.Note(e);
        Vector2 client = window.Value / Dpi;
        Vector2 local = (client - m._rect.Position) * Dpi; // device pixels in the viewport
        bool pressed = e is InputEventScreenTouch { Pressed: true } || e is InputEventMouseButton { Pressed: true };
        bool released = e is InputEventScreenTouch { Pressed: false } || e is InputEventMouseButton { Pressed: false };

        if (pressed)
        {
            m._pressing = m._rect.HasPoint(client);
            m._dragged = false;
            m._pressAt = m._lastAt = local;
        }
        else if (m._pressing && (e is InputEventScreenDrag || e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left }))
        {
            // A drag over the list scrolls it, with the finger.
            if (!m._dragged && local.DistanceTo(m._pressAt) > 8 * m._scale / 3f)
            {
                m._dragged = true;
            }

            if (m._dragged)
            {
                m._scroll.ScrollVertical -= (int)Math.Round((local.Y - m._lastAt.Y) / m._scale);
            }

            m._lastAt = local;
        }
        else if (released && m._pressing)
        {
            m._pressing = false;

            if (!m._dragged)
            {
                m.Click(local);
            }
        }

        return true;
    }

    /// <summary>A tap at a point of the viewport, as the control under it expects it.</summary>
    private void Click(Vector2 local)
    {
        _viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
        _viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        _viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = false }, true);

        // A field wants the keyboard: on a phone that means the soft one.
        if (_viewport.GuiGetFocusOwner() is LineEdit field)
        {
            DisplayServer.VirtualKeyboardShow(field.Text);
        }
    }

    // --- the probe ------------------------------------------------------------------

    /// <summary>For the probe: the centre of a control with this text (a button, or an action by name), in client pixels.</summary>
    public static Vector2? CentreOf(string text)
    {
        if (!IsOpen)
        {
            return null;
        }

        foreach (Node n in _instance._card.FindChildren("*", "Button", true, false))
        {
            if (n is Button b && b.IsVisibleInTree()
                && (b.Text == text || b.HasMeta("action") && (string)b.GetMeta("action") == text))
            {
                // Bring it into view first: it may be further down the list.
                _instance._scroll.EnsureControlVisible(b);
                Vector2 inViewport = b.GetGlobalRect().GetCenter() * _instance._scale;
                return _instance._rect.Position + inViewport / Dpi;
            }
        }

        return null;
    }

    /// <summary>For the probe: type into the search field.</summary>
    public static void SearchForProbe(string text)
    {
        if (IsOpen)
        {
            _instance._search.Text = text;
            _instance.Refresh();
        }
    }
}
