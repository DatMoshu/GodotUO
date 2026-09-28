// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The journal, Modern (ADR-0024, gump index 10): a full-height reader with
/// the journal in large type, the classic journal's four filters as plates,
/// and a drag to scroll back. It is a reader, not a replacement: the classic
/// journal stays the always-open gump it is, and this opens from its window
/// menu ("Read") and from a hold on the bar's Journal button.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO, and not in the ModernGumps registry,
/// which replaces a classic gump as it opens; a journal restored at login would
/// otherwise open a modal over the game. The lines are the companion tabs'
/// reader (<see cref="JournalReader"/>); the filters write the profile fields
/// JournalGump's check boxes write, so both show the same lines.
/// </remarks>
internal sealed partial class ModernJournal : ModernGump
{
    private static ModernJournal _instance;

    private JournalReader _reader;
    private readonly Button[] _filters = new Button[4];

    private static readonly (string label, TextType type)[] Kinds =
    {
        ("System", TextType.SYSTEM), ("Objects", TextType.OBJECT), ("Client", TextType.CLIENT), ("Guild", TextType.GUILD_ALLY),
    };

    public ModernJournal(World world) : base(world) { }

    protected override float MaxArtWidth => 560f;

    /// <summary>Open the reader over the game (touch only; the caller checks).</summary>
    public static void OpenReader(World world)
    {
        if (_instance == null || !GodotObject.IsInstanceValid(_instance))
        {
            _instance = new ModernJournal(world);
            Client.Game.AddChild(_instance);
        }

        _instance.Open();
        TouchInput.Note("modern: journal reader opened");
    }

    /// <summary>For the probe: the reader open now, or null.</summary>
    public static ModernJournal OpenNow => IsOpen ? Current as ModernJournal : null;

    public JournalReader Reader => _reader;

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        Label title = UoTheme.Label("Journal", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);

        // The classic journal's filters, as plates: pressed shows that kind.
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        col.AddChild(filters);

        for (int i = 0; i < Kinds.Length; i++)
        {
            int index = i;
            Button b = UoTheme.Button(Kinds[i].label, 70);
            b.SetMeta("journal", "filter " + Kinds[i].label);
            b.Pressed += () => Toggle(index);
            filters.AddChild(b);
            _filters[i] = b;
        }

        _reader = new JournalReader();
        col.AddChild(_reader);
    }

    private void Toggle(int index)
    {
        Profile p = ProfileManager.CurrentProfile;

        switch (Kinds[index].type)
        {
            case TextType.SYSTEM: p.ShowJournalSystem = !p.ShowJournalSystem; break;
            case TextType.OBJECT: p.ShowJournalObjects = !p.ShowJournalObjects; break;
            case TextType.CLIENT: p.ShowJournalClient = !p.ShowJournalClient; break;
            case TextType.GUILD_ALLY: p.ShowJournalGuildAlly = !p.ShowJournalGuildAlly; break;
        }

        Refresh();
    }

    protected override void OnOpen() => Refresh();

    protected override void Refresh()
    {
        for (int i = 0; i < Kinds.Length; i++)
        {
            bool on = JournalReader.Shows(Kinds[i].type);
            _filters[i].AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            _filters[i].AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Muted);
        }

        _reader.Refresh();
    }

    /// <summary>For the probe: a control by its tag or text.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("journal") && (string)c.GetMeta("journal") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }
}
