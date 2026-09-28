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
/// like the window menu: Godot controls in a SubViewport, in the window
/// card's charcoal and gold, drawn into the second screen's bitmap by
/// DualScreen.Draw; the second screen's touches come here first
/// (<see cref="HandleInput"/>).
/// </remarks>
internal sealed partial class CompanionTabs : Node
{
    private static readonly Color Gold = new("dfbb77"), Text = new("eeeade"), Muted = new("abb5ac"), Fill = new("141917");

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

    /// <summary>The height of the Classic-mode strip along the second screen's bottom.</summary>
    private const int StripHeight = 36;

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
            Size = new Vector2I(64, 64),
        };
        AddChild(_viewport);
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);
        Build();
    }

    // --- building ---------------------------------------------------------------

    private static StyleBoxFlat Box(string bg, string border, int radius = 8, int pad = 12) => new()
    {
        BgColor = new Color(bg), BorderColor = new Color(border),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = pad, ContentMarginRight = pad, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 15 };
        theme.SetStylebox("normal", "Button", Box("202922", "455342"));
        theme.SetStylebox("hover", "Button", Box("303d2e", "b4a16b"));
        theme.SetStylebox("pressed", "Button", Box("3f4931", "dfbb77"));
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", Text);
        theme.SetColor("font_pressed_color", "Button", Text);
        theme.SetColor("font_color", "Label", Text);
        return theme;
    }

    private static Label Lbl(string text, int size, Color color)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private void Build()
    {
        _panel = new PanelContainer { Theme = BuildTheme() };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Fill, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10 });
        _root.AddChild(_panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(col);

        // Tab strip: the two destinations, and Classic to go back to the shelf.
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 8);
        col.AddChild(tabs);
        _tabJournal = Tab("Journal", () => Show(0));
        _tabCharacter = Tab("Character", () => Show(1));
        tabs.AddChild(_tabJournal);
        tabs.AddChild(_tabCharacter);
        tabs.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        tabs.AddChild(Tab("Classic ›", () => SetClassic(true)));

        // Journal: the newest lines at the bottom; a finger drag scrolls back.
        _journalView = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddChild(_journalView);
        var journalCard = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        journalCard.AddThemeStyleboxOverride("panel", Box("1b221e", "354039", 10));
        journalCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _journalView.AddChild(journalCard);
        _journalLines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _journalLines.AddThemeConstantOverride("separation", 4);
        _journalView.AddChild(_journalLines);

        // Character: who, the three bars, then the numbers.
        _characterView = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, Visible = false };
        ((VBoxContainer)_characterView).AddThemeConstantOverride("separation", 8);
        col.AddChild(_characterView);
        var who = new PanelContainer();
        who.AddThemeStyleboxOverride("panel", Box("1b221e", "354039", 10, 14));
        _characterView.AddChild(who);
        var whoCol = new VBoxContainer();
        who.AddChild(whoCol);
        whoCol.AddChild(_name = Lbl("", 24, Text));
        whoCol.AddChild(_title = Lbl("", 14, Muted));
        whoCol.AddChild(_hits = Bar(new Color("b8483e"), "Hits"));
        whoCol.AddChild(_mana = Bar(new Color("3f6fc4"), "Mana"));
        whoCol.AddChild(_stam = Bar(new Color("c9a23b"), "Stamina"));

        var numbers = new PanelContainer();
        numbers.AddThemeStyleboxOverride("panel", Box("1b221e", "354039", 10, 14));
        _characterView.AddChild(numbers);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 6);
        numbers.AddChild(grid);

        foreach (string key in new[] { "Strength", "Gold", "Dexterity", "Weight", "Intelligence", "Armour" })
        {
            Label k = Lbl(key, 15, Muted);
            k.AutowrapMode = TextServer.AutowrapMode.Off;
            Label v = Lbl("", 17, Text);
            v.AutowrapMode = TextServer.AutowrapMode.Off;
            v.CustomMinimumSize = new Vector2(90, 0);
            grid.AddChild(k);
            grid.AddChild(v);
            _values[key] = v;
        }

        // Classic mode: one pill in the corner to come back.
        _pill = new PanelContainer { Theme = BuildTheme(), Visible = false };
        _pill.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        Button back = Tab("‹ Tabs", () => SetClassic(false));
        back.CustomMinimumSize = new Vector2(84, 30);
        _pill.AddChild(back);
        _root.AddChild(_pill);
    }

    private static Button Tab(string text, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(96, 44) };
        b.AddThemeFontSizeOverride("font_size", 16);
        b.Pressed += pressed;
        return b;
    }

    private static ProgressBar Bar(Color color, string what)
    {
        var bar = new ProgressBar { CustomMinimumSize = new Vector2(0, 22), ShowPercentage = false, TooltipText = what };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color("2c352f"),
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
        });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
        });
        var label = Lbl(what, 13, Text);
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
        _tabJournal.AddThemeStyleboxOverride("normal", tab == 0 ? Box("3f4931", "dfbb77") : Box("202922", "455342"));
        _tabCharacter.AddThemeStyleboxOverride("normal", tab == 1 ? Box("3f4931", "dfbb77") : Box("202922", "455342"));
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

        float dpi = Math.Max(1f, Client.Game.DpiScale);
        var size = new Vector2(DualScreen.LogicalWidth, DualScreen.LogicalHeight);

        if (size != _size || dpi != _scale)
        {
            _size = size;
            _scale = dpi;
            _root.Scale = new Vector2(dpi, dpi);
            _panel.Position = Vector2.Zero;
            _panel.Size = size;
            Show(_tab);
        }

        _panel.Visible = !_classic;
        _pill.Visible = _classic;

        if (_classic)
        {
            // The pill in its own strip along the bottom (ShelfReserve); the
            // viewport covers only that strip.
            _pill.ResetSize();
            _pill.Position = new Vector2(size.X - _pill.Size.X - 6, (StripHeight - _pill.Size.Y) / 2);
            _viewport.Size = new Vector2I((int)Math.Ceiling(size.X * dpi), (int)Math.Ceiling(StripHeight * dpi));
            return;
        }

        _viewport.Size = new Vector2I((int)Math.Ceiling(size.X * dpi), (int)Math.Ceiling(size.Y * dpi));

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
                row.AddThemeConstantOverride("separation", 10);
                Label time = Lbl(e.Time.ToString("HH:mm"), 13, new Color(Muted, 0.8f));
                time.AutowrapMode = TextServer.AutowrapMode.Off;
                time.CustomMinimumSize = new Vector2(40, 0);
                time.VerticalAlignment = VerticalAlignment.Top;
                row.AddChild(time);
                var text = Lbl(system ? e.Text : $"{e.Name}: {e.Text}", 16, system ? Gold : Text);
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
        if (_instance == null || !Active) return;
        CompanionTabs m = _instance;
        Vector2I px = m._viewport.Size;
        int w = (int)Math.Round(px.X / m._scale), h = (int)Math.Round(px.Y / m._scale);
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
        if (_instance == null || !Active) return false;
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
        Vector2 local = (client - new Vector2(DualScreen.MainWidth, top)) * m._scale;

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
                        m._journalScroll += dy;
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
