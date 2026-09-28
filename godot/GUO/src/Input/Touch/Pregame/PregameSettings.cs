// GUO addition, not a port: upstream ClassicUO has no second screen (ADR-0009).

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps.Login;
using GUO.Input.Glyphs;
using GUO.Platform.Android;
using GUO.Resources;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// The pre-game card's Settings tab: only what exists before login, the
/// global settings (settings.json) and the second screen's. The groups are a
/// list on the left and their fields on parchment on the right, the same two
/// panes as Servers. What belongs to a character's profile is named, once per
/// group, as "Set in Options once you're in the world".
/// </summary>
/// <remarks>
/// The login gump on the top screen has boxes of its own for three of these
/// (save account, auto-login, login music and its volume), and writes its
/// boxes back to the settings when the player logs in. A change here goes
/// through that gump's box while it is up, so the two never disagree.
/// </remarks>
internal sealed partial class PregameSettings : HBoxContainer
{
    public static readonly string[] Groups = { "Your UO files", "Account", "Screen", "Second screen", "Controls", "Sound", "About" };

    /// <summary>The groups' captions on a narrow card (the Thor's lower screen at the shelf's scale, 310 art px).</summary>
    private static readonly string[] Short = { "UO files", "Account", "Screen", "Shelf", "Controls", "Sound", "About" };

    /// <summary>Below this many art pixels wide the card is narrow: short captions, a tighter page.</summary>
    public const int NarrowBelow = 400;

    /// <summary>Font 1's line box is tall for running text; wrapped lines sit this much closer.</summary>
    private const int LineSpacing = -6;

    private const string ProfileNote = "Set in Options once you're in the world: ";

    private readonly Dictionary<string, Button> _groupButtons = new();
    private readonly List<Action> _refreshers = new();
    private readonly List<HSlider> _sliders = new();
    private ScrollContainer _scroll;
    private VBoxContainer _fields;
    private string _group = Groups[0];
    private Label _reportNote;
    private PanelContainer _page;
    private bool _narrow;

    public PregameSettings()
    {
        AddThemeConstantOverride("separation", 6);

        var list = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 3);
        AddChild(list);

        foreach (string g in Groups)
        {
            Button b = UoTheme.Button(g, 104);
            b.Alignment = HorizontalAlignment.Left;
            string group = g;
            b.Pressed += () => ShowGroup(group);
            list.AddChild(b);
            _groupButtons[g] = b;
        }

        var page = _page = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(page);

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        page.AddChild(_scroll);

        _fields = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _fields.AddThemeConstantOverride("separation", 4);
        _scroll.AddChild(_fields);

