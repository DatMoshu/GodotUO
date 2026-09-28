// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// The skills gump, Modern (ADR-0024, gump 3): every skill as a row a thumb can
/// use, with its value and cap, a Use plate where it can be used, and its lock
/// arrow; a group and a sort to narrow and order the list. Both classic skills
/// gumps (StandardSkillsGump, SkillGumpAdvanced) open as it on touch.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. Use is GameActions.UseSkill and the
/// lock is GameActions.ChangeSkillLockStatus cycling Up, Down, Locked, as
/// SkillGumpAdvanced's rows do; the lock art is the classic gumps' own arrows
/// (0x0983 up, 0x0985 down, 0x082C locked). Groups are the player's own
/// (World.SkillsGroupManager), as the standard gump shows them.
/// </remarks>
internal sealed partial class ModernSkills : ModernGump
{
    private const int RowHeight = 26;

    private static readonly string[] Sorts = { "By name", "By value", "By base" };

    private Label _totals;
    private Label _groupLabel, _sortLabel;
    private VBoxContainer _list;
    private int _group = -1; // -1: all
    private int _sort;
    private readonly List<(Skill skill, Label value, TextureButton lockButton)> _rows = new();

    public ModernSkills(World world) : base(world) { }

    protected override float MaxArtWidth => 470f;

    protected override void Build(PanelContainer card)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        Label title = UoTheme.Label("Skills", UoTheme.Heading, 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        Button close = UoTheme.Button("X", 30);
        close.Pressed += Close;
        header.AddChild(close);

        _totals = UoTheme.Label("", UoTheme.Muted);
        col.AddChild(_totals);

        // The group and the order, as steppers: a tap each.
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        col.AddChild(filters);
        _groupLabel = Stepper(filters, "group", d => { _group = Wrap(_group + 1 + d, GroupCount + 1) - 1; Rebuild(); });
        _sortLabel = Stepper(filters, "sort", d => { _sort = Wrap(_sort + d, Sorts.Length); Rebuild(); });

        col.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 4));
        col.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 1);
        scroll.AddChild(_list);
    }

    private static int Wrap(int v, int n) => ((v % n) + n) % n;

    private int GroupCount => World.SkillsGroupManager.Groups.Count;

    private static Label Stepper(HBoxContainer row, string tag, Action<int> step)
    {
        Button prev = UoTheme.Button("‹", 30), next = UoTheme.Button("›", 30);
        prev.SetMeta("skills", tag + " prev");
        next.SetMeta("skills", tag + " next");
        Label value = UoTheme.Label("", UoTheme.Ink);
        value.CustomMinimumSize = new Vector2(120, 0);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        prev.Pressed += () => step(-1);
        next.Pressed += () => step(1);
        row.AddChild(prev);
        row.AddChild(value);
        row.AddChild(next);
        return value;
    }

    protected override void OnOpen() => Rebuild();

    /// <summary>The list for the current group and order.</summary>
    private void Rebuild()
    {
        foreach (Node n in _list.GetChildren())
        {
            _list.RemoveChild(n);
            n.QueueFree();
        }

        _rows.Clear();
        Skill[] skills = World.Player?.Skills;

        if (skills == null)
        {
            return;
        }

        var shown = new List<Skill>();
        SkillsGroup group = _group >= 0 && _group < GroupCount ? World.SkillsGroupManager.Groups[_group] : null;

        foreach (Skill s in skills)
        {
            if (s != null && (group == null || group.Contains((byte)s.Index)))
            {
                shown.Add(s);
            }
        }

        shown.Sort((a, b) => _sort switch
        {
            1 => b.ValueFixed.CompareTo(a.ValueFixed),
            2 => b.BaseFixed.CompareTo(a.BaseFixed),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        });

        foreach (Skill s in shown)
        {
            _list.AddChild(BuildRow(s));
        }

        _groupLabel.Text = group?.Name ?? "All skills";
        _sortLabel.Text = Sorts[_sort];
        Refresh();
    }

    private Control BuildRow(Skill s)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, RowHeight) };
        row.AddThemeConstantOverride("separation", 6);

        // Use, where the skill can be used; the same room kept where not.
        if (s.IsClickable)
        {
            Button use = UoTheme.Button("Use", 44);
            use.SetMeta("skills", "use " + s.Name);
            use.Pressed += () => { GameActions.UseSkill(s.Index); Close(); };
            row.AddChild(use);
        }
        else
        {
            row.AddChild(new Control { CustomMinimumSize = new Vector2(44, 0) });
        }

        Label name = UoTheme.Label(s.Name, UoTheme.Ink);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.ClipText = true;
        row.AddChild(name);

        Label value = UoTheme.Label("", UoTheme.Ink);
        value.CustomMinimumSize = new Vector2(84, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(value);

        var lockButton = new TextureButton { StretchMode = TextureButton.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(24, RowHeight) };
        lockButton.SetMeta("skills", "lock " + s.Name);
        lockButton.Pressed += () => CycleLock(s);
        row.AddChild(lockButton);

        _rows.Add((s, value, lockButton));
        return row;
    }

    /// <summary>Up, then Down, then Locked, as the classic rows cycle it.</summary>
    private void CycleLock(Skill s)
    {
        Lock next = s.Lock switch { Lock.Up => Lock.Down, Lock.Down => Lock.Locked, _ => Lock.Up };
        s.Lock = next;
        GameActions.ChangeSkillLockStatus((ushort)s.Index, (byte)next);
        Refresh();
    }

    protected override void Refresh()
    {
        float real = 0, value = 0;

        foreach (Skill s in World.Player?.Skills ?? Array.Empty<Skill>())
        {
            if (s != null)
            {
                real += s.Base;
                value += s.Value;
            }
        }

        _totals.Text = $"Total {real:0.0} (with modifiers {value:0.0})";

        foreach ((Skill skill, Label valueLabel, TextureButton lockButton) in _rows)
        {
            valueLabel.Text = $"{skill.Value:0.0} / {skill.Cap:0}";
            lockButton.TextureNormal = UoTheme.GumpTexture(skill.Lock == Lock.Up ? (ushort)0x0983 : skill.Lock == Lock.Down ? (ushort)0x0985 : (ushort)0x082C);
        }
    }

    /// <summary>For the probe: a control by its tag.</summary>
    public Control Find(string what)
    {
        foreach (Node n in Card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.IsVisibleInTree()
                && ((c.HasMeta("skills") && (string)c.GetMeta("skills") == what) || c is Button b && b.Text == what))
            {
                return c;
            }
        }

        return null;
    }
}
