// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.Managers;

namespace GUO.Input.Touch;

/// <summary>
/// The journal as touch reads it: the newest lines at the bottom of a parchment
/// card, a finger drag to scroll back, and following new lines while the
/// reader is at the bottom. One reader for both places it appears: the
/// companion tabs' Journal on a second screen (C7), and the Modern journal on
/// one screen (ADR-0024, gump index 10).
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. It only reads JournalManager.Entries
/// and hides lines by the four filters JournalGump hides them by, from the same
/// profile fields (ShowJournalSystem, ShowJournalObjects, ShowJournalClient,
/// ShowJournalGuildAlly). It is a ScrollContainer, so ModernGump's drag scrolls it
/// as it scrolls any other list; the companion tabs scroll it with
/// <see cref="ScrollBy"/>.
/// </remarks>
internal sealed partial class JournalReader : ScrollContainer
{
    private const int MaxLines = 200;

    private readonly VBoxContainer _lines;
    private readonly Control _spacer;
    private int _seen = -1;
    private string _filters = "";
    private bool _follow = true;
    private int _placed = -1; // where the reader itself last put the scroll

    public JournalReader()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        HorizontalScrollMode = ScrollMode.Disabled;
        VerticalScrollMode = ScrollMode.ShowNever;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 6));

        // The lines sit at the bottom while they are fewer than the card holds.
        _lines = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _lines.AddThemeConstantOverride("separation", 1);
        AddChild(_lines);
        _spacer = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _lines.AddChild(_spacer);
    }

    /// <summary>Lines shown now, for the probe.</summary>
    public int LineCount => _lines.GetChildCount() - 1;

    /// <summary>True while the reader follows new lines (it is at the bottom).</summary>
    public bool Following => _follow;

    /// <summary>The classic journal's filter for a kind of line.</summary>
    public static bool Shows(TextType type)
    {
        Profile p = ProfileManager.CurrentProfile;

        return p == null || type switch
        {
            TextType.CLIENT => p.ShowJournalClient,
            TextType.SYSTEM => p.ShowJournalSystem,
            TextType.OBJECT => p.ShowJournalObjects,
            TextType.GUILD_ALLY => p.ShowJournalGuildAlly,
            _ => true,
        };
    }

    /// <summary>New lines, or a filter changed: rebuild, and keep to the bottom if following.</summary>
    public void Refresh()
    {
        var entries = JournalManager.Entries;
        int count = entries.Count;
        string filters = $"{Shows(TextType.SYSTEM)}{Shows(TextType.OBJECT)}{Shows(TextType.CLIENT)}{Shows(TextType.GUILD_ALLY)}";

        if (count != _seen || filters != _filters)
        {
            _seen = count;
            _filters = filters;

            foreach (Node n in _lines.GetChildren())
            {
                if (n != _spacer)
                {
                    _lines.RemoveChild(n);
                    n.QueueFree();
                }
            }

            int shown = 0;

            for (int i = count - 1; i >= 0 && shown < MaxLines; i--)
            {
                JournalEntry e = entries[i];

                if (e == null || !Shows(e.TextType))
                {
                    continue;
                }

                _lines.AddChild(Row(e));
                _lines.MoveChild(_lines.GetChild(_lines.GetChildCount() - 1), 1);
                shown++;
            }
        }

    }

    private static Control Row(JournalEntry e)
    {
        bool system = string.IsNullOrEmpty(e.Name) || e.Name == "System";
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        Label time = UoTheme.Label(e.Time.ToString("HH:mm"), UoTheme.Muted);
        time.CustomMinimumSize = new Vector2(32, 0);
        time.VerticalAlignment = VerticalAlignment.Top;
        row.AddChild(time);
        Label text = UoTheme.Label(system ? e.Text : $"{e.Name}: {e.Text}", system ? UoTheme.Heading : UoTheme.Ink);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(text);
        return row;
    }

    /// <summary>A finger drag by <paramref name="dy"/> panel pixels: down scrolls back.</summary>
    public void ScrollBy(float dy)
    {
        ScrollVertical -= (int)Math.Round(dy);
        _follow = AtBottom();
    }

    private bool AtBottom()
    {
        ScrollBar bar = GetVScrollBar();
        return ScrollVertical >= bar.MaxValue - bar.Page - 2;
    }

    public override void _Process(double delta)
    {
        // Only a scroll the reader did not make (a finger, through ModernGump
        // or ScrollBy) decides whether it follows: back to the bottom, it does.
        if (ScrollVertical != _placed)
        {
            _follow = AtBottom();
        }

        if (_follow)
        {
            ScrollVertical = (int)GetVScrollBar().MaxValue;
        }

        _placed = ScrollVertical;
    }
}