        SetNarrow(false, force: true);
    }

    /// <summary>
    /// The card's width in art pixels decides the layout: on a narrow card the
    /// groups take short captions and the page a tighter margin, so the fields
    /// keep about 190 art pixels.
    /// </summary>
    public void SetNarrow(bool narrow, bool force = false)
    {
        if (narrow == _narrow && !force)
        {
            return;
        }

        _narrow = narrow;
        AddThemeConstantOverride("separation", narrow ? 4 : 6);
        _page.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, narrow ? 5 : 8));

        for (int i = 0; i < Groups.Length; i++)
        {
            Button b = _groupButtons[Groups[i]];
            b.Text = narrow ? Short[i] : Groups[i];
            b.CustomMinimumSize = new Vector2(narrow ? 0 : 104, UoTheme.ButtonHeight);
        }

        ShowGroup(_group);
    }

    public bool Narrow => _narrow;

    /// <summary>The group shown now.</summary>
    public string Group => _group;

    /// <summary>For the probe: the group buttons by name.</summary>
    public Button GroupButton(string group) => _groupButtons[group];

    /// <summary>For the probe: the controls on the page shown now.</summary>
    public IEnumerable<Control> Fields => _fields.GetChildren().OfType<Control>().SelectMany(Flatten);

    private static IEnumerable<Control> Flatten(Control c) => new[] { c }.Concat(c.GetChildren().OfType<Control>().SelectMany(Flatten));

    /// <summary>For the probe: the page's text, joined.</summary>
    public string Text => string.Join(" | ", Fields.Select(c => c switch
    {
        Label l => l.Text,
        Button b => b.Text,
        _ => null,
    }).Where(t => !string.IsNullOrEmpty(t)));

    public void ShowGroup(string group)
    {
        _group = group;

        foreach ((string g, Button b) in _groupButtons)
        {
            bool on = g == group;
            b.AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            b.AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
            b.AddThemeColorOverride("font_hover_color", on ? UoTheme.Heading : UoTheme.Ink);
        }

        foreach (Node n in _fields.GetChildren())
        {
            _fields.RemoveChild(n);
            n.QueueFree();
        }

        _refreshers.Clear();
        _sliders.Clear();
        _reportNote = null;
        _scroll.ScrollVertical = 0;

        switch (group)
        {
            case "Your UO files": UoFiles(); break;
            case "Account": Account(); break;
            case "Screen": Screen(); break;
            case "Second screen": SecondScreen(); break;
            case "Controls": Controls(); break;
            case "Sound": Sound(); break;
            case "About": About(); break;
        }

        Refresh();
        GD.Print($"[GUO] pregame card: settings, {group}");
    }

    /// <summary>The values in force back into the fields (a change made on the login gump or in Options).</summary>
    public void Refresh()
    {
        foreach (Action a in _refreshers)
        {
            a();
        }
    }

    public void ScrollBy(float artPixels) => _scroll.ScrollVertical += (int) artPixels;

    /// <summary>Whether a finger at <paramref name="at"/> (viewport pixels) is on a slider.</summary>
    public bool SliderAt(Vector2 at) => _sliders.Any(s => s.IsVisibleInTree() && s.GetGlobalRect().Grow(4).HasPoint(at));

    // --- the groups ----------------------------------------------------------------

    private void UoFiles()
    {
        Settings s = Settings.GlobalSettings;
        Value("Folder", () => s.UltimaOnlineDirectory, fromLeft: true);
        Value("Client", () => string.IsNullOrWhiteSpace(s.ClientVersion) ? "Found from the files" : s.ClientVersion);
        Value("State", () => Client.Game?.UO?.FileManager != null ? "Loaded, in use now" : "Not loaded");
        Act("Choose folder...", () => GUO.Host.FirstRunScreen.OpenChange());
        Note("The folder picker opens on the top screen. A new folder applies the next time GUO starts.");
    }

    private void Account()
    {
        Settings s = Settings.GlobalSettings;
        Check("Save the account name", () => s.SaveAccount, v => LoginBox(ResGumps.SaveAccount, v, () => s.SaveAccount = v));
        Check("Log in automatically", () => s.AutoLogin, v => LoginBox(ResGumps.Autologin, v, () => s.AutoLogin = v));
        Check("Reconnect when the connection drops", () => s.Reconnect, v => s.Reconnect = v);
        Value("Last server", () => string.IsNullOrWhiteSpace(s.LastServerName) ? "None yet" : s.LastServerName);
        Note("The account name and password are typed on the login gump.");
    }

    private void Screen()
    {
        Act("Screen effects...", GUO.Renderer.PostFx.PostFxMenu.Toggle);
        Note("The look of the world: the effects menu opens on the top screen, with the world behind it as its preview.");
        Note(ProfileNote + "the game window's size, zoom and gump scale.");
    }

    private void SecondScreen()
    {
        Check("Shelve gumps there in the world", () => DualScreenSettings.Current.Enabled, v => DualScreenSettings.Edit(x => x.Enabled = v));
        Heading("Shelve when opened");
        Check("Paperdoll", () => DualScreenSettings.Current.Paperdoll, v => DualScreenSettings.Edit(x => x.Paperdoll = v));
        Check("Status bar", () => DualScreenSettings.Current.Status, v => DualScreenSettings.Edit(x => x.Status = v));
        Check("Backpack", () => DualScreenSettings.Current.Backpack, v => DualScreenSettings.Edit(x => x.Backpack = v));
        Check("Journal", () => DualScreenSettings.Current.Journal, v => DualScreenSettings.Edit(x => x.Journal = v));
        Check("Other gumps: skills, spellbook, containers", () => DualScreenSettings.Current.Others, v => DualScreenSettings.Edit(x => x.Others = v));

        Heading("Shelf scale");
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 4);
        row.AddThemeConstantOverride("v_separation", 3);
        _fields.AddChild(row);

        for (int i = 0; i <= DualScreenSettings.MaxScale; i++)
        {
            int scale = i;
            Button b = UoTheme.Button(i == 0 ? "As main" : $"{i}x", 44);
            b.Pressed += () => DualScreenSettings.Edit(x => x.Scale = scale);
            row.AddChild(b);
            _refreshers.Add(() =>
            {
                bool on = DualScreenSettings.Current.Scale == scale;
                b.AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
                b.AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
            });
        }

        Note(ProfileNote + "companion tabs on the second screen.");
    }

    private void Controls()
    {
        Value("Using now", () => InputMode.Current switch
        {
            InputKind.Gamepad => InputMode.PadFamily switch
            {
                PadFamily.PlayStation => "A PlayStation pad",
                PadFamily.Nintendo => "A Nintendo pad",
                PadFamily.SteamDeck => "The Steam Deck's controls",
                PadFamily.Xbox => "An Xbox pad",
                _ => "A controller",
            },
            InputKind.Touch => "Touch",
            _ => "Keyboard and mouse",
        });

        Heading("Controller buttons");

        foreach ((PadAction action, string words) in GlyphPic.Legend)
        {
            var pair = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            pair.AddThemeConstantOverride("separation", 6);
            var glyph = new TextureRect
            {
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                CustomMinimumSize = new Vector2(InputGlyphs.Size, InputGlyphs.Size),
                TextureFilter = TextureFilterEnum.Nearest,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            pair.AddChild(glyph);
            Label w = UoTheme.Label(words, UoTheme.Ink);
            w.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            w.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            w.AddThemeConstantOverride("line_spacing", LineSpacing);
            pair.AddChild(w);
            _fields.AddChild(pair);
            PadAction a = action;
            _refreshers.Add(() => glyph.Texture = InputGlyphs.Pad(a));
        }

        Note(ProfileNote + "the controller on or off, its button labels, and the touch controls.");
    }

    private void Sound()
    {
        Settings s = Settings.GlobalSettings;
        Check("Login music", () => s.LoginMusic, v => LoginBox("Music", v, () =>
        {
            s.LoginMusic = v;
            Client.Game.Audio.UpdateCurrentMusicVolume(true);
        }));

        Heading("Login music volume");
        var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 16) };
        bool syncing = false;
        slider.ValueChanged += v =>
        {
            if (syncing)
            {
                return;
            }

            LoginSlider((int) v, () =>
            {
                s.LoginMusicVolume = (int) v;
                Client.Game.Audio.UpdateCurrentMusicVolume(true);
            });
            s.Save();
        };
        _fields.AddChild(slider);
        _sliders.Add(slider);
        _refreshers.Add(() =>
        {
            syncing = true;
            slider.Value = s.LoginMusicVolume;
            slider.Editable = s.LoginMusic;
            syncing = false;
        });

        Note(ProfileNote + "the game's sound, music and their volumes.");
    }

    private void About()
    {
        Value("GUO", () => CUOEnviroment.Version);
        Value("UO data", () => string.IsNullOrWhiteSpace(Settings.GlobalSettings.ClientVersion) ? "Found from the files" : Settings.GlobalSettings.ClientVersion);
        Note("GUO is ClassicUO on Godot, under its BSD licence. The button glyphs are Kenney's Input Prompts Pixel (CC0). Ultima Online's art is your own install and is never included.");
        Act("Report a problem", () =>
        {
            string log = ProjectSettings.GlobalizePath("user://logs/godot.log");
            DisplayServer.ClipboardSet(log);

            if (_reportNote != null)
            {
                _reportNote.Text = "The log's location is on the clipboard. Attach that file to your report.";
            }

            GD.Print("[GUO] pregame card: log path copied");
        });
        _reportNote = Note("Copies where GUO's log is, to attach to a report.");
    }

    // --- fields -----------------------------------------------------------------------

    private Label Heading(string text)
    {
        Label l = UoTheme.Label(text, UoTheme.Heading);
        _fields.AddChild(l);
        return l;
    }

    private Label Note(string text)
    {
        Label l = UoTheme.Label(text, UoTheme.Muted);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.AddThemeConstantOverride("line_spacing", LineSpacing);
        _fields.AddChild(l);
        return l;
    }

    /// <param name="fromLeft">A path: when it is too long, keep its end ("...\Ultima Online") rather than its start.</param>
    private void Value(string key, Func<string> get, bool fromLeft = false)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        Label k = UoTheme.Label(key, UoTheme.Muted);
        k.CustomMinimumSize = new Vector2(_narrow ? 48 : 64, 0);
        Label v = UoTheme.Label("", UoTheme.Ink);
        v.ClipText = true;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(k);
        row.AddChild(v);
        _fields.AddChild(row);
        void Show() => v.Text = fromLeft ? FitLeft(get() ?? "", v.Size.X) : get() ?? "";
        _refreshers.Add(Show);

        if (fromLeft)
        {
            v.Resized += Show;
        }
    }

    private void Check(string text, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox
        {
            Text = text,
            FocusMode = FocusModeEnum.All,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        box.AddThemeConstantOverride("line_spacing", LineSpacing);
        bool syncing = false;
        box.Toggled += v =>
        {
            if (syncing)
            {
                return;
            }

            set(v);
            Settings.GlobalSettings.Save();
            GD.Print($"[GUO] pregame card: \"{text}\" {(v ? "on" : "off")}");
        };
        _fields.AddChild(box);
        _refreshers.Add(() =>
        {
            syncing = true;
            box.ButtonPressed = get();
            syncing = false;
        });
    }

    /// <summary>The end of <paramref name="text"/> that fits <paramref name="width"/>, after "..." (font 1 has no ellipsis glyph).</summary>
    private static string FitLeft(string text, float width)
    {
        Font font = UoTheme.Font;

        if (width <= 0 || font == null || font.GetStringSize(text, HorizontalAlignment.Left, -1, UoTheme.FontSize).X <= width)
        {
            return text;
        }

        for (int i = 1; i < text.Length; i++)
        {
            string cut = "..." + text[i..];

            if (font.GetStringSize(cut, HorizontalAlignment.Left, -1, UoTheme.FontSize).X <= width)
            {
                return cut;
            }
        }

        return text;
    }

    private void Act(string text, Action run)
    {
        Button b = UoTheme.Button(text, 96);
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        b.Pressed += run;
        _fields.AddChild(b);
    }

    // --- the login gump's own boxes -------------------------------------------------------

    /// <summary>
    /// A setting that the login gump also has a box for: through that box while
    /// the gump is up (its own handler writes the setting, and it keeps the
    /// value it logs in with), straight into the settings otherwise.
    /// </summary>
    private static void LoginBox(string text, bool value, Action direct)
    {
        Game.UI.Controls.Checkbox box = LoginControls().OfType<Game.UI.Controls.Checkbox>().FirstOrDefault(c => c.Text == text);

        if (box != null)
        {
            box.IsChecked = value;
        }

        direct();
    }

    private static void LoginSlider(int value, Action direct)
    {
        Game.UI.Controls.HSliderBar bar = LoginControls().OfType<Game.UI.Controls.HSliderBar>().FirstOrDefault();

        if (bar != null)
        {
            bar.Value = value;
        }

        direct();
    }

    private static IEnumerable<Game.UI.Controls.Control> LoginControls()
    {
        LoginGump login = UIManager.GetGump<LoginGump>();

        return login == null || login.IsDisposed ? Enumerable.Empty<Game.UI.Controls.Control>() : All(login);
    }

    private static IEnumerable<Game.UI.Controls.Control> All(Game.UI.Controls.Control c) => c.Children.SelectMany(x => new[] { x }.Concat(All(x)));
}
