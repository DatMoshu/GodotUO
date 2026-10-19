// SPDX-License-Identifier: BSD-2-Clause
// Keyboard checks on the login screen; no shard is required.
using System;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Input;

namespace GUO.Host;

/// <summary>
/// The keyboard layer that replaces upstream's SDL filter (GodotInput), driven
/// with injected key events at the login screen. Focus loss and an AltGr
/// layout come from the operating system and cannot be injected, so the
/// focus-out notification is sent to the client directly, and the AltGr key
/// is sent as Godot delivers it once the input method is on (no Ctrl or Alt).
/// </summary>
internal static class KeyboardProbe
{
    private static int _failed, _checks;

    private static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) _failed++;
        GD.Print($"[GUO] keyboard probe: {(ok ? "ok" : "FAIL")} {name}{(detail.Length > 0 ? " -- " + detail : "")}");
    }

    private static void Press(Key key, bool down, bool ctrl = false, bool alt = false, bool shift = false, long unicode = 0)
    {
        GodotInput.Handle(new InputEventKey
        {
            Keycode = key, PhysicalKeycode = key, Pressed = down,
            CtrlPressed = ctrl, AltPressed = alt, ShiftPressed = shift, Unicode = unicode,
        });
    }

    public static bool Run()
    {
        _failed = _checks = 0;

        try
        {
            FocusLoss();
            AltGrText();
        }
        catch (Exception e)
        {
            Check($"unexpected exception {e}", false);
        }

        GD.Print($"[GUO] keyboard probe: {(_failed == 0 ? "PASS" : "FAIL")} ({_checks - _failed}/{_checks})");
        return _failed == 0;
    }

    /// <summary>K1 of the input audit: a modifier held over a focus loss.</summary>
    private static void FocusLoss()
    {
        foreach ((Key key, Func<bool> held, string name) in new (Key, Func<bool>, string)[]
        {
            (Godot.Key.Alt, () => Keyboard.Alt, "Alt"),
            (Godot.Key.Ctrl, () => Keyboard.Ctrl, "Ctrl"),
            (Godot.Key.Shift, () => Keyboard.Shift, "Shift"),
        })
        {
            Press(key, true, ctrl: key == Godot.Key.Ctrl, alt: key == Godot.Key.Alt, shift: key == Godot.Key.Shift);
            Check($"{name} down is held", held());
            // Alt-Tab: the window loses focus with the key down, and Godot sends no key-up.
            Client.Game.Notification((int) Node.NotificationApplicationFocusOut);
            Check($"{name} is not held after the window loses focus", !held());
            Client.Game.Notification((int) Node.NotificationApplicationFocusIn);
        }

        // Two at once (Ctrl+Shift), and a letter: all released together.
        Press(Godot.Key.Ctrl, true, ctrl: true);
        Press(Godot.Key.Shift, true, ctrl: true, shift: true);
        Press(Godot.Key.A, true, ctrl: true, shift: true);
        Client.Game.Notification((int) Node.NotificationApplicationFocusOut);
        Check("every key held over a focus loss comes up", !Keyboard.Ctrl && !Keyboard.Shift && !Keyboard.Alt);
        Client.Game.Notification((int) Node.NotificationApplicationFocusIn);

        // A key that already came up is not raised a second time.
        Press(Godot.Key.Alt, true, alt: true);
        Press(Godot.Key.Alt, false);
        Client.Game.Notification((int) Node.NotificationApplicationFocusOut);
        Check("a released key is not released again", !Keyboard.Alt);
        Client.Game.Notification((int) Node.NotificationApplicationFocusIn);
    }

    /// <summary>
    /// K2: an AltGr character reaches a text field. With the input method on,
    /// Godot sends AltGr+Q on a German layout as Q with "@" and neither Ctrl
    /// nor Alt; before, it came with both and was dropped as a command.
    /// </summary>
    private static void AltGrText()
    {
        var box = new StbTextBox(1, 64, 200) { X = 10, Y = 10, Width = 200, Height = 20 };
        var holder = new Game.UI.Gumps.Gump(Client.Game.UO.World, 0, 0) { Width = 220, Height = 40 };
        holder.Add(box);
        UIManager.Add(holder);
        box.SetKeyboardFocus();

        try
        {
            Press(Godot.Key.Q, true, unicode: '@');
            Press(Godot.Key.Q, false);
            Check("an AltGr character as Godot sends it with the input method on is typed", box.Text == "@", $"\"{box.Text}\"");

            Press(Godot.Key.C, true, ctrl: true, unicode: 'c');
            Press(Godot.Key.C, false);
            Check("Ctrl+C is still a command, not text", box.Text == "@", $"\"{box.Text}\"");
        }
        finally
        {
            holder.Dispose();
        }
    }
}
