// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Utility;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The abilities book, Modern (ADR-0024, gump index 6): the two abilities of
/// the weapon in hand as large tiles (tap to use, hold to place its button),
/// then every ability with its icon, what it does and the weapons that have
/// it. Classic's book pages a 406 × 229 spread one ability at a time, and
/// uses one on a double-click of a 44 px icon.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. A tap is GameActions.UsePrimaryAbility
/// or UseSecondaryAbility, as CombatBookGump's double-click; a hold makes the
/// UseAbilityButtonGump its drag makes, replacing that ability's button as it
/// does. The number of abilities follows the client version as the book's
/// does; each one's text is the book's tooltip cliloc (1061693 + i), and its
/// weapons come from the book's own table (CombatBookGump.GetItemsList, read
/// from the instance being opened, so the ported file is untouched; without it
/// the weapons are left out).
/// </remarks>
internal sealed partial class ModernAbilities : ModernGump
{
    private readonly TextureButton[] _current = new TextureButton[2];
    private readonly Label[] _currentName = new Label[2];
    private VBoxContainer _list;
    private List<ushort>[] _weapons;
    private readonly List<(TextureRect rect, ushort icon)> _iconless = new();

    public ModernAbilities(World world) : base(world) { }

    protected override float MaxArtWidth => 520f;

    private static int Count
    {
        get
        {
            if (Client.Game.UO.Version < ClientVersion.CV_7000)
            {
                return Client.Game.UO.Version < ClientVersion.CV_500A ? 29 : 13;
            }

            return Constants.MAX_ABILITIES_COUNT;
        }
    }

    public override bool Accept(Gump classic)
    {
        if (classic is not CombatBookGump || World.Player == null)
        {
            return false;
        }

        // The book's own table of weapons per ability, read once: the private
        // method CombatBookGump.GetItemsList(byte index) -> List<ushort>. Fail
        // safe: if upstream renames or reshapes it, or it throws, the
        // abilities are listed without their weapons.
        if (_weapons == null)
        {
            _weapons = new List<ushort>[Count];

            try
            {
                System.Reflection.MethodInfo list = typeof(CombatBookGump).GetMethod("GetItemsList",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(byte) }, null);

                if (list != null && list.ReturnType == typeof(List<ushort>))
                {
                    for (int i = 0; i < Count; i++)
                    {
                        _weapons[i] = list.Invoke(classic, new object[] { (byte)i }) as List<ushort>;
                    }
                }
                else
                {
                    GD.Print("[GUO] modern: abilities book -- CombatBookGump.GetItemsList(byte) not found; weapons left out");
                }
            }
            catch (Exception e)
            {
                _weapons = new List<ushort>[Count];
                GD.Print($"[GUO] modern: abilities book -- reading the weapons failed ({e.GetType().Name}); weapons left out");
            }
        }

