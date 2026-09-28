// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch;

/// <summary>
/// Companion tabs (prototype, sprint story C7): the second screen as a set of
/// modern panels instead of a shelf of floating gumps. This first cut has two
/// destinations, Journal and Character, as docs/second-screen-ui-research.md
/// says to start with, plus a Classic tab that hands the screen back to the
/// shelf. The panels only read client state (JournalManager, the player); they
/// send nothing and duplicate no interactive gump.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. On only with the touch layer and a
/// second screen, and only when the profile's CompanionTabs is on (Options,
/// "Companion tabs on the second screen"; off by default while it is a
/// prototype) or --companion-tabs is given. The desktop never sees it. Drawn
/// like the window menu: Godot controls in a SubViewport, in the client's
/// own look (UoTheme: stone, parchment, font 1, the plate buttons, at a
/// whole-number scale), drawn into the second screen's bitmap by
/// DualScreen.Draw; the second screen's touches come here first
/// (<see cref="HandleInput"/>).
/// </remarks>
internal sealed partial class CompanionTabs : Node
{
    private static readonly Color Text = UoTheme.Ink, Muted = UoTheme.Muted, Heading = UoTheme.Heading;

    /// <summary>Set by --companion-tabs, for a run that shows them without a profile change.</summary>
    public static bool Forced { get; set; }

    public static bool Active => TouchInput.Enabled && DualScreen.ShelfOn
        && (Forced || (ProfileManager.CurrentProfile?.CompanionTabs ?? false))
        && (Client.Game?.UO?.World?.InGame ?? false);

    private static CompanionTabs _instance;

    private SubViewport _viewport;
    private Control _root, _panel, _pill;
    private Button _tabJournal, _tabCharacter;
    private Control _journalView, _characterView;
    private VBoxContainer _journalLines;
    private Label _name, _title;
    private readonly Dictionary<string, Label> _values = new();
    private ProgressBar _hits, _mana, _stam;
    private float _scale = 1f;
    private int _tab; // 0 journal, 1 character
    private bool _classic;
    private float _journalScroll;       // logical px from the bottom; 0 follows new lines
    private int _journalSeen = -1;
    private double _refresh;
    private Vector2 _size;              // the panel's logical size: the second screen's
    private bool _dragging;
    private Vector2 _dragLast;
    private bool _dragMoved;

    public static void Setup(Node host)
    {
        if (_instance == null)
        {
            _instance = new CompanionTabs();
            host.AddChild(_instance);
        }
    }

    /// <summary>
    /// Art pixels to the second screen's logical pixels: a whole number, one
    /// step under the cards' (UoTheme.PixelScale), as the tabs hold running
    /// text. The viewport is 1:1 with the screen's logical pixels, so one art
    /// pixel is always a square of them.
    /// </summary>
    private static int ArtScale => Math.Max(1, UoTheme.PixelScale - 1);

    /// <summary>The height of the Classic-mode strip along the second screen's bottom: a plate and a margin.</summary>
    private static int StripHeight => (UoTheme.ButtonHeight + 6) * ArtScale;

    /// <summary>
    /// Logical pixels the shelf keeps gumps out of: in Classic mode the "‹ Tabs"
    /// pill sits in its own strip along the bottom, and DualScreen clamps gumps
    /// above it, the way C2 keeps gumps above the touch bar.
    /// </summary>
    public static int ShelfReserve => _instance != null && _instance._classic && Active ? StripHeight : 0;

