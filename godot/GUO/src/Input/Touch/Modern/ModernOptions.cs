// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// Options, Modern (ADR-0024, gump 1; the design is docs/ui/modern/options.md):
/// the classic Options window's look (dark glass, font 1, grey rules, a page
/// column, the jewelled Cancel/Apply/Default/Okay row) made for a thumb.
/// It covers the settings a player changes on a phone; "Classic view" opens
/// the ported Options, fitted, for the rest.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. Apply and Okay write each covered
/// setting to the same Profile field the classic Apply writes, with the same
/// side effects (the audio volumes, the roofs' redraw, the tree textures);
/// Cancel writes nothing; Default puts the page's covered settings back to a
/// new profile's values, as the classic Default does for its page.
/// </remarks>
internal sealed partial class ModernOptions : ModernGump
{
    // The classic Options' materials (docs/ui/modern/options.md).
    private static readonly Color Glass = new(0.047f, 0.047f, 0.047f, 0.95f);
    private static readonly Color Rule = new("6c6c6c");
    private static readonly Color Text = UoTheme.Cream;
    private static readonly Color Heading = new("9c9c9c");
    private static readonly Color Selected = new("3a3a3a");

    private const int RowHeight = 26; // art pixels: 78 device pixels at 3x, 5.4 mm on the Thor

    private enum Kind { Bool, Int, Choice, Action, Hue }

    private sealed class Setting
    {
        public string Page, Label;
        public Kind Kind;
        public Func<Profile, object> Get;
        public Action<Profile, object> Set;
        public string[] Choices;
        public int Min, Max;
        public Action Run;
        public Action<object> Show; // puts a value into the setting's control
    }

    private static readonly string[] Pages = { "General", "Sound", "Video", "Macros", "Tooltip", "Fonts", "Speech", "Combat", "Containers", "Touch" };

    private readonly List<Setting> _settings = new();
    private readonly Dictionary<Setting, object> _values = new();
    private readonly Dictionary<string, Control> _pageRows = new();
    private readonly Dictionary<string, Button> _pageButtons = new();
    private string _page = Pages[0];
    private Label _title;
    private ScrollContainer _scroll;
    private bool _audioChanged;
    private ModernMacros _macros;
    private ModernHuePicker _huePicker;
    private bool _macrosChanged;

    public ModernOptions(World world) : base(world)
    {
        Define();
    }

