// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The spellbook, Modern (ADR-0024, gump 6): the spells the book holds as a
/// grid of their own icons and names, a tap to cast, a hold to place a spell
/// button; Magery by circle. Classic's book pages icons 44 px apart and
/// casts on a double-click, neither of which suits a thumb.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. The book's spells are read from its
/// contents as SpellbookGump reads them (each child's Amount is a 1-based spell
/// index); the kind of book from its graphic, as SpellbookGump.AssignGraphic
/// does. A tap is GameActions.CastSpell; a hold creates the UseSpellButtonGump
/// the desktop's drag creates (replacing that spell's button, as it does).
/// Mastery books stay Classic: their pages hold more than spells.
/// </remarks>
internal sealed partial class ModernSpellbook : ModernGump
{
    private uint _serial;
    private SpellBookType _type;
    private Label _title;
    private VBoxContainer _list;
    private int _drawnHash;

    public ModernSpellbook(World world) : base(world) { }

    protected override float MaxArtWidth => 520f;

    public override bool Accept(Gump classic)
    {
        Item book = World.Items.Get(classic.LocalSerial);

        if (classic is not SpellbookGump || book == null)
        {
            return false;
        }

        SpellBookType? type = TypeOf(book);

        if (type == null)
        {
            return false;
        }

        _serial = classic.LocalSerial;
        _type = type.Value;

        return true;
    }

    /// <summary>The kind of book, as SpellbookGump.AssignGraphic decides it; null for a Mastery book.</summary>
    private SpellBookType? TypeOf(Item book)
    {
        bool samurai = (World.ClientFeatures.Flags & CharacterListFlags.CLF_SAMURAI_NINJA) != 0;

        return book.Graphic switch
        {
            0x2253 => SpellBookType.Necromancy,
            0x2252 => SpellBookType.Chivalry,
            0x238C => samurai ? SpellBookType.Bushido : SpellBookType.Magery,
            0x23A0 => samurai ? SpellBookType.Ninjitsu : SpellBookType.Magery,
            0x2D50 => SpellBookType.Spellweaving,
            0x2D9D => SpellBookType.Mysticism,
            0x225A or 0x225B => null,
            _ => SpellBookType.Magery,
        };
    }