    /// <summary>For the probe and the trace: which tab shows, or "classic".</summary>
    public static string State => _instance == null ? "none" : _instance._classic ? "classic" : _instance._tab == 0 ? "journal" : "character";

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            Size = new Vector2I(64, 64),
        };
        AddChild(_viewport);
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);

        // Built on first use (_Process), once the client's art is loaded: the
        // panel is the client's own gumps and font (UoTheme).
    }

    private bool _built;

    // --- building ---------------------------------------------------------------

    /// <summary>A label in font 1 (2x for the name), wrapping at words.</summary>
    private static Label Lbl(string text, bool big, Color color)
    {
        Label l = UoTheme.Label(text, color, big ? 2 : 1);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    /// <summary>A parchment card, as the client's text fields and scrolls.</summary>
    private static StyleBox Parchment() => UoTheme.Frame(UoTheme.FieldFrame, 6);

    private void Build()
    {
        _panel = new PanelContainer { Theme = UoTheme.Theme };
        _root.AddChild(_panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _panel.AddChild(col);

        // Tab strip: the two destinations, and Classic to go back to the shelf.
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        col.AddChild(tabs);
        _tabJournal = Tab("Journal", () => Show(0));
        _tabCharacter = Tab("Character", () => Show(1));
        tabs.AddChild(_tabJournal);
        tabs.AddChild(_tabCharacter);
        tabs.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        tabs.AddChild(Tab("Classic", () => SetClassic(true)));

        // Journal: the newest lines at the bottom; a finger drag scrolls back.
        _journalView = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddChild(_journalView);
        var journalCard = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        journalCard.AddThemeStyleboxOverride("panel", Parchment());
        journalCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _journalView.AddChild(journalCard);
        _journalLines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _journalLines.AddThemeConstantOverride("separation", 1);
        _journalView.AddChild(_journalLines);

        // Character: who, the three bars, then the numbers.
        _characterView = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, Visible = false };
        ((VBoxContainer)_characterView).AddThemeConstantOverride("separation", 5);
        col.AddChild(_characterView);
        var who = new PanelContainer();
        who.AddThemeStyleboxOverride("panel", Parchment());
        _characterView.AddChild(who);
        var whoCol = new VBoxContainer();
        who.AddChild(whoCol);
        whoCol.AddThemeConstantOverride("separation", 3);
        whoCol.AddChild(_name = Lbl("", true, Heading));
        whoCol.AddChild(_title = Lbl("", false, Muted));
        whoCol.AddChild(_hits = Bar(new Color("b8483e"), "Hits"));
        whoCol.AddChild(_mana = Bar(new Color("3f6fc4"), "Mana"));
        whoCol.AddChild(_stam = Bar(new Color("c9a23b"), "Stamina"));

        var numbers = new PanelContainer();
        numbers.AddThemeStyleboxOverride("panel", Parchment());
        _characterView.AddChild(numbers);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 2);
        numbers.AddChild(grid);

        foreach (string key in new[] { "Strength", "Gold", "Dexterity", "Weight", "Intelligence", "Armour" })
        {
            Label k = Lbl(key, false, Muted);
            k.AutowrapMode = TextServer.AutowrapMode.Off;
            Label v = Lbl("", false, Text);
            v.AutowrapMode = TextServer.AutowrapMode.Off;
            v.CustomMinimumSize = new Vector2(60, 0);
            grid.AddChild(k);
            grid.AddChild(v);
            _values[key] = v;
        }

        // Classic mode: one pill in the corner to come back.
        _pill = new PanelContainer { Theme = UoTheme.Theme, Visible = false };
        _pill.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        Button back = Tab("Tabs", () => SetClassic(false));
        _pill.AddChild(back);
        _root.AddChild(_pill);
    }

    private static Button Tab(string text, Action pressed)
    {
        Button b = UoTheme.Button(text, 64);
        b.Pressed += pressed;
        return b;
    }

    private static ProgressBar Bar(Color color, string what)
    {
        // Square, framed in ink, one flat colour: the client's own bars are.
        var bar = new ProgressBar { CustomMinimumSize = new Vector2(0, 16), ShowPercentage = false, TooltipText = what };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color("3a342c"), BorderColor = UoTheme.Ink,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = color, BorderColor = UoTheme.Ink,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        });
        Label label = Lbl(what, false, UoTheme.Cream);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Name = "Caption";
        bar.AddChild(label);
        return bar;
    }

    // --- state -------------------------------------------------------------------

    private void Show(int tab)
    {
        _tab = tab;
        _journalView.Visible = tab == 0;
        _characterView.Visible = tab == 1;
        // The open tab is the pressed plate, captioned in the heading colour.
        foreach ((Button b, bool on) in new[] { (_tabJournal, tab == 0), (_tabCharacter, tab == 1) })
        {
            b.AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? 0.70f : 1f));
            b.AddThemeColorOverride("font_color", on ? Heading : Text);
        }

        _refresh = 0;
        GD.Print($"[GUO] companion tabs: {State}");
    }

    private void SetClassic(bool classic)
    {
        _classic = classic;
        GD.Print($"[GUO] companion tabs: {State}");
    }

    public override void _Process(double delta)
    {
        if (!Active)
        {
            return;
        }

        if (!_built)
        {
            if (!UoTheme.Ready)
            {
                return;
            }

            Build();
            _built = true;
        }

        float art = ArtScale;
        var size = new Vector2(DualScreen.LogicalWidth, DualScreen.LogicalHeight);

        if (size != _size || art != _scale)
        {
            _size = size;
            _scale = art;
            _root.Scale = new Vector2(art, art);
            _panel.Position = Vector2.Zero;
            _panel.Size = (size / art).Floor();
            Show(_tab);
        }

        _panel.Visible = !_classic;
        _pill.Visible = _classic;

        if (_classic)
        {
            // The pill in its own strip along the bottom (ShelfReserve); the
            // viewport covers only that strip.
            _pill.ResetSize();
            _pill.Position = new Vector2((int)(size.X / art) - _pill.Size.X - 3, (int)((StripHeight / art - _pill.Size.Y) / 2));
            _viewport.Size = new Vector2I((int)size.X, StripHeight);
            return;
        }

        _viewport.Size = new Vector2I((int)size.X, (int)size.Y);

        _refresh -= delta;

        if (_refresh > 0)
        {
            return;
        }

        _refresh = 0.25;

        if (_tab == 0) RefreshJournal();
        else RefreshCharacter();
    }

    private void RefreshJournal()
    {
        var entries = JournalManager.Entries;
        int count = entries.Count;

        if (count != _journalSeen)
        {
            _journalSeen = count;

            foreach (Node n in _journalLines.GetChildren())
            {
                n.QueueFree();
            }

            for (int i = Math.Max(0, count - 80); i < count; i++)
            {
                JournalEntry e = entries[i];
                if (e == null) continue;
                bool system = string.IsNullOrEmpty(e.Name) || e.Name == "System";
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                Label time = Lbl(e.Time.ToString("HH:mm"), false, Muted);
                time.AutowrapMode = TextServer.AutowrapMode.Off;
                time.CustomMinimumSize = new Vector2(32, 0);
                time.VerticalAlignment = VerticalAlignment.Top;
                row.AddChild(time);
                var text = Lbl(system ? e.Text : $"{e.Name}: {e.Text}", false, system ? Heading : Text);
                text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(text);
                _journalLines.AddChild(row);
            }
        }

        LayoutJournal();
    }

    /// <summary>Pin the lines to the bottom of the view, minus how far the finger scrolled back.</summary>
    private void LayoutJournal()
    {
        Vector2 view = _journalView.Size;
        _journalLines.Position = new Vector2(12, 0);
        _journalLines.Size = new Vector2(Math.Max(0, view.X - 24), 0);
        _journalLines.ResetSize();
        _journalLines.Size = new Vector2(Math.Max(0, view.X - 24), _journalLines.GetCombinedMinimumSize().Y);
        float content = _journalLines.Size.Y;
        float maxBack = Math.Max(0, content - (view.Y - 20));
        _journalScroll = Math.Clamp(_journalScroll, 0, maxBack);
        _journalLines.Position = new Vector2(12, view.Y - 10 - content + _journalScroll);
    }

    private void RefreshCharacter()
    {
        PlayerMobile p = Client.Game.UO.World.Player;
        if (p == null) return;

        _name.Text = p.Name;
        _title.Text = string.IsNullOrEmpty(p.Title) ? " " : p.Title;
        SetBar(_hits, p.Hits, p.HitsMax, "Hits");
        SetBar(_mana, p.Mana, p.ManaMax, "Mana");
        SetBar(_stam, p.Stamina, p.StaminaMax, "Stamina");
        _values["Strength"].Text = p.Strength.ToString();
        _values["Dexterity"].Text = p.Dexterity.ToString();
        _values["Intelligence"].Text = p.Intelligence.ToString();
        _values["Gold"].Text = p.Gold.ToString();
        _values["Weight"].Text = $"{p.Weight} / {p.WeightMax}";
        _values["Armour"].Text = p.PhysicalResistance.ToString();
    }

    private static void SetBar(ProgressBar bar, int value, int max, string what)
    {
        bar.MaxValue = Math.Max(1, max);
        bar.Value = value;
        if (bar.GetNodeOrNull<Label>("Caption") is Label l) l.Text = $"{what}  {value} / {max}";
    }

    // --- drawing and input ---------------------------------------------------------

    /// <summary>The panel (or the pill), drawn into the second screen's bitmap in client pixels.</summary>
    public static void DrawShelf(UltimaBatcher2D b)
    {
        if (_instance == null || !Active || !_instance._built) return;
        CompanionTabs m = _instance;
        // The viewport is the screen's logical pixels, 1:1.
        Vector2I px = m._viewport.Size;
        int w = px.X, h = px.Y;
        int x = DualScreen.MainWidth;
        int y = m._classic ? DualScreen.LogicalHeight - StripHeight : 0;
        b.Draw(m._viewport.GetTexture(), new Compat.Rectangle(x, y, w, h), ShaderHueTranslator.GetHueVector(0), 0);
    }

    /// <summary>
    /// The second screen's pointer events while the tabs are up: pushed into
    /// the panel (a drag on the journal scrolls it). In Classic mode only the
    /// pill takes events; the rest go on to the shelf. True when consumed.
    /// </summary>
    public static bool HandleInput(InputEvent e)
    {
        if (_instance == null || !Active || !_instance._built) return false;
        CompanionTabs m = _instance;

        Vector2? window = e switch
        {
            InputEventScreenTouch t => t.Position,
            InputEventScreenDrag d => d.Position,
            _ => null,
        };

        if (window == null) return false;

        float dpi = Client.Game.DpiScale;
        Vector2 client = window.Value / dpi;
        if (client.X < DualScreen.MainWidth) return false;

        float top = m._classic ? DualScreen.LogicalHeight - StripHeight : 0;
        Vector2 local = client - new Vector2(DualScreen.MainWidth, top);

        if (m._classic && !new Rect2(Vector2.Zero, m._viewport.Size).HasPoint(local)) return false;

        TouchOverlay.Note(e);

        switch (e)
        {
            case InputEventScreenTouch { Pressed: true }:
                m._dragging = true;
                m._dragMoved = false;
                m._dragLast = client;
                m._viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
                break;

            case InputEventScreenDrag:
                if (m._dragging && !m._classic && m._tab == 0)
                {
                    float dy = client.Y - m._dragLast.Y;
                    if (Math.Abs(client.Y - m._dragLast.Y) > 0 || m._dragMoved)
                    {
                        m._dragMoved |= Math.Abs(dy) > 0.5f;
                        m._journalScroll += dy / m._scale;
                        m.LayoutJournal();
                    }
                    m._dragLast = client;
                }
                break;

            case InputEventScreenTouch { Pressed: false }:
                if (m._dragging && !m._dragMoved)
                {
                    // A tap: press and release on the control under it.
                    m._viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
                    m._viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = false }, true);
                }
                m._dragging = false;
                break;
        }

        return true;
    }

    /// <summary>For the probe: switch tabs as a tap would.</summary>
    public static void SetTabForProbe(string which)
    {
        if (_instance == null) return;
        if (which == "classic") _instance.SetClassic(true);
        else { _instance.SetClassic(false); _instance.Show(which == "character" ? 1 : 0); }
    }
}