    protected override StyleBox CardStyle => new StyleBoxFlat
    {
        BgColor = Glass,
        ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    // --- the settings it covers (the classic Apply's fields) ----------------------

    private void Bool(string page, string label, Func<Profile, bool> get, Action<Profile, bool> set) =>
        _settings.Add(new Setting { Page = page, Label = label, Kind = Kind.Bool, Get = p => get(p), Set = (p, v) => set(p, (bool)v) });

    private void Int(string page, string label, int min, int max, Func<Profile, int> get, Action<Profile, int> set) =>
        _settings.Add(new Setting { Page = page, Label = label, Kind = Kind.Int, Min = min, Max = max, Get = p => get(p), Set = (p, v) => set(p, (int)v) });

    private void Choice(string page, string label, string[] choices, Func<Profile, int> get, Action<Profile, int> set) =>
        _settings.Add(new Setting { Page = page, Label = label, Kind = Kind.Choice, Choices = choices, Get = p => get(p), Set = (p, v) => set(p, (int)v) });

    private void Hue(string page, string label, Func<Profile, ushort> get, Action<Profile, ushort> set) =>
        _settings.Add(new Setting { Page = page, Label = label, Kind = Kind.Hue, Get = p => (int)get(p), Set = (p, v) => set(p, (ushort)(int)v) });

    private void Action_(string page, string label, Action run) =>
        _settings.Add(new Setting { Page = page, Label = label, Kind = Kind.Action, Run = run });

    private void Define()
    {
        Bool("General", "Highlight game objects", p => p.HighlightGameObjects, (p, v) => p.HighlightGameObjects = v);
        Bool("General", "Enable pathfinding", p => p.EnablePathfind, (p, v) => p.EnablePathfind = v);
        Bool("General", "Always run", p => p.AlwaysRun, (p, v) => p.AlwaysRun = v);
        Bool("General", "Unless hidden", p => p.AlwaysRunUnlessHidden, (p, v) => p.AlwaysRunUnlessHidden = v);
        Bool("General", "Auto open doors", p => p.AutoOpenDoors, (p, v) => p.AutoOpenDoors = v);
        Bool("General", "Smooth doors", p => p.SmoothDoors, (p, v) => p.SmoothDoors = v);
        Bool("General", "Auto open corpses", p => p.AutoOpenCorpses, (p, v) => p.AutoOpenCorpses = v);
        Bool("General", "Show mobiles' hits", p => p.ShowMobilesHP, (p, v) => p.ShowMobilesHP = v);
        Bool("General", "Highlight the poisoned", p => p.HighlightMobilesByPoisoned, (p, v) => p.HighlightMobilesByPoisoned = v);
        Bool("General", "Names overhead always on", p => p.NameOverheadToggled, (p, v) => p.NameOverheadToggled = v);

        Bool("Sound", "Sounds", p => p.EnableSound, (p, v) => { p.EnableSound = v; _audioChanged = true; });
        Int("Sound", "Sounds volume", 0, 100, p => p.SoundVolume, (p, v) => { p.SoundVolume = v; _audioChanged = true; });
        Bool("Sound", "Music", p => p.EnableMusic, (p, v) => { p.EnableMusic = v; _audioChanged = true; });
        Int("Sound", "Music volume", 0, 100, p => p.MusicVolume, (p, v) => { p.MusicVolume = v; _audioChanged = true; });
        Bool("Sound", "Footsteps", p => p.EnableFootstepsSound, (p, v) => p.EnableFootstepsSound = v);
        Bool("Sound", "Combat music", p => p.EnableCombatMusic, (p, v) => p.EnableCombatMusic = v);

        // "Hide roofs" is the classic box: checked means DrawRoofs off, and a
        // change redraws the roofs (OptionsGump.Apply).
        Bool("Video", "Hide roofs", p => !p.DrawRoofs, (p, v) =>
        {
            if (p.DrawRoofs == v)
            {
                p.DrawRoofs = !v;
                Client.Game.GetScene<GameScene>()?.UpdateMaxDrawZ(true);
            }
        });
        Bool("Video", "Trees to stumps", p => p.TreeToStumps, (p, v) =>
        {
            if (p.TreeToStumps != v)
            {
                StaticFilters.CleanTreeTextures();
                p.TreeToStumps = v;
            }
        });
        Bool("Video", "Hide vegetation", p => p.HideVegetation, (p, v) => p.HideVegetation = v);
        Bool("Video", "Circle of transparency", p => p.UseCircleOfTransparency, (p, v) => p.UseCircleOfTransparency = v);
        Bool("Video", "Shadows", p => p.ShadowsEnabled, (p, v) => p.ShadowsEnabled = v);
        Bool("Video", "Death screen", p => p.EnableDeathScreen, (p, v) => p.EnableDeathScreen = v);

        // Tooltip, Fonts and Speech: the classic pages' boxes, sliders and
        // colours (ModernHuePicker), with their ranges. Font pickers stay in
        // Classic view.
        Bool("Tooltip", "Use tooltips", p => p.UseTooltip, (p, v) => p.UseTooltip = v);
        Int("Tooltip", "Delay before display", 0, 1000, p => p.TooltipDelayBeforeDisplay, (p, v) => p.TooltipDelayBeforeDisplay = v);
        Int("Tooltip", "Tooltip zoom", 100, 200, p => p.TooltipDisplayZoom, (p, v) => p.TooltipDisplayZoom = v);
        Int("Tooltip", "Background opacity", 0, 100, p => p.TooltipBackgroundOpacity, (p, v) => p.TooltipBackgroundOpacity = v);
        Hue("Tooltip", "Tooltip text colour", p => p.TooltipTextHue, (p, v) => p.TooltipTextHue = v);

        Bool("Fonts", "Override the game font", p => p.OverrideAllFonts, (p, v) => p.OverrideAllFonts = v);
        Choice("Fonts", "Override with", new[] { "ASCII", "Unicode" }, p => p.OverrideAllFontsIsUnicode ? 1 : 0, (p, v) => p.OverrideAllFontsIsUnicode = v == 1);
        Bool("Fonts", "Force Unicode in the journal", p => p.ForceUnicodeJournal, (p, v) => p.ForceUnicodeJournal = v);

        Bool("Speech", "Scale speech delay by length", p => p.ScaleSpeechDelay, (p, v) => p.ScaleSpeechDelay = v);
        Int("Speech", "Speech delay", 0, 1000, p => p.SpeechDelay, (p, v) => p.SpeechDelay = v);
        Bool("Speech", "Save the journal to a file", p => p.SaveJournalToFile, (p, v) => p.SaveJournalToFile = v);
        // As the classic Apply: a change also switches the system chat's state.
        Bool("Speech", "Chat opens on Enter", p => p.ActivateChatAfterEnter, (p, v) =>
        {
            if (p.ActivateChatAfterEnter != v)
            {
                UIManager.SystemChat.IsActive = !v;
                p.ActivateChatAfterEnter = v;
            }
        });
        Bool("Speech", "Hide the chat gradient", p => p.HideChatGradient, (p, v) => p.HideChatGradient = v);
        Bool("Speech", "Ignore guild messages", p => p.IgnoreGuildMessages, (p, v) => p.IgnoreGuildMessages = v);
        Bool("Speech", "Ignore alliance messages", p => p.IgnoreAllianceMessages, (p, v) => p.IgnoreAllianceMessages = v);
        Bool("Speech", "Party messages overhead", p => p.OverheadPartyMessages, (p, v) => p.OverheadPartyMessages = v);
        Hue("Speech", "Speech colour", p => p.SpeechHue, (p, v) => p.SpeechHue = v);
        Hue("Speech", "Emote colour", p => p.EmoteHue, (p, v) => p.EmoteHue = v);
        Hue("Speech", "Yell colour", p => p.YellHue, (p, v) => p.YellHue = v);
        Hue("Speech", "Whisper colour", p => p.WhisperHue, (p, v) => p.WhisperHue = v);
        Hue("Speech", "Party message colour", p => p.PartyMessageHue, (p, v) => p.PartyMessageHue = v);
        Hue("Speech", "Guild message colour", p => p.GuildMessageHue, (p, v) => p.GuildMessageHue = v);
        Hue("Speech", "Alliance message colour", p => p.AllyMessageHue, (p, v) => p.AllyMessageHue = v);
        Hue("Speech", "Chat message colour", p => p.ChatMessageHue, (p, v) => p.ChatMessageHue = v);

        // Combat: its boxes, then the notoriety and spell colours.
        Bool("Combat", "Ask before a criminal act", p => p.EnabledCriminalActionQuery, (p, v) => p.EnabledCriminalActionQuery = v);
        Bool("Combat", "Ask before a criminal beneficial act", p => p.EnabledBeneficialCriminalActionQuery, (p, v) => p.EnabledBeneficialCriminalActionQuery = v);
        Bool("Combat", "Cast spells by one click", p => p.CastSpellsByOneClick, (p, v) => p.CastSpellsByOneClick = v);
        Bool("Combat", "Buff bar timers", p => p.BuffBarTime, (p, v) => p.BuffBarTime = v);
        Bool("Combat", "Fast spell assign", p => p.FastSpellsAssign, (p, v) => p.FastSpellsAssign = v);
        Bool("Combat", "Colour spells by kind", p => p.EnabledSpellHue, (p, v) => p.EnabledSpellHue = v);
        Bool("Combat", "Show DPS with damage", p => p.ShowDPSWithDamageNumbers, (p, v) => p.ShowDPSWithDamageNumbers = v);
        Hue("Combat", "Innocent colour", p => p.InnocentHue, (p, v) => p.InnocentHue = v);
        Hue("Combat", "Friend colour", p => p.FriendHue, (p, v) => p.FriendHue = v);
        Hue("Combat", "Criminal colour", p => p.CriminalHue, (p, v) => p.CriminalHue = v);
        Hue("Combat", "Can be attacked colour", p => p.CanAttackHue, (p, v) => p.CanAttackHue = v);
        Hue("Combat", "Enemy colour", p => p.EnemyHue, (p, v) => p.EnemyHue = v);
        Hue("Combat", "Murderer colour", p => p.MurdererHue, (p, v) => p.MurdererHue = v);
        Hue("Combat", "Beneficial spell colour", p => p.BeneficHue, (p, v) => p.BeneficHue = v);
        Hue("Combat", "Harmful spell colour", p => p.HarmfulHue, (p, v) => p.HarmfulHue = v);
        Hue("Combat", "Neutral spell colour", p => p.NeutralHue, (p, v) => p.NeutralHue = v);

        Bool("Containers", "Grid view", p => p.GridContainers, (p, v) => p.GridContainers = v);
        Int("Containers", "Grid slot size", GridContainerGump.MIN_SLOT, GridContainerGump.MAX_SLOT, p => p.GridContainerSlotSize, (p, v) => p.GridContainerSlotSize = v);

        Bool("Touch", "Vibrate when the command bar snaps", p => p.TouchVibrate, (p, v) => p.TouchVibrate = v);
        Bool("Touch", "Reduce motion", p => p.TouchReduceMotion, (p, v) => p.TouchReduceMotion = v);
        string[] flick = GumpFlick.ActionTitles;
        Choice("Touch", "Hold and flick up", flick, p => p.FlickUp, (p, v) => p.FlickUp = v);
        Choice("Touch", "Hold and flick down", flick, p => p.FlickDown, (p, v) => p.FlickDown = v);
        Choice("Touch", "Hold and flick left", flick, p => p.FlickLeft, (p, v) => p.FlickLeft = v);
        Choice("Touch", "Hold and flick right", flick, p => p.FlickRight, (p, v) => p.FlickRight = v);
        Action_("Touch", "Edit the command bar", () => { Close(); BarEditor.Open(0); });
    }

    // --- building ---------------------------------------------------------------------

    private static Label Lbl(string text, Color color, int scale = 1)
    {
        Label l = UoTheme.Label(text, color, scale);
        l.AutowrapMode = TextServer.AutowrapMode.Off;
        return l;
    }

    private static StyleBoxFlat Flat(Color c) => new() { BgColor = c, ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 2, ContentMarginBottom = 2 };

    /// <summary>A row the classic Options' way: text on the dark glass, the whole row the target.</summary>
    private static T Row<T>(T c) where T : Control
    {
        c.CustomMinimumSize = new Vector2(c.CustomMinimumSize.X, RowHeight);
        return c;
    }

    protected override void Build(PanelContainer card)
    {
        card.AddThemeColorOverride("font_color", Text);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 4);
        card.AddChild(outer);

        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 6);
        outer.AddChild(body);

