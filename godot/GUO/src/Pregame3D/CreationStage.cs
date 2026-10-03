// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: character creation for the 3D pregame, the
// "Tailor's Table" (docs/ui/pregame_3d.md). The data it builds is the classic
// gumps' (CharCreationGump, CreateCharAppearanceGump, CreateCharProfessionGump,
// CreateCharTradeGump, CreateCharSelectionCityGump): the same PlayerMobile,
// items, skills, stats, city and profession byte go to
// LoginScene.CreateCharacter. Those gumps are read, never edited.

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Assets;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps.CharCreation;
using GUO.Input.Touch;
using GUO.Resources;
using GUO.Utility;

namespace GUO.Pregame3D;

/// <summary>
/// Five steps under tabs (Trade, Look, Skills for Advanced only, Home,
/// Name), the character large on a plinth to the left, live. L1/R1 or
/// Start move between steps, B goes back one (out of Trade to the
/// character list).
/// </summary>
internal sealed partial class CreationStage : Stage
{
    public enum Step { Trade, Look, Skills, Home, Name }

    private static readonly string[] StepNames = { "Trade", "Look", "Skills", "Home", "Name" };

    // --- palette (docs/ui/uo_godot_style.md + the Tailor's Table mockups) ---
    private static readonly Color ParchmentBg = new("e8dcb8");
    private static readonly Color Border = new("5a3a0c");
    private static readonly Color Ink = new("1c1812");
    private static readonly Color Heading = new("5a3a0c");
    private static readonly Color Gold = new("e0b050");
    private static readonly Color Active = new("8c1c12");
    private static readonly Color Cream = new("eeeade");
    private static readonly Color Muted = new("6e6250");

    private Step _step;
    private readonly HashSet<Step> _done = new();
    private World _world;
    private PlayerMobile _character;

    private bool _female;
    private RaceType _race = RaceType.HUMAN;
    private string _name = "";
    private readonly Dictionary<Layer, int> _option = new();

    /// <summary>Per layer: the index into its hue grid, -1 for the picker's starting hue.</summary>
    private readonly Dictionary<Layer, int> _hueIndex = new();

    private ProfessionInfo _profession;
    private ProfessionInfo _category;
    private int _citySelected = -1;

    private readonly int[] _stats = new int[3]; // Str, Int, Dex (the trade gump's order)
    private int[] _skillPick;
    private int[] _skillValue;
    private List<SkillEntry> _skillList;

    // --- UI ---
    private Control _chrome;
    private HBoxContainer _tabs;
    private readonly Label[] _tabLabels = new Label[5];
    private readonly PanelContainer[] _tabPanels = new PanelContainer[5];
    private PanelContainer _content;
    private VBoxContainer _body;
    private ScrollContainer _scroll;
    private readonly List<IOverlayFocusable> _items = new();
    private Control _popover;
    private readonly List<IOverlayFocusable> _popItems = new();
    private Action<bool> _popClose;

    // --- the figure ---
    private TextureRect _figure;
    private Panel _shadow;
    private byte _direction = 3; // facing the viewer (UO direction 3: south-ish... see Turn)
    private int _frame;
    private double _frameTime;
    private bool _dirty = true;
    private int _retries;
    private float _stickX;
    private int _figureHeight = 80;
    private Rect2I _used = new(Mannequin.Foot.X - 16, Mannequin.Foot.Y - 64, 32, 64);
    private bool _toldFigure;

    public static string ProbeFocusTag => (PregameScreen.Instance?.Stage as CreationStage)?.FocusTag;
    public static Step? ProbeStep => (PregameScreen.Instance?.Stage as CreationStage)?._step;
    public static bool ProbePopoverOpen => (PregameScreen.Instance?.Stage as CreationStage)?._popover != null;
    public static bool ProbeMapReady => (PregameScreen.Instance?.Stage as CreationStage)?._mapReady ?? false;
    public static byte? ProbeDirection => (PregameScreen.Instance?.Stage as CreationStage)?._direction;

    private string FocusTag => (D.Focus.Current as UiFocus)?.Tag as string;

