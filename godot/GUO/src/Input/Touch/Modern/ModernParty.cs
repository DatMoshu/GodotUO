// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Network;
using GUO.Resources;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The party gump, Modern (ADR-0024, gump 2): who is in the party with their
/// hits, a tap to tell one of them or the whole party, and the leader's add
/// and remove, in one column a thumb can use. Classic's PartyGump is 450 × 480
/// and did not fit a handheld at a usable size (docs/ui/tall_gumps.md).
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. Every action is the classic
/// gump's own call (PartyGump.OnButtonClick): the invite and remove requests,
/// GameActions.RequestPartyQuit, the loot flag sent on Okay, and "/" or "/N "
/// put on the say line. A tell also opens the soft keyboard, as the command
/// bar's Chat does, since a phone has no keys to type the message with.
/// </remarks>
internal sealed partial class ModernParty : ModernGump
{
    private const int RowHeight = 26;
    private const int Slots = 10;

    private Label _status;
    private CheckBox _loot;
    private Button _add, _leave, _tellAll;
    private readonly HBoxContainer[] _rows = new HBoxContainer[Slots];
    private readonly Label[] _names = new Label[Slots];
    private readonly ProgressBar[] _hits = new ProgressBar[Slots];
    private readonly Button[] _tell = new Button[Slots];
    private readonly Button[] _kick = new Button[Slots];
    private bool _canLoot;

    public ModernParty(World world) : base(world) { }

    protected override float MaxArtWidth => 420f;

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        Label title = UoTheme.Label("Party", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);

        _status = UoTheme.Label("", UoTheme.Muted);
        col.AddChild(_status);
        col.AddChild(new HSeparator());

        // The members, on parchment, scrolling if the screen is short.
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        col.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(list);

        for (int i = 0; i < Slots; i++)
        {
            int index = i;
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight) };
            row.AddThemeConstantOverride("separation", 6);
            list.AddChild(row);
            _rows[i] = row;

            Label name = UoTheme.Label("", UoTheme.Ink);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.ClipText = true;
            row.AddChild(name);
            _names[i] = name;

            var hits = new ProgressBar { CustomMinimumSize = new Vector2(70, 10), ShowPercentage = false, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            hits.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("3a342c"), BorderColor = UoTheme.Ink, BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1 });
            hits.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("b8483e") });
            row.AddChild(hits);
            _hits[i] = hits;

            Button tell = UoTheme.Button("Tell", 44);
            tell.Pressed += () => Tell($"/{index + 1} ");
            tell.SetMeta("party", $"tell {index}");
            row.AddChild(tell);
            _tell[i] = tell;

            Button kick = UoTheme.Button("Remove", 60);
            kick.Pressed += () => Remove(index);
            kick.SetMeta("party", $"remove {index}");
            row.AddChild(kick);
            _kick[i] = kick;
        }

        _loot = new CheckBox { Text = "Party members can loot me", CustomMinimumSize = new Vector2(0, RowHeight) };
        _loot.Toggled += on => _canLoot = on;
        col.AddChild(_loot);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 4);
        col.AddChild(actions);
        _add = UoTheme.Button("Add member", 90);
        _add.Pressed += () => NetClient.Socket.Send_PartyInviteRequest();
        actions.AddChild(_add);
        _tellAll = UoTheme.Button("Tell party", 80);
        _tellAll.Pressed += () => Tell("/");
        actions.AddChild(_tellAll);
        _leave = UoTheme.Button("Leave party", 90);
        _leave.Pressed += Leave;
        actions.AddChild(_leave);

        col.AddChild(new HSeparator());
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        footer.AddThemeConstantOverride("separation", 4);
        col.AddChild(footer);
        Button cancel = UoTheme.Button("Cancel", 70);
        cancel.Pressed += Close;
        footer.AddChild(cancel);
        Button okay = UoTheme.Button("Okay", 70);
        okay.AddThemeColorOverride("font_color", UoTheme.Heading);
        okay.Pressed += Okay;
        footer.AddChild(okay);
    }

    protected override void OnOpen()
    {
        _canLoot = World.Party.CanLoot;
        _loot.SetPressedNoSignal(_canLoot);
        Refresh();
    }

    protected override void Refresh()
    {
        PartyManager party = World.Party;
        bool inParty = party.Leader != 0;
        bool leader = party.Leader == 0 || party.Leader == World.Player;
        int count = 0;

        for (int i = 0; i < Slots; i++)
        {
            PartyMember m = party.Members[i];
            bool there = m != null && m.Serial != 0;
            _rows[i].Visible = there || !inParty && i == 0;

            if (!there)
            {
                _names[i].Text = inParty ? "" : "No one yet";
                _hits[i].Visible = _tell[i].Visible = _kick[i].Visible = false;
                continue;
            }

            count++;
            Mobile mobile = World.Mobiles.Get(m.Serial);
            _names[i].Text = !string.IsNullOrEmpty(m.Name) ? m.Name : mobile?.Name ?? "?";
            _hits[i].Visible = mobile != null && mobile.HitsMax > 0;

            if (_hits[i].Visible)
            {
                _hits[i].MaxValue = mobile.HitsMax;
                _hits[i].Value = mobile.Hits;
            }

            _tell[i].Visible = true;
            _kick[i].Visible = leader && m.Serial != World.Player.Serial;
        }

        _status.Text = !inParty ? "You are not in a party."
            : party.Leader == World.Player ? $"You lead a party of {count}."
            : $"A party of {count}.";
        _add.Disabled = !leader;
        _leave.Disabled = !inParty;
        _tellAll.Disabled = !inParty;
        _loot.Disabled = !inParty;
    }

    /// <summary>The say line, primed as the classic gump primes it, with the soft keyboard up.</summary>
    private void Tell(string prefix)
    {
        if (World.Party.Leader == 0 && prefix == "/")
        {
            GameActions.Print(World, ResGumps.YouAreNotInAParty, 0, MessageType.System, 3, false);
            return;
        }

        Close();
        UIManager.SystemChat?.TextBoxControl?.SetText(prefix);
        UIManager.SystemChat?.TextBoxControl?.SetKeyboardFocus();
        TouchInput.ShowKeyboard(prefix, false);
    }

    private void Remove(int index)
    {
        PartyMember m = World.Party.Members[index];

        if (m == null || m.Serial == 0)
        {
            GameActions.Print(World, ResGumps.ThereIsNoOneInThatPartySlot, 0, MessageType.System, 3, false);
            return;
        }

        NetClient.Socket.Send_PartyRemoveRequest(m.Serial);
    }

    private void Leave()
    {
        if (World.Party.Leader == 0)
        {
            GameActions.Print(World, ResGumps.YouAreNotInAParty, 0, MessageType.System, 3, false);
            return;
        }

        GameActions.RequestPartyQuit(World.Player);
    }

    private void Okay()
    {
        if (World.Party.Leader != 0 && World.Party.CanLoot != _canLoot)
        {
            World.Party.CanLoot = _canLoot;
            NetClient.Socket.Send_PartyChangeLootTypeRequest(_canLoot);
        }

        Close();
    }

    /// <summary>For the probe: a control by its text or party tag.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("party") && (string)c.GetMeta("party") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }
}