        // The page column, as the classic's NiceButtons: text, the open one lit,
        // the rows touching (each is its whole target), so all ten fit a 1080p
        // phone at 3x (C13). Classic view sits in the footer, not under it. On
        // a shorter screen the column scrolls with a drag.
        var columnScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(112, 0),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        body.AddChild(columnScroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 0);
        columnScroll.AddChild(column);

        foreach (string page in Pages)
        {
            string p = page;
            var b = Row(new Button { Text = page, Alignment = HorizontalAlignment.Center });
            b.AddThemeStyleboxOverride("normal", Flat(Colors.Transparent));
            b.AddThemeStyleboxOverride("hover", Flat(Colors.Transparent));
            b.AddThemeStyleboxOverride("pressed", Flat(Selected));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.AddThemeColorOverride("font_color", Text);
            b.AddThemeColorOverride("font_hover_color", Text);
            b.AddThemeColorOverride("font_pressed_color", Colors.White);
            b.Pressed += () => ShowPage(p);
            column.AddChild(b);
            _pageButtons[page] = b;
        }

        body.AddChild(RuleLine(false));

        // The page: its name, a rule, and its rows, scrolling with a swipe.
        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 3);
        body.AddChild(content);
        _title = Lbl("", Text, 2);
        content.AddChild(_title);
        content.AddChild(RuleLine(true));
        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        content.AddChild(_scroll);
        var pages = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _scroll.AddChild(pages);

        foreach (string page in Pages)
        {
            var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            rows.AddThemeConstantOverride("separation", 2);
            pages.AddChild(rows);
            _pageRows[page] = rows;

            // Macros is a page of its own kind: the macro list and editor (ModernMacros).
            if (page == "Macros")
            {
                var scripts = UoTheme.Button("Scripts", 120);
                scripts.Pressed += () => { Close(); ModernScripts.Show(World); };
                rows.AddChild(scripts);
                _macros = new ModernMacros(World, Text, () => _macrosChanged = true);
                rows.AddChild(_macros);
                continue;
            }

            foreach (Setting s in _settings)
            {
                if (s.Page == page)
                {
                    rows.AddChild(BuildRow(s));
                }
            }
        }

        // The colour picker, shown in place of a page while a colour is chosen.
        _huePicker = new ModernHuePicker(Text) { Visible = false };
        pages.AddChild(_huePicker);

        // The classic window's own button row, pinned.
        outer.AddChild(RuleLine(true));
        _footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _footer.AddThemeConstantOverride("separation", 24);
        outer.AddChild(_footer);
        BuildFooter();
    }

    private HBoxContainer _footer;
    private bool _jewels;

    /// <summary>
    /// The footer: the classic's jewelled buttons once their gumps can be
    /// read (the client uploads a gump the first time it is asked for), the
    /// marble plates until then; rebuilt on open until the jewels are in.
    /// </summary>
    private void BuildFooter()
    {
        _jewels = UoTheme.GumpTexture(0x00F3) != null && UoTheme.GumpTexture(0x00F9) != null;

        foreach (Node n in _footer.GetChildren())
        {
            _footer.RemoveChild(n);
            n.QueueFree();
        }

        // Classic view first, apart from the classic's own four.
        Button classic = Row(UoTheme.Button("Classic view"));
        classic.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        classic.Pressed += OpenClassic;
        _footer.AddChild(classic);
        _footer.AddChild(RuleLine(false));
        _footer.AddChild(Jewel(0x00F3, 0x00F1, 0x00F2, "Cancel", Close));
        _footer.AddChild(Jewel(0x00EF, 0x00F0, 0x00EE, "Apply", Apply));
        _footer.AddChild(Jewel(0x00F6, 0x00F4, 0x00F5, "Default", Default));
        _footer.AddChild(Jewel(0x00F9, 0x00F8, 0x00F7, "Okay", () => { Apply(); Close(); }));
    }

    protected override void Refresh()
    {
        if (!_jewels)
        {
            BuildFooter();
        }
    }

    private static Control RuleLine(bool horizontal)
    {
        var r = new ColorRect { Color = Rule, MouseFilter = Control.MouseFilterEnum.Ignore };
        r.CustomMinimumSize = horizontal ? new Vector2(0, 1) : new Vector2(1, 0);
        return r;
    }

    /// <summary>One of the classic footer's jewelled buttons, from its own gumps.</summary>
    private static Control Jewel(ushort normal, ushort pressed, ushort over, string name, Action run)
    {
        Texture2D n = UoTheme.GumpTexture(normal);

        if (n == null)
        {
            Button fallback = UoTheme.Button(name, 64);
            fallback.Pressed += run;
            return fallback;
        }

        var b = new TextureButton
        {
            TextureNormal = n,
            TexturePressed = UoTheme.GumpTexture(pressed),
            TextureHover = UoTheme.GumpTexture(over),
            TooltipText = name,
            Name = name,
        };
        b.Pressed += run;

        // A thumb's target, around the jewel.
        var holder = new CenterContainer { CustomMinimumSize = new Vector2(n.GetWidth() + 12, RowHeight) };
        holder.AddChild(b);
        return holder;
    }

    private Control BuildRow(Setting s)
    {
        switch (s.Kind)
        {
            case Kind.Bool:
            {
                var box = Row(new CheckBox { Text = s.Label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                box.AddThemeColorOverride("font_color", Text);
                box.AddThemeColorOverride("font_hover_color", Text);
                box.AddThemeColorOverride("font_pressed_color", Text);
                box.AddThemeColorOverride("font_hover_pressed_color", Text);
                box.AddThemeColorOverride("font_focus_color", Text);
                box.Toggled += on => _values[s] = on;
                s.Show = v => box.SetPressedNoSignal((bool)v);
                box.SetMeta("setting", s.Label);
                return box;
            }

            case Kind.Int:
            {
                var row = Row(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                row.AddThemeConstantOverride("separation", 8);
                Label name = Lbl(s.Label, Text);
                name.CustomMinimumSize = new Vector2(110, 0);
                row.AddChild(name);
                var slider = new HSlider
                {
                    MinValue = s.Min, MaxValue = s.Max, Step = 1,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    CustomMinimumSize = new Vector2(0, 16),
                };
                slider.SetMeta("setting", s.Label);
                Label value = Lbl("", Text);
                value.CustomMinimumSize = new Vector2(28, 0);
                slider.ValueChanged += v => { _values[s] = (int)v; value.Text = ((int)v).ToString(); };
                s.Show = v => { slider.SetValueNoSignal((int)v); value.Text = ((int)v).ToString(); };
                row.AddChild(slider);
                row.AddChild(value);
                return row;
            }

            case Kind.Choice:
            {
                var row = Row(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                row.AddThemeConstantOverride("separation", 4);
                Label name = Lbl(s.Label, Text);
                name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(name);
                Label value = Lbl("", Text);
                value.CustomMinimumSize = new Vector2(150, 0);
                value.HorizontalAlignment = HorizontalAlignment.Center;
                Button prev = UoTheme.Button("‹", 30), next = UoTheme.Button("›", 30);
                prev.SetMeta("setting", s.Label + " prev");
                next.SetMeta("setting", s.Label + " next");
                int Current() => _values.TryGetValue(s, out object v) ? (int)v : 0;
                void Step(int d)
                {
                    int i = ((Current() + d) % s.Choices.Length + s.Choices.Length) % s.Choices.Length;
                    _values[s] = i;
                    value.Text = s.Choices[i];
                }
                prev.Pressed += () => Step(-1);
                next.Pressed += () => Step(1);
                s.Show = v => value.Text = s.Choices[Math.Clamp((int)v, 0, s.Choices.Length - 1)];
                row.AddChild(prev);
                row.AddChild(value);
                row.AddChild(next);
                return row;
            }

            case Kind.Hue:
            {
                var row = Row(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
                row.AddThemeConstantOverride("separation", 6);
                Label name = Lbl(s.Label, Text);
                name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(name);
                var swatch = new ColorRect { CustomMinimumSize = new Vector2(40, 16), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                row.AddChild(swatch);
                Label number = Lbl("", Text);
                number.CustomMinimumSize = new Vector2(40, 0);
                row.AddChild(number);
                Button change = UoTheme.Button("Change", 60);
                change.SetMeta("setting", s.Label);
                change.Pressed += () => PickHue(s);
                row.AddChild(change);
                s.Show = v => { swatch.Color = ModernHuePicker.ColourOf((ushort)(int)v); number.Text = ((int)v).ToString(); };
                return row;
            }

            default:
            {
                Button b = Row(UoTheme.Button(s.Label));
                b.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                b.Pressed += () => s.Run();
                b.SetMeta("setting", s.Label);
                return b;
            }
        }
    }

    // --- state ---------------------------------------------------------------------------

    protected override void OnOpen()
    {
        Profile p = ProfileManager.CurrentProfile;
        _values.Clear();

        if (!_jewels)
        {
            BuildFooter();
        }

        foreach (Setting s in _settings)
        {
            if (s.Get == null) continue;
            _values[s] = s.Get(p);
            s.Show?.Invoke(_values[s]);
        }

        ShowPage(_page);
    }

    /// <summary>A colour setting's Change: the picker in place of the page, then back to it.</summary>
    private void PickHue(Setting s)
    {
        int current = _values.TryGetValue(s, out object v) ? (int)v : 0;

        foreach (Control rows in _pageRows.Values)
        {
            rows.Visible = false;
        }

        _title.Text = $"{_page}: {s.Label}";
        _scroll.ScrollVertical = 0;
        _huePicker.Visible = true;
        _huePicker.Open(s.Label, (ushort)current, hue =>
        {
            _values[s] = (int)hue;
            s.Show?.Invoke((int)hue);
            ShowPage(_page);
        }, () => ShowPage(_page));
    }

    /// <summary>For the probe: the colour picker.</summary>
    public ModernHuePicker HuePicker => _huePicker;

    private void ShowPage(string page)
    {
        _page = page;
        _title.Text = page;

        foreach (KeyValuePair<string, Control> kv in _pageRows)
        {
            kv.Value.Visible = kv.Key == page;
        }

        if (_huePicker != null)
        {
            _huePicker.Visible = false;
        }

        foreach (KeyValuePair<string, Button> kv in _pageButtons)
        {
            kv.Value.AddThemeStyleboxOverride("normal", Flat(kv.Key == page ? Selected : Colors.Transparent));
        }

        _scroll.ScrollVertical = 0;

        if (page == "Macros")
        {
            _macros?.ShowList();
        }
    }

    private void Apply()
    {
        Profile p = ProfileManager.CurrentProfile;
        _audioChanged = false;

        foreach (Setting s in _settings)
        {
            if (s.Set != null && _values.TryGetValue(s, out object v) && !Equals(v, s.Get(p)))
            {
                s.Set(p, v);
            }
        }

        // As the classic Apply does for the sound page.
        if (_audioChanged && Client.Game?.Audio != null)
        {
            Client.Game.Audio.UpdateCurrentMusicVolume();
            Client.Game.Audio.UpdateCurrentSoundsVolume();

            if (!p.EnableMusic) Client.Game.Audio.StopMusic();
            if (!p.EnableSound) Client.Game.Audio.StopSounds();
        }

        // As the classic Apply does: the macros are edited live and saved here.
        if (_macrosChanged)
        {
            World.Macros.Save();
            _macrosChanged = false;
        }

        // As the classic Apply ends: the profile goes to disk now, not at the
        // next pause or logout, so a killed app keeps what was applied.
        p?.Save(World, ProfileManager.ProfilePath);

        GD.Print($"[GUO] modern: Options applied");
    }

    /// <summary>The page's covered settings back to a new profile's values (not written until Apply).</summary>
    private void Default()
    {
        var fresh = new Profile();

        foreach (Setting s in _settings)
        {
            if (s.Page == _page && s.Get != null)
            {
                _values[s] = s.Get(fresh);
                s.Show?.Invoke(_values[s]);
            }
        }
    }

    private void OpenClassic()
    {
        Close();
        ModernGumps.OpenClassicNext(typeof(OptionsGump));
        GameActions.OpenSettings(World);
    }

    // --- the probe ----------------------------------------------------------------------------

    /// <summary>For the probe: the Macros page.</summary>
    public ModernMacros Macros => _macros;

    /// <summary>For the probe: open a page by name.</summary>
    public void Page(string page) => ShowPage(page);

    /// <summary>For the probe: the control of a setting (by its words) or a footer button (by name), if on the open page.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("setting") && (string)c.GetMeta("setting") == what) || (c.HasMeta("macros") && (string)c.GetMeta("macros") == what)
                    || c.Name == what || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }
}