    private static int SkillsCount => CharCreationGump._skillsCount;

    private bool Advanced => _profession != null && _profession.DescriptionIndex <= 0;

    public override IEnumerable<IOverlayFocusable> OverlayItems => _popover != null ? _popItems : _items;

    public override string Hints
    {
        get
        {
            if (_popover != null)
            {
                return _step == Step.Skills ? "A  Choose     B  Close" : "A  Keep     B  Undo";
            }

            return _step switch
            {
                Step.Trade => "A  Choose     Y  Random     RS  Turn     B  Back",
                Step.Look => "<>  Change     A  Palette     Y  Random     LB/RB  Step     Start  Next     B  Back",
                Step.Skills => "<>  Adjust     A  Skill list     X  Clear     Start  Next     B  Back",
                Step.Home => "A  Choose     Start  Next     B  Back",
                _ => "A  Type     Start  Enter Britannia     B  Back",
            };
        }
    }

    // ==========================================================================
    // Enter / Exit / Update
    // ==========================================================================

    public override void Enter()
    {
        _world = Client.Game.UO.World;
        D.SetDim(0.42f);

        // As CreateCharAppearanceGump's constructor: a human male, defaults.
        _female = false;
        _race = RaceType.HUMAN;
        _name = "";
        _hueIndex.Clear();
        ResetStyles();
        _profession = null;
        _category = null;
        _citySelected = -1;
        _skillPick = null;
        _character = null;
        _done.Clear();
        Rebuild();

        BuildChrome();
        BuildFigure();
        ShowStep(Step.Trade);
    }

    private void KeepInView(IFocusable f)
    {
        if (_popover == null && f is IOverlayFocusable o && _scroll != null && GodotObject.IsInstanceValid(_scroll) && _body.IsAncestorOf(o.Control))
        {
            Callable.From(() => { if (GodotObject.IsInstanceValid(_scroll) && GodotObject.IsInstanceValid(o.Control) && _scroll.IsInsideTree() && o.Control.IsInsideTree() && _scroll.IsAncestorOf(o.Control)) _scroll.EnsureControlVisible(o.Control); }).CallDeferred();
        }
    }

    public override void Exit()
    {
        D.Focus.Moved -= KeepInView;
        ClosePopover(false);
        _chrome?.QueueFree();
        _chrome = null;
        _figure = null;
        _shadow = null;
        _items.Clear();
        D.Focus.Clear();
        D.SetDim(1f);
    }

    public override void Update(double delta)
    {
        if (_figure == null)
        {
            return;
        }

        // A slow idle: the stand animation's frames, about five a second.
        _frameTime += delta;

        if (_frameTime > 0.2)
        {
            _frameTime = 0;
            int count = Mannequin.FrameCount(_character, _direction);

            if (count > 1)
            {
                _frame = (_frame + 1) % count;
                _dirty = true;
            }
        }

        if (_dirty)
        {
            Image img = Mannequin.Compose(_character, _direction, _frame, out bool complete);
            _dirty = !complete && ++_retries < 30;

            if (complete)
            {
                _retries = 0;
            }

            Rect2I used = img.GetUsedRect();

            if (complete && !_toldFigure)
            {
                _toldFigure = true;
                GD.Print($"[GUO] pregame3d: figure body 0x{_character.Graphic:X4} dir {_direction}: {used.Size.X}x{used.Size.Y} px drawn, {Mannequin.FrameCount(_character, _direction)} frame(s)");
            }

            if (used.Size.Y > 0)
            {
                _figureHeight = Mannequin.Foot.Y - used.Position.Y;
            }

            // Only the drawn part (the canvas is mostly empty), so the figure's
            // rect is the figure: placed by its feet, it stays on screen.
            if (used.Size.X > 0 && used.Size.Y > 0)
            {
                _used = used;
                Image crop = img.GetRegion(used);

                if (_figure.Texture is ImageTexture tex && tex.GetSize() == new Vector2(crop.GetWidth(), crop.GetHeight()))
                {
                    tex.Update(crop);
                }
                else
                {
                    _figure.Texture = ImageTexture.CreateFromImage(crop);
                }
            }
        }

        PlaceFigure();
    }

