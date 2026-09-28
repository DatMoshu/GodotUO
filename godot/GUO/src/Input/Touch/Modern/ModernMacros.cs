// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// Modern Options' Macros page (ADR-0024; gump index 4, "a later page of
/// Modern Options"): the macros as finger-sized rows, and one macro's actions
/// edited with pickers instead of the classic page's combo boxes. It runs
/// in three views on the same page: the list, one macro, and a picker (an
/// action's type, or its choice).
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. It edits World.Macros exactly as
/// OptionsGump's Macros page and MacroControl do: a new macro is
/// Macro.CreateEmptyMacro pushed to the manager, and deleting one removes it
/// and its MacroButtonGump. A new action is Macro.Create(type) put in place of
/// the old one, a choice is SubCode within Macro.GetBoundByCode, and text is
/// MacroObjectString.Text. Edits are live, as the classic's are; Apply and
/// Okay save the macros (World.Macros.Save(), as the classic Apply does).
/// "Place button" is the MacroButtonGump the classic list's drag makes. The
/// hotkey is shown, not edited (a phone has no keys; Classic view edits it).
/// </remarks>
internal sealed partial class ModernMacros : VBoxContainer
{
    private const int RowHeight = 26; // as ModernOptions' rows

    private static readonly string[] TypeNames = Enum.GetNames(typeof(MacroType));
    private static readonly string[] SubNames = Enum.GetNames(typeof(MacroSubType));

    private readonly World _world;
    private readonly Action _changed;
    private readonly Color _text;
    private readonly Color _muted;
    private Macro _macro;
    private string _confirmDelete;