        return true;
    }

    // The UO font's lines are far apart for its glyphs; wrapped lines closer.
    private const int WrapSpacing = -6;

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        header.AddThemeConstantOverride("separation", 8);
        header.AddChild(UoTheme.Label("Abilities", UoTheme.Heading, 2));

        // The hint beside the title, not a line of its own: on a landscape
        // phone every line here is a list row fewer (C14).
        Label hint = UoTheme.Label("Tap to use, hold to place a button.", UoTheme.Muted);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(hint);
        Button classic = UoTheme.Button("Classic view", 90);
        classic.Pressed += OpenClassic;
        header.AddChild(classic);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);

        var now = new HBoxContainer();
        now.AddThemeConstantOverride("separation", 8);
        col.AddChild(now);

        for (int i = 0; i < 2; i++)
        {
            bool primary = i == 0;
            // A tile: the big icon, its name and role beside it, one icon tall.
            var tile = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            tile.AddThemeConstantOverride("separation", 6);
            now.AddChild(tile);
            var icon = new TextureButton { StretchMode = TextureButton.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(48, 48), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            icon.SetMeta("ability", primary ? "primary" : "secondary");
            icon.Pressed += () => { Close(); if (primary) GameActions.UsePrimaryAbility(World); else GameActions.UseSecondaryAbility(World); };
            tile.AddChild(icon);
            var words = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            words.AddThemeConstantOverride("separation", 0);
            tile.AddChild(words);
            Label name = UoTheme.Label("", UoTheme.Ink);
            words.AddChild(name);
            Label which = UoTheme.Label(primary ? "Primary" : "Secondary", UoTheme.Muted);
            words.AddChild(which);
            _current[i] = icon;
            _currentName[i] = name;
        }

        col.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        col.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_list);
    }

    protected override void OnOpen()
    {
        foreach (Node n in _list.GetChildren())
        {
            _list.RemoveChild(n);
            n.QueueFree();
        }

        _iconless.Clear();

        for (int i = 0; i < Count && i < AbilityData.Abilities.Length; i++)
        {
            _list.AddChild(Row(i));
        }

        Refresh();
    }

    private Control Row(int i)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        ushort iconId = (ushort)(0x5200 + i);
        var icon = new TextureRect { Texture = UoTheme.GumpTexture(iconId), StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(44, 44), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };

        if (icon.Texture == null)
        {
            _iconless.Add((icon, iconId));
        }

        row.AddChild(icon);

        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        row.AddChild(text);
        text.AddChild(UoTheme.Label(StringHelper.CapitalizeAllWords(AbilityData.Abilities[i].Name), UoTheme.Heading));

        string about = Client.Game.UO.FileManager.Clilocs.GetString(1061693 + i);

        if (!string.IsNullOrEmpty(about))
        {
            Label l = UoTheme.Label(about, UoTheme.Ink);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.AddThemeConstantOverride("line_spacing", WrapSpacing);
            text.AddChild(l);
        }

        if (_weapons != null && i < _weapons.Length && _weapons[i] is List<ushort> weapons && weapons.Count > 0)
        {
            var names = new List<string>();
            var statics = Client.Game.UO.FileManager.TileData.StaticData;

            foreach (ushort id in weapons)
            {
                if (id < statics.Length)
                {
                    names.Add(StringHelper.CapitalizeAllWords(statics[id].Name));
                }
            }

            Label w = UoTheme.Label(string.Join(", ", names), UoTheme.Muted);
            w.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            w.AddThemeConstantOverride("line_spacing", WrapSpacing);
            text.AddChild(w);
        }

        return row;
    }

    protected override void Refresh()
    {
        if (World.Player == null)
        {
            return;
        }

        for (int i = _iconless.Count - 1; i >= 0; i--)
        {
            if (UoTheme.GumpTexture(_iconless[i].icon) is Texture2D t)
            {
                _iconless[i].rect.Texture = t;
                _iconless.RemoveAt(i);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            byte index = (byte)World.Player.Abilities[i];
            int at = (index & 0x7F) - 1;

            if (at < 0 || at >= AbilityData.Abilities.Length)
            {
                _currentName[i].Text = "None";
                _current[i].TextureNormal = null;
                continue;
            }

            AbilityDefinition def = AbilityData.Abilities[at];
            _currentName[i].Text = StringHelper.CapitalizeAllWords(def.Name);
            _current[i].TextureNormal = UoTheme.GumpTexture(def.Icon);
            // Armed (the high bit), as the book hues it: the icon lit in the heading colour.
            _current[i].Modulate = (index & 0x80) != 0 ? new Color(1f, 0.55f, 0.45f) : Colors.White;
        }
    }

    /// <summary>A hold on one of the weapon's abilities: its button, as the book's drag makes it.</summary>
    protected override void OnHold(Control under)
    {
        if (under == null || !under.HasMeta("ability") || World.Player == null)
        {
            return;
        }

        bool primary = (string)under.GetMeta("ability") == "primary";
        int index = (byte)World.Player.Abilities[primary ? 0 : 1] & 0x7F;

        if (index <= 0)
        {
            return;
        }

        foreach (Gump g in UIManager.Gumps)
        {
            if (g is UseAbilityButtonGump b && b.Index == AbilityData.Abilities[index - 1].Index)
            {
                b.Dispose();
            }
        }

        // Where the drag would drop it: at the finger; here the middle of the screen, for the player to move.
        Compat.Rectangle screen = Client.Game.ClientBounds;
        UIManager.Add(new UseAbilityButtonGump(World, primary) { X = screen.Width / 2 - 22, Y = screen.Height / 3 - 22 });
        TouchInput.Note($"modern: ability button placed ({(primary ? "primary" : "secondary")})");
        Close();
    }

    private void OpenClassic()
    {
        Close();
        ModernGumps.OpenClassicNext(typeof(CombatBookGump));
        GameActions.OpenAbilitiesBook(World);
    }

    /// <summary>For the probe: a control by its tag or text.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("ability") && (string)c.GetMeta("ability") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>For the probe: the rows listed.</summary>
    public int Rows => _list.GetChildCount();

    /// <summary>For the probe: abilities with their weapons read from the book's table.</summary>
    public int WithWeapons
    {
        get
        {
            int n = 0;
            foreach (List<ushort> w in _weapons ?? Array.Empty<List<ushort>>()) if (w != null && w.Count > 0) n++;
            return n;
        }
    }
}