    private SpellDefinition Spell(int index) => _type switch
    {
        SpellBookType.Necromancy => SpellsNecromancy.GetSpell(index),
        SpellBookType.Chivalry => SpellsChivalry.GetSpell(index),
        SpellBookType.Bushido => SpellsBushido.GetSpell(index),
        SpellBookType.Ninjitsu => SpellsNinjitsu.GetSpell(index),
        SpellBookType.Spellweaving => SpellsSpellweaving.GetSpell(index),
        SpellBookType.Mysticism => SpellsMysticism.GetSpell(index),
        _ => SpellsMagery.GetSpell(index),
    };

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        _title = UoTheme.Label("Spellbook", UoTheme.Heading, 2);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_title);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);
        col.AddChild(UoTheme.Label("Tap a spell to cast it. Hold one to place its button.", UoTheme.Muted));
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
        _title.Text = _type == SpellBookType.Magery ? "Spellbook" : _type.ToString();
        _drawnHash = 0;
        Refresh();
    }

    /// <summary>The spells the book holds, 1-based, read from its contents.</summary>
    private List<int> Held()
    {
        var held = new List<int>();
        Item book = World.Items.Get(_serial);

        if (book == null)
        {
            return held;
        }

        for (LinkedObject i = book.Items; i != null; i = i.Next)
        {
            int index = ((Item)i).Amount;

            if (index > 0 && Spell(index) is SpellDefinition def && def.ID > 0 && !held.Contains(index))
            {
                held.Add(index);
            }
        }

        held.Sort();
        return held;
    }

    /// <summary>Tiles whose icon was not readable yet (the client uploads a gump the first time it is asked for).</summary>
    private readonly List<(Button tile, ushort icon)> _iconless = new();

    protected override void Refresh()
    {
        if (World.Items.Get(_serial) == null)
        {
            Close();
            return;
        }

        for (int i = _iconless.Count - 1; i >= 0; i--)
        {
            if (UoTheme.GumpTexture(_iconless[i].icon) is Texture2D icon)
            {
                _iconless[i].tile.Icon = icon;
                _iconless.RemoveAt(i);
            }
        }

        List<int> held = Held();
        int hash = string.Join(",", held).GetHashCode();

        if (hash == _drawnHash)
        {
            return;
        }

        _drawnHash = hash;
        _iconless.Clear();

        foreach (Node n in _list.GetChildren())
        {
            _list.RemoveChild(n);
            n.QueueFree();
        }

        if (held.Count == 0)
        {
            _list.AddChild(UoTheme.Label("This book holds no spells yet.", UoTheme.Muted));
            return;
        }

        // Magery by circle, eight spells to a circle; the others in one grid.
        GridContainer grid = null;
        int circle = -1;

        foreach (int index in held)
        {
            int c = _type == SpellBookType.Magery ? (index - 1) / 8 : 0;

            if (grid == null || c != circle)
            {
                circle = c;

                if (_type == SpellBookType.Magery && c < SpellsMagery.CircleNames.Length)
                {
                    _list.AddChild(UoTheme.Label(SpellsMagery.CircleNames[c], UoTheme.Heading));
                }

                grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                grid.AddThemeConstantOverride("h_separation", 4);
                grid.AddThemeConstantOverride("v_separation", 4);
                _list.AddChild(grid);
            }

            grid.AddChild(Tile(Spell(index)));
        }
    }

    /// <summary>A spell: its own icon and its name, the whole of it the target.</summary>
    private Control Tile(SpellDefinition def)
    {
        var tile = new Button
        {
            Text = def.Name,
            Icon = UoTheme.GumpTexture((ushort)def.GumpIconID),
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 48),
            ClipText = true,
            ExpandIcon = false,
        };

        StyleBoxFlat flat = new() { BgColor = Colors.Transparent, ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2 };
        tile.AddThemeStyleboxOverride("normal", flat);
        tile.AddThemeStyleboxOverride("hover", flat);
        tile.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = new Color(UoTheme.Gold, 0.35f), ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2 });
        tile.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        tile.AddThemeColorOverride("font_color", UoTheme.Ink);
        tile.AddThemeColorOverride("font_hover_color", UoTheme.Ink);
        tile.AddThemeColorOverride("font_pressed_color", UoTheme.Heading);
        tile.SetMeta("spell", def.Name);
        tile.SetMeta("spellId", def.ID);
        tile.Pressed += () => { Close(); GameActions.CastSpell(def.ID); };

        if (tile.Icon == null)
        {
            _iconless.Add((tile, (ushort)def.GumpIconID));
        }

        return tile;
    }

    /// <summary>A hold on a spell: its button, placed as the desktop's drag places it.</summary>
    protected override void OnHold(Control under)
    {
        for (Node n = under; n != null; n = n.GetParent())
        {
            if (n is Control c && c.HasMeta("spellId"))
            {
                int id = (int)c.GetMeta("spellId");
                SpellDefinition def = FindById(id);

                if (def == null)
                {
                    return;
                }

                foreach (Gump g in UIManager.Gumps)
                {
                    if (g is UseSpellButtonGump b && b.SpellID == id)
                    {
                        b.Dispose();
                    }
                }

                // Where the classic drag would drop it: at the finger, here the
                // middle of the screen, which the player then moves.
                Compat.Rectangle screen = Client.Game.ClientBounds;
                UIManager.Add(new UseSpellButtonGump(World, def) { X = screen.Width / 2 - 22, Y = screen.Height / 3 - 22 });
                TouchInput.Note($"modern: spell button placed for {def.Name}");
                Close();

                return;
            }
        }
    }

    private SpellDefinition FindById(int id)
    {
        foreach (int index in Held())
        {
            if (Spell(index) is SpellDefinition def && def.ID == id)
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>For the probe: a spell's tile by its name, or a button by its text.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("spell") && (string)c.GetMeta("spell") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }
}