    /// <summary>
    /// The figure on the left third, its feet low, a whole number of screen
    /// pixels to an art pixel, about 47% of the screen's height (380 of 800).
    /// </summary>
    private void PlaceFigure()
    {
        Vector2 room = D.OverlayRoot.Size;
        int ui = D.UiScale;
        float screenH = room.Y * ui;
        var feet = new Vector2(room.X * 0.19f, room.Y * 0.86f);
        int k = Math.Max(1, (int) Math.Round(screenH * 0.475f / Math.Max(30, _figureHeight)));

        // Never taller than the room between the tabs and the feet, nor wider than the left third.
        while (k > 1 && (_used.Size.Y * k / (float) ui > feet.Y - 36 || _used.Size.X * k / (float) ui > room.X * 0.36f))
        {
            k--;
        }

        _figure.Size = new Vector2(_used.Size.X, _used.Size.Y) * k / ui;
        // Where the canvas's foot point lands: the crop keeps its offset from it.
        _figure.Position = feet - new Vector2(Mannequin.Foot.X - _used.Position.X, Mannequin.Foot.Y - _used.Position.Y) * k / ui;
        _shadow.Size = new Vector2(34f * k / ui, 9f * k / ui);
        _shadow.Position = feet - _shadow.Size / 2f;
    }

    /// <summary>The right stick turns the figure, one direction per push.</summary>
    public override bool RightStick(float x)
    {
        int was = Math.Abs(_stickX) > 0.6f ? Math.Sign(_stickX) : 0;
        int now = Math.Abs(x) > 0.6f ? Math.Sign(x) : 0;
        _stickX = x;

        if (now != 0 && now != was)
        {
            Turn(now);
        }

        return true;
    }

    private void Turn(int d)
    {
        _direction = (byte) ((_direction + d + 8) % 8);
        _frame = 0;
        _dirty = true;
    }