    public ModernMacros(World world, Color text, Action changed)
    {
        _world = world;
        _text = text;
        _muted = new Color(text, 0.6f);
        _changed = changed;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);
    }

    /// <summary>What shows now, for the probe: "list", "macro NAME", or "picker".</summary>
    public string View { get; private set; } = "list";

    private void Clear()
    {
        foreach (Node n in GetChildren())
        {
            RemoveChild(n);
            n.QueueFree();
        }

        // Each view starts at its top, as a new page does.
        for (Node p = GetParent(); p != null; p = p.GetParent())
        {
            if (p is ScrollContainer s)
            {
                s.ScrollVertical = 0;
                break;
            }
        }
    }

    private Label Lbl(string text, Color? color = null)
    {
        Label l = UoTheme.Label(text, color ?? _text);
        l.AutowrapMode = TextServer.AutowrapMode.Off;
        return l;
    }

    private static T Row<T>(T c) where T : Control
    {
        c.CustomMinimumSize = new Vector2(c.CustomMinimumSize.X, RowHeight);
        return c;
    }

    private static Button Plate(string text, string tag, Action pressed, float width = 0)
    {
        Button b = UoTheme.Button(text, width);
        b.SetMeta("macros", tag);
        b.Pressed += pressed;
        return b;
    }

    /// <summary>"OpenDoor" as "Open door", "MSC_NONE" as "None": the enum names the classic lists, readable.</summary>
    public static string Words(string name)
    {
        if (name.StartsWith("MSC_")) name = name.Substring(4);
        var sb = new StringBuilder();

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];

            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(i > 0 && sb.Length > 0 && sb[^1] == ' ' ? char.ToLowerInvariant(c) : c);
        }

        string s = sb.ToString();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant() : s;
    }

    // --- the list ---------------------------------------------------------------

    public void ShowList()
    {
        Clear();
        View = "list";
        _macro = null;
        _confirmDelete = null;

        var add = Row(new HBoxContainer());
        add.AddThemeConstantOverride("separation", 4);
        AddChild(add);
        var name = new LineEdit { PlaceholderText = "New macro name", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18) };
        name.SetMeta("macros", "new name");
        add.AddChild(name);
        add.AddChild(Plate("Add", "add", () => AddMacro(name.Text), 50));

        int count = 0;

        for (Macro m = (Macro)_world.Macros.Items; m != null; m = (Macro)m.Next)
        {
            Macro macro = m;
            var row = Row(new HBoxContainer());
            row.AddThemeConstantOverride("separation", 4);
            AddChild(row);
            Label l = Lbl(macro.Name);
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.ClipText = true;
            row.AddChild(l);
            row.AddChild(Plate("Edit", "edit " + macro.Name, () => ShowMacro(macro), 44));
            row.AddChild(Plate("Place button", "place " + macro.Name, () => PlaceButton(macro), 84));
            count++;
        }

        if (count == 0)
        {
            AddChild(Lbl("No macros yet. Name one and tap Add.", _muted));
        }
    }

    public void AddMacro(string name)
    {
        name = name?.Trim();

        if (string.IsNullOrEmpty(name) || _world.Macros.FindMacro(name) != null)
        {
            return;
        }

        Macro macro = Macro.CreateEmptyMacro(name);
        _world.Macros.PushToBack(macro);
        _changed();
        TouchInput.Note($"modern: macro {name} added");
        ShowMacro(macro);
    }

    private void PlaceButton(Macro macro)
    {
        foreach (Gump g in UIManager.Gumps)
        {
            if (g is MacroButtonGump b && b._macro == macro)
            {
                b.Dispose();
            }
        }

        // Where the classic drag would drop it: at the finger; here the middle of the screen, to be moved.
        Compat.Rectangle screen = Client.Game.ClientBounds;
        var gump = new MacroButtonGump(_world, macro, screen.Width / 2, screen.Height / 3);
        gump.X -= gump.Width >> 1;
        gump.Y -= gump.Height >> 1;
        UIManager.Add(gump);
        TouchInput.Note($"modern: macro button placed for {macro.Name}");
    }

    // --- one macro ----------------------------------------------------------------

    public void ShowMacro(Macro macro)
    {
        Clear();
        _macro = macro;
        View = "macro " + macro.Name;

        var head = Row(new HBoxContainer());
        head.AddThemeConstantOverride("separation", 4);
        AddChild(head);
        head.AddChild(Plate("‹ Macros", "back", ShowList, 70));
        Label title = Lbl(macro.Name, UoTheme.Gold);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.ClipText = true;
        head.AddChild(title);
        bool confirming = _confirmDelete == macro.Name;
        head.AddChild(Plate(confirming ? "Tap again to delete" : "Delete", "delete", () => Delete(macro), confirming ? 120 : 56));

        AddChild(Lbl(Hotkey(macro), _muted));

        int index = 0;

        for (MacroObject o = (MacroObject)macro.Items; o != null; o = (MacroObject)o.Next)
        {
            if (o.Code == MacroType.None)
            {
                continue;
            }

            AddChild(ActionRow(o, ++index));
        }

        if (index == 0)
        {
            AddChild(Lbl("No actions yet.", _muted));
        }

        var foot = Row(new HBoxContainer());
        foot.AddThemeConstantOverride("separation", 4);
        AddChild(foot);
        foot.AddChild(Plate("Add action", "add action", () => PickType(null), 90));
        foot.AddChild(Plate("Place button", "place " + macro.Name, () => PlaceButton(macro), 84));
    }

    private static string Hotkey(Macro m)
    {
        if (m.Key == 0 && m.MouseButton == 0 && !m.WheelScroll)
        {
            return "No key (set one in Classic view)";
        }

        string mods = (m.Ctrl ? "Ctrl+" : "") + (m.Alt ? "Alt+" : "") + (m.Shift ? "Shift+" : "");
        string key = m.WheelScroll ? (m.WheelUp ? "Wheel up" : "Wheel down") : m.Key != 0 ? m.Key.ToString().Replace("SDLK_", "") : m.MouseButton.ToString();
        return "Key: " + mods + key;
    }

    private Control ActionRow(MacroObject o, int number)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 1);

        var row = Row(new HBoxContainer());
        row.AddThemeConstantOverride("separation", 4);
        box.AddChild(row);
        row.AddChild(Lbl($"{number}.", _muted));
        row.AddChild(Plate(Words(TypeNames[(int)o.Code]), $"type {number}", () => PickType(o)));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(Plate("Remove", $"remove {number}", () => RemoveAction(o), 56));

        if (o.SubMenuType == 1)
        {
            int count = 0, offset = 0;
            Macro.GetBoundByCode(o.Code, ref count, ref offset);
            int at = (int)o.SubCode - offset;
            string current = at >= 0 && at < count ? Words(SubNames[offset + at]) : "Choose";
            var sub = Row(new HBoxContainer());
            sub.AddThemeConstantOverride("separation", 4);
            sub.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
            sub.AddChild(Plate(current, $"choice {number}", () => PickSub(o)));
            box.AddChild(sub);
        }
        else if (o.SubMenuType == 2 && o is MacroObjectString s)
        {
            var text = new LineEdit { Text = s.Text ?? "", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 18) };
            text.SetMeta("macros", $"text {number}");
            text.TextChanged += t => { s.Text = t; _changed(); };
            box.AddChild(text);
        }

        return box;
    }

    private void RemoveAction(MacroObject o)
    {
        _macro.Remove(o);

        if (_macro.Items == null)
        {
            _macro.Items = Macro.Create(MacroType.None);
        }

        _changed();
        ShowMacro(_macro);
    }

    private void Delete(Macro macro)
    {
        if (_confirmDelete != macro.Name)
        {
            _confirmDelete = macro.Name;
            ShowMacro(macro);
            return;
        }

        foreach (Gump g in UIManager.Gumps)
        {
            if (g is MacroButtonGump b && b._macro == macro)
            {
                b.Dispose();
            }
        }

        _world.Macros.Remove(macro);
        _changed();
        TouchInput.Note($"modern: macro {macro.Name} deleted");
        ShowList();
    }

    // --- pickers -------------------------------------------------------------------

    private void Picker(string title, IReadOnlyList<string> names, int current, Action<int> chosen)
    {
        Clear();
        View = "picker";
        var head = Row(new HBoxContainer());
        head.AddThemeConstantOverride("separation", 4);
        AddChild(head);
        head.AddChild(Plate("‹ Back", "back", () => ShowMacro(_macro), 60));
        head.AddChild(Lbl(title, UoTheme.Gold));

        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            Button b = Plate(names[i], "pick " + names[i], () => chosen(index));
            b.Alignment = HorizontalAlignment.Left;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            if (i == current)
            {
                b.AddThemeStyleboxOverride("normal", UoTheme.Plate(UoTheme.SelectedShade));
                b.AddThemeColorOverride("font_color", UoTheme.Heading);
            }

            AddChild(b);
        }
    }

    /// <summary>An action's type; <paramref name="o"/> null adds one at the end.</summary>
    private void PickType(MacroObject o)
    {
        var names = new List<string>();

        for (int i = 1; i < TypeNames.Length; i++)
        {
            names.Add(Words(TypeNames[i]));
        }

        Picker(o == null ? "New action" : "Action", names, o == null ? -1 : (int)o.Code - 1, i =>
        {
            MacroObject made = Macro.Create((MacroType)(i + 1));

            if (o == null)
            {
                // After the last real action, in place of the classic's empty last entry.
                MacroObject last = (MacroObject)_macro.GetLast();

                if (last != null && last.Code == MacroType.None)
                {
                    _macro.Insert(last, made);
                    _macro.Remove(last);
                }
                else
                {
                    _macro.PushToBack(made);
                }
            }
            else
            {
                _macro.Insert(o, made);
                _macro.Remove(o);
            }

            _changed();
            TouchInput.Note($"modern: macro {_macro.Name} action -> {(MacroType)(i + 1)}");
            ShowMacro(_macro);
        });
    }

    private void PickSub(MacroObject o)
    {
        int count = 0, offset = 0;
        Macro.GetBoundByCode(o.Code, ref count, ref offset);
        var names = new List<string>();

        for (int i = 0; i < count; i++)
        {
            names.Add(Words(SubNames[offset + i]));
        }

        Picker(Words(TypeNames[(int)o.Code]), names, (int)o.SubCode - offset, i =>
        {
            o.SubCode = (MacroSubType)(offset + i);
            _changed();
            ShowMacro(_macro);
        });
    }

    // --- the probe ------------------------------------------------------------------

    /// <summary>For the probe: the macro being edited.</summary>
    public Macro Editing => _macro;

    /// <summary>For the probe: the picker's <paramref name="i"/>th choice.</summary>
    public Control Pick(int i)
    {
        foreach (Node n in GetChildren())
        {
            if (n is Button b && b.HasMeta("macros") && ((string)b.GetMeta("macros")).StartsWith("pick ") && i-- == 0)
            {
                return b;
            }
        }

        return null;
    }
}