    private void BuildFigure()
    {
        // A soft shadow for the figure to stand on, then the figure (xBR'd like the painting).
        _shadow = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        _shadow.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.45f),
            CornerRadiusTopLeft = 64, CornerRadiusTopRight = 64, CornerRadiusBottomLeft = 64, CornerRadiusBottomRight = 64,
            CornerDetail = 6,
        });
        _figure = new TextureRect
        {
            Name = "Figure",
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = GumpProp.NewMaterial(),
        };
        _chrome.AddChild(_shadow);
        _chrome.AddChild(_figure);
        _chrome.MoveChild(_shadow, 0);
        _chrome.MoveChild(_figure, 1);
        _dirty = true;
    }

    public override void Resized() => Callable.From(() =>
    {
        if (_figure != null)
        {
            PlaceFigure();
        }
    }).CallDeferred();

    // ==========================================================================
    // The character: CreateCharAppearanceGump's data flow
    // ==========================================================================

    private void ResetStyles()
    {
        _option[Layer.Hair] = 1;
        _option[Layer.Beard] = 0;
    }

    private bool HasBeard => !_female && _race != RaceType.ELF;

    private ushort[] Palette(Layer layer) => layer switch
    {
        Layer.Invalid => CharacterCreationValues.GetSkinPallet(_race),
        Layer.Hair or Layer.Beard => CharacterCreationValues.GetHairPallet(_race),
        _ => null,
    };

    /// <summary>A layer's hue grid as the picker builds it (ColorPickerBox: palette + 1, or 3, 8, 13 ... without one).</summary>
    private ushort[] HueGrid(Layer layer)
    {
        ushort[] palette = Palette(layer);

        if (palette != null)
        {
            return palette.Select(h => (ushort) (h + 1)).ToArray();
        }

        var grid = new ushort[200];
        ushort start = 2;

        for (int i = 0; i < grid.Length; i++, start += 5)
        {
            grid[i] = (ushort) (start + 1);
        }

        return grid;
    }

    /// <summary>The grid's columns, as the gump lays it out (8 rows of a palette; 10x20 otherwise).</summary>
    private int HueColumns(Layer layer)
    {
        ushort[] palette = Palette(layer);
        return palette != null ? Math.Max(1, palette.Length >> 3) : 20;
    }

    private ushort Hue(Layer layer)
    {
        ushort[] grid = HueGrid(layer);
        int i = _hueIndex.TryGetValue(layer, out int v) ? v : -1;

        if (i < 0 || i >= grid.Length)
        {
            ushort[] palette = Palette(layer);
            return (ushort) ((palette != null && palette.Length > 0 ? palette[0] : 1) + 1);
        }

        return grid[i];
    }

    /// <summary>CreateCharAppearanceGump.CreateCharacter then UpdateEquipments, with this stage's choices.</summary>
    private void Rebuild()
    {
        if (_character == null || !_world.Mobiles.ContainsKey(_character.Serial))
        {
            _character = new PlayerMobile(_world, 1);
            _world.Mobiles.Add(_character);
        }

        LinkedObject first = _character.Items;

        while (first != null)
        {
            LinkedObject next = first.Next;
            _world.RemoveItem((Item) first, true);
            first = next;
        }

        _character.Clear();
        _character.Race = _race;
        _character.IsFemale = _female;

        if (_female)
        {
            _character.Flags |= Flags.Female;
        }
        else
        {
            _character.Flags &= ~Flags.Female;
        }

        ushort shirt = Hue(Layer.Shirt), pants = Hue(Layer.Pants);

        switch (_race)
        {
            case RaceType.GARGOYLE:
                _character.Graphic = _female ? (ushort) 0x029B : (ushort) 0x029A;
                Push(CreateItem(0x4001, shirt, Layer.Robe));
                break;

            case RaceType.ELF when _female:
                _character.Graphic = 0x025E;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(0x1531, pants, Layer.Skirt));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;

            case RaceType.ELF:
                _character.Graphic = 0x025D;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(0x152F, pants, Layer.Pants));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;

            default:
                _character.Graphic = _female ? (ushort) 0x0191 : (ushort) 0x0190;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(_female ? 0x1531 : 0x152F, pants, Layer.Pants));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;
        }

        _character.Hue = Hue(Layer.Invalid);

        if (HasBeard)
        {
            var beards = CharacterCreationValues.GetFacialHairComboContent(_race);

            if (beards.Labels.Length > 0)
            {
                Push(CreateItem(beards.GetGraphic(Math.Clamp(_option[Layer.Beard], 0, beards.Labels.Length - 1)), Hue(Layer.Beard), Layer.Beard));
            }
        }

        var hairs = CharacterCreationValues.GetHairComboContent(_female, _race);

        if (hairs.Labels.Length > 0)
        {
            Push(CreateItem(hairs.GetGraphic(Math.Clamp(_option[Layer.Hair], 0, hairs.Labels.Length - 1)), Hue(Layer.Hair), Layer.Hair));
        }

        _character.Name = _name;

        // The profession's (or the trade page's) values survive a look change.
        ApplyTrade();
        _dirty = true;
        _frame = 0;
    }

    private void Push(Item item)
    {
        if (item != null)
        {
            _character.PushToBack(item);
        }
    }

    /// <summary>CreateCharAppearanceGump.CreateItem: a client-side item per layer, its serial the layer.</summary>
    private Item CreateItem(int id, ushort hue, Layer layer)
    {
        Item exists = _character.FindItemByLayer(layer);

        if (exists != null)
        {
            _world.RemoveItem(exists, true);
            _character.Remove(exists);
        }

        if (id == 0)
        {
            return null;
        }

        Item item = _world.GetOrCreateItem(0x4000_0000 + (uint) layer);
        _character.Remove(item);
        item.Graphic = (ushort) id;
        item.Hue = hue;
        item.Layer = layer;
        item.Container = _character;

        return item;
    }

    private (bool elf, bool garg) AllowedRaces()
    {
        CharacterListFlags flags = _world.ClientFeatures.Flags;
        LockedFeatureFlags locks = _world.ClientLockedFeatures.Flags;

        return ((flags & CharacterListFlags.CLF_ELVEN_RACE) != 0 && locks.HasFlag(LockedFeatureFlags.ML), locks.HasFlag(LockedFeatureFlags.SA));
    }

    private List<RaceType> Races()
    {
        (bool elf, bool garg) = AllowedRaces();
        var races = new List<RaceType> { RaceType.HUMAN };

        if (elf)
        {
            races.Add(RaceType.ELF);
        }

        if (garg)
        {
            races.Add(RaceType.GARGOYLE);
        }

        return races;
    }

    /// <summary>The chosen profession's skills and stats (or the trade page's) onto the character.</summary>
    private void ApplyTrade()
    {
        if (_character == null)
        {
            return;
        }

        foreach (Skill skill in _character.Skills)
        {
            skill.ValueFixed = 0;
            skill.BaseFixed = 0;
            skill.CapFixed = 0;
            skill.Lock = Lock.Locked;
        }

        if (_profession == null)
        {
            return;
        }

        if (Advanced)
        {
            if (_skillPick != null && _skillList != null)
            {
                for (int i = 0; i < _skillPick.Length; i++)
                {
                    if (_skillPick[i] >= 0)
                    {
                        Skill skill = _character.Skills[_skillList[_skillPick[i]].Index];
                        skill.ValueFixed = (ushort) _skillValue[i];
                    }
                }
            }

            _character.Strength = (ushort) _stats[0];
            _character.Intelligence = (ushort) _stats[1];
            _character.Dexterity = (ushort) _stats[2];
            return;
        }

        for (int i = 0; i < SkillsCount; i++)
        {
            int skillIndex = _profession.SkillDefVal[i, 0];

            if (skillIndex < _character.Skills.Length)
            {
                _character.Skills[skillIndex].ValueFixed = (ushort) _profession.SkillDefVal[i, 1];
            }
        }

        _character.Strength = (ushort) _profession.StatsVal[0];
        _character.Intelligence = (ushort) _profession.StatsVal[1];
        _character.Dexterity = (ushort) _profession.StatsVal[2];
    }

    // ==========================================================================
    // Chrome: tabs, the content card
    // ==========================================================================

    /// <summary>A flat box: only for small marks (a bar's track, a swatch's edge, a map pin).</summary>
    private static StyleBoxFlat Box(Color bg, Color border, int width = 2, int margin = 6) => new()
    {
        BgColor = bg,
        BorderColor = border,
        BorderWidthLeft = width, BorderWidthTop = width, BorderWidthRight = width, BorderWidthBottom = width,
        ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin - 2, ContentMarginBottom = margin - 2,
    };

    /// <summary>The client's parchment (0x0BB8) as a panel; tint 1 = focused (gold), 2 = current (red).</summary>
    private static StyleBox Parch(int margin, int tint = 0) => Overlay.Frame(Overlay.Parchment, margin, tint);

    private static PanelContainer Card(int margin = 6, ushort frame = Overlay.Parchment)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", Overlay.Frame(frame, margin));
        return p;
    }

    private static Label Text(string s, Color c, int scale = 1, bool wrap = false) => Overlay.Text(s, c, scale, wrap);

    private void BuildChrome()
    {
        _chrome = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        D.OverlayRoot.AddChild(_chrome);
        _chrome.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _tabs = Overlay.Row(4);
        _chrome.AddChild(_tabs);
        _tabs.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.Minsize, 6);
        _tabs.GrowHorizontal = Control.GrowDirection.Both;

        for (int i = 0; i < 5; i++)
        {
            _tabPanels[i] = Card(5);
            _tabLabels[i] = Text("", Ink);
            _tabPanels[i].AddChild(_tabLabels[i]);
            _tabs.AddChild(_tabPanels[i]);
        }

        _content = Card(10, Overlay.Stone);
        _chrome.AddChild(_content);
        _content.AnchorLeft = 0.38f;
        _content.AnchorRight = 1f;
        _content.AnchorTop = 0f;
        _content.AnchorBottom = 1f;
        _content.OffsetLeft = 0;
        _content.OffsetRight = -8;
        _content.OffsetTop = 34;
        _content.OffsetBottom = -28;
        _content.ClipContents = true;

        // The step's content fits the card: rows are compact, and only when a
        // step still has more than the room between the tabs and the hint
        // bar does it scroll (the focused row kept in view).
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FollowFocus = false,
        };
        _content.AddChild(_scroll);
        _body = Overlay.Column(2);
        _body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scroll.AddChild(_body);
        D.Focus.Moved += KeepInView;
    }

    private void RefreshTabs()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;

        for (int i = 0; i < 5; i++)
        {
            var step = (Step) i;
            bool skipped = step == Step.Skills && !Advanced;
            string caption = StepNames[i];

            if (_done.Contains(step) && step != _step)
            {
                caption = step switch
                {
                    Step.Trade when _profession != null => Title(clilocs.GetString(_profession.Localization) ?? _profession.Name),
                    Step.Look => (_female ? "Female " : "Male ") + Title(_race.ToString()),
                    Step.Home when CityName() != null => CityName(),
                    Step.Name when _name.Length > 0 => _name,
                    _ => caption,
                };
            }

            _tabLabels[i].Text = $"{i + 1} {caption}";
            bool current = step == _step;
            _tabPanels[i].AddThemeStyleboxOverride("panel", Parch(5, current ? 2 : 0));
            _tabLabels[i].AddThemeColorOverride("font_color", current ? Ink : skipped ? Muted : _done.Contains(step) ? Heading : Ink);
            _tabPanels[i].Modulate = skipped && !current ? new Color(1, 1, 1, 0.6f) : Colors.White;
        }
    }

    private static string Title(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

    // ==========================================================================
    // Steps
    // ==========================================================================

    private void ShowStep(Step step, string focusTag = null)
    {
        ClosePopover(false);
        _step = step;

        foreach (Node c in _body.GetChildren())
        {
            _body.RemoveChild(c);
            c.QueueFree();
        }

        _items.Clear();
        D.Focus.Clear();

        switch (step)
        {
            case Step.Trade: TradeStep(); break;
            case Step.Look: LookStep(); break;
            case Step.Skills: SkillsStep(); break;
            case Step.Home: HomeStep(); break;
            case Step.Name: NameStep(); break;
        }

        IFocusable focus = _items.FirstOrDefault(i => (i as UiFocus)?.Tag as string == focusTag) ?? DefaultFocus();
        D.Focus.Set(focus);
        RefreshTabs();
        D.RefreshHints();
    }

    private IFocusable DefaultFocus() => _items.FirstOrDefault(i => i.CanFocus);

    /// <summary>A step forward: the current one's checks first, Skills only for Advanced.</summary>
    private void Next()
    {
        if (!Leave(_step))
        {
            return;
        }

        if (_step == Step.Name)
        {
            Create();
            return;
        }

        Step next = _step + 1;

        if (next == Step.Skills && !Advanced)
        {
            next = Step.Home;
        }

        ShowStep(next);
    }

    private void Back()
    {
        if (_step == Step.Trade)
        {
            if (_category != null)
            {
                _category = null;
                ShowStep(Step.Trade);
                return;
            }

            Login.StepBack();
            return;
        }

        Step prev = _step - 1;

        if (prev == Step.Skills && !Advanced)
        {
            prev = Step.Look;
        }

        ShowStep(prev);
    }

    /// <summary>Whether the step may be left forward; marks it done.</summary>
    private bool Leave(Step step)
    {
        switch (step)
        {
            case Step.Trade when _profession == null:
                D.ShowMessage("Choose a trade first.");
                return false;

            case Step.Skills when !SkillsValid():
                return false;

            case Step.Name when !NameValid():
                return false;
        }

        _done.Add(step);
        ApplyTrade();
        return true;
    }

    public override bool Command(PadCmd cmd)
    {
        if (_popover != null)
        {
            switch (cmd)
            {
                case PadCmd.A or PadCmd.Start:
                    D.Focus.Current?.Press();
                    return true;
                case PadCmd.B:
                    ClosePopover(false);
                    return true;
                case PadCmd.Up or PadCmd.Down or PadCmd.Left or PadCmd.Right:
                    return false; // the focus moves inside the popover
            }

            return true;
        }

        switch (cmd)
        {
            case PadCmd.B:
                Back();
                return true;

            case PadCmd.Start:
                if (_step == Step.Trade && _profession == null && D.Focus.Current != null)
                {
                    D.Focus.Current.Press();
                    return true;
                }

                Next();
                return true;

            case PadCmd.RightShoulder:
                if (_step == Step.Trade && _profession == null)
                {
                    D.ShowMessage("Choose a trade first.");
                    return true;
                }

                Next();
                return true;

            case PadCmd.LeftShoulder:
                if (_step != Step.Trade)
                {
                    Back();
                }

                return true;

            case PadCmd.Y when _step == Step.Trade:
                RandomTrade();
                return true;

            case PadCmd.Y when _step == Step.Look:
                RandomLook();
                return true;

            case PadCmd.X when _step == Step.Skills:
                ClearFocusedSkill();
                return true;
        }

        return false;
    }

    // ==========================================================================
    // Popovers (palette, skill list)
    // ==========================================================================

    private void OpenPopover(Control content, List<IOverlayFocusable> items, IFocusable focus, Action<bool> close)
    {
        ClosePopover(false);
        PanelContainer pop = Card(10, Overlay.Stone);
        pop.AddChild(content);
        _chrome.AddChild(pop);
        pop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        pop.GrowHorizontal = Control.GrowDirection.Both;
        pop.GrowVertical = Control.GrowDirection.Both;
        Callable.From(() =>
        {
            if (IsInstanceValid(pop))
            {
                pop.ResetSize();
                pop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
                // Over the content card, clear of the figure.
                pop.Position = new Vector2(Math.Max(pop.Position.X, _content.Position.X), pop.Position.Y);
            }
        }).CallDeferred();
        _popover = pop;
        _popItems.Clear();
        _popItems.AddRange(items);
        _popClose = close;
        D.Focus.Set(focus ?? items.FirstOrDefault());
        D.RefreshHints();
    }

    private IFocusable _beforePopover;

    private void ClosePopover(bool keep)
    {
        if (_popover == null)
        {
            return;
        }

        Action<bool> close = _popClose;
        _popClose = null;
        _popover.QueueFree();
        _popover = null;
        _popItems.Clear();
        close?.Invoke(keep);
        D.Focus.Set(_beforePopover);
        _beforePopover = null;
        D.RefreshHints();
    }

    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);

    // ==========================================================================
    // Create
    // ==========================================================================

    private bool NameValid()
    {
        _character.Name = _name;
        int invalid = CreateCharAppearanceGump.Validate(_name);

        if (invalid > 0)
        {
            D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(invalid));
            return false;
        }

        return true;
    }

    private void Create()
    {
        if (_profession == null)
        {
            ShowStep(Step.Trade);
            return;
        }

        if (!NameValid())
        {
            return;
        }

        (bool allowElf, bool allowGarg) = AllowedRaces();

        if (_race == RaceType.ELF && !allowElf || _race == RaceType.GARGOYLE && !allowGarg)
        {
            ShowStep(Step.Look);
            return;
        }

        ApplyTrade();
        CityInfo city = City();
        int cityIndex = city?.Index ?? 0;
        _character.Name = _name;
        GUO.Utility.Logging.Log.Trace($"Creating character '{_name}'");
        GD.Print($"[GUO] pregame3d: create \"{_name}\" ({(_female ? "female" : "male")} {_race}), profession {_profession.DescriptionIndex}, "
            + $"str {_character.Strength} int {_character.Intelligence} dex {_character.Dexterity}, "
            + $"skills {string.Join(", ", _character.Skills.Where(s => s.ValueFixed > 0).Select(s => $"{s.Name} {s.ValueFixed}"))}, "
            + $"shirt hue {_character.FindItemByLayer(Layer.Shirt)?.Hue}, city {cityIndex} {city?.City}");
        Login.CreateCharacter(_character, cityIndex, (byte) _profession.DescriptionIndex);
    }
}
