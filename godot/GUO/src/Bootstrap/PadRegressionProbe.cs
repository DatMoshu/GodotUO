// SPDX-License-Identifier: BSD-2-Clause
// Injected input regression checks; no controller or shard is required.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Input.Gamepad;
using ClassicControl = GUO.Game.UI.Controls.Control;
using HitBox = GUO.Game.UI.Controls.HitBox;

namespace GUO.Host;

internal static class PadRegressionProbe
{
    private const int Device = 241;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static int _failed, _checks;

    private static void Check(string name, bool ok)
    {
        _checks++;
        if (!ok) _failed++;
        GD.Print($"[GUO] pad regression: {(ok ? "ok" : "FAIL")} {name}");
    }

    private static void Button(JoyButton button, bool down)
    {
        var e = new InputEventJoypadButton { Device = Device, ButtonIndex = button, Pressed = down };
        InputMode.Note(e);
        GamepadInput.Handle(e);
    }

    private static void Tap(JoyButton button)
    {
        Button(button, true);
        Button(button, false);
    }

    private static bool Held(string field) => (bool) typeof(GamepadInput).GetField(field, PrivateStatic).GetValue(null);

    // This records the virtual server cancel response, avoiding network I/O.
    // Gump.CloseWithRightClick itself is production code, not a test substitute.
    private sealed class Dialog : Gump
    {
        public int Response = -1;
        public Dialog() : base(Client.Game.UO.World, 0, 123)
        {
            X = 20; Y = 20; Width = 220; Height = 120;
            ActivePage = 1;
            CanMove = false;
        }
        public override void OnButtonClick(int id) { Response = id; Dispose(); }
    }

    private sealed class Branch : ClassicControl { }

    private sealed class Click : HitBox
    {
        public int Clicks;
        public Action Action;
        public Click(int x = 10) : base(x, 10, 30, 30) { }
        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left) { Clicks++; Action?.Invoke(); }
            base.OnMouseUp(x, y, button);
        }
    }

    private static Dialog Show()
    {
        var g = new Dialog();
        UIManager.Add(g);
        return g;
    }

    public static bool Run()
    {
        _failed = _checks = 0;
        GamepadInput.Forced = true;
        PadWizard.OfferAllowed = false;
        PadScreen.Close();
        try
        {
            Inventory();
            foreach (GamepadLayout layout in new[] { GamepadLayout.Labels, GamepadLayout.Swapped })
            {
                PadBindings.ResetToDefaults();
                GamepadInput.KnownLayout(Device, layout);
                var map = new Dictionary<PadCommand, PadInput>();
                foreach (PadCommand c in PadBindings.Order) if (PadBindings.For(c) is PadInput p) map[c] = p;
                map[PadCommand.Use] = PadInput.Button(JoyButton.X);
                map[PadCommand.Cancel] = PadInput.Button(JoyButton.Y);
                map[PadCommand.AttackLast] = PadInput.Button(JoyButton.A);
                map[PadCommand.MacroRow] = PadInput.Button(JoyButton.B);
                PadBindings.Apply(map, PadWizard.Choices);
                JoyButton use = layout == GamepadLayout.Labels ? JoyButton.X : JoyButton.Y;
                JoyButton cancel = layout == GamepadLayout.Labels ? JoyButton.Y : JoyButton.X;
                var g = Show();
                var click = new Click { Action = () => g.ChangePage(2) };
                g.Add(click, 1);
                g.Add(new Click(60), 2);
                Tap(JoyButton.A); Tap(JoyButton.B);
                Check($"{layout}: raw A/B remapped away do not confirm/cancel", click.Clicks == 0 && !g.IsDisposed);
                Tap(use);
                Check($"{layout}: remapped confirm changes page", click.Clicks == 1 && g.ActivePage == 2);
                Check($"{layout}: confirm release does not leak a click", !Held("_clickDown"));
                g.CanCloseWithRightClick = false;
                Tap(cancel);
                Check($"{layout}: mandatory dialog stays open", !g.IsDisposed && g.Response == -1);
                g.CanCloseWithRightClick = true;
                Button(cancel, true);
                Check($"{layout}: cancel follows virtual server response 0", g.IsDisposed && g.Response == 0);
                Button(cancel, false);
                Check($"{layout}: cancel release after disposal does not leak Escape", !Held("_escapeDown"));
            }
            FocusTransitions();
            Axes();
        }
        catch (Exception e) { Check($"unexpected exception {e}", false); }
        finally
        {
            PadScreen.Close();
            GamepadInput.ReleaseAll();
            PadBindings.ResetToDefaults();
            GamepadInput.Forced = null;
        }
        GD.Print($"[GUO] pad regression: {(_failed == 0 ? "PASS" : "FAIL")} ({_checks - _failed}/{_checks})");
        return _failed == 0;
    }

    private static void FocusTransitions()
    {
        PadBindings.ResetToDefaults();
        GamepadInput.KnownLayout(Device, GamepadLayout.Labels);
        var g = Show();
        var first = new Click();
        var second = new Click(60);
        var branch = new Branch { IsVisible = false, Width = 200, Height = 60 };
        var nested = new Click(100);
        branch.Add(nested);
        g.Add(first, 1); g.Add(second, 2); g.Add(branch);
        Tap(JoyButton.A);
        Check("focus excludes inactive page and hidden ancestors", first.Clicks == 1 && second.Clicks == 0 && nested.Clicks == 0 && PadGumpNav.FocusCount == 1);
        g.ChangePage(2);
        Tap(JoyButton.A);
        Check("page transition invalidates focus without child-count change", second.Clicks == 1 && first.Clicks == 1 && PadGumpNav.FocusCount == 1);
        branch.IsVisible = true;
        Tap(JoyButton.DpadRight);
        Tap(JoyButton.A);
        Check("showing ancestor adds nested focus", nested.Clicks == 1 && PadGumpNav.FocusCount == 2);
        branch.IsEnabled = false;
        Tap(JoyButton.A);
        Check("disabling ancestor removes nested focus", second.Clicks == 2 && nested.Clicks == 1 && PadGumpNav.FocusCount == 1);
        second.IsVisible = false;
        branch.IsEnabled = true;
        Tap(JoyButton.A);
        Check("hiding focused control rebuilds focus", nested.Clicks == 2 && second.Clicks == 2 && PadGumpNav.FocusCount == 1);
        g.Dispose();
    }

    private static void Axes()
    {
        var map = new Dictionary<PadCommand, PadInput>();
        foreach (PadCommand c in PadBindings.Order) if (PadBindings.For(c) is PadInput p) map[c] = p;
        map.Remove(PadCommand.MenuWheel);
        map.Remove(PadCommand.InteractRadar);
        map[PadCommand.Use] = PadInput.Axis(JoyAxis.TriggerLeft, 1);
        map[PadCommand.Cancel] = PadInput.Axis(JoyAxis.TriggerRight, 1);
        PadBindings.Apply(map, PadWizard.Choices);
        var g = Show();
        var click = new Click(); g.Add(click);
        void Axis(JoyAxis axis, float value)
        {
            var e = new InputEventJoypadMotion { Device = Device, Axis = axis, AxisValue = value };
            InputMode.Note(e); GamepadInput.Handle(e);
        }
        Axis(JoyAxis.TriggerLeft, 1); Axis(JoyAxis.TriggerLeft, 0);
        Check("axis-bound confirm activates once and consumes release", click.Clicks == 1 && !Held("_clickDown"));
        Axis(JoyAxis.TriggerRight, 1); Axis(JoyAxis.TriggerRight, 0);
        Check("axis-bound cancel responds once and consumes release", g.Response == 0 && g.IsDisposed && !Held("_escapeDown"));
    }

    private static void Inventory()
    {
        PadBindings.ResetToDefaults();
        GamepadInput.KnownLayout(Device, GamepadLayout.Labels);
        // Synthetic item delegates isolate input dispatch from shard/network actions.
        // Only fixture state is installed with reflection; production dispatch and
        // PadScreen.Activate/Drop/Equip/Context execute unchanged.
        Type screen = typeof(PadScreen);
        var lines = (IList) screen.GetField("_lines", PrivateStatic).GetValue(null);
        Type lineType = screen.GetNestedType("Line", BindingFlags.NonPublic);
        object line = Activator.CreateInstance(lineType, true);
        int use = 0, drop = 0, equip = 0, context = 0;
        lineType.GetField("Act").SetValue(line, (Action) (() => use++));
        lineType.GetField("Drop").SetValue(line, (Action) (() => drop++));
        lineType.GetField("Equip").SetValue(line, (Action) (() => equip++));
        lineType.GetField("Context").SetValue(line, (Action) (() => context++));
        lines.Clear(); lines.Add(line);
        screen.GetField("_focus", PrivateStatic).SetValue(null, 0);
        screen.GetProperty("IsOpen").SetValue(null, true);
        screen.GetProperty("Current").SetValue(null, WheelWindow.Backpack);
        Tap(JoyButton.X); Tap(JoyButton.Y); Tap(JoyButton.Back); Tap(JoyButton.A);
        Check("modal inventory drop dispatches once", drop == 1);
        Check("modal inventory equip dispatches once", equip == 1);
        Check("modal inventory context dispatches once", context == 1);
        Check("modal use still dispatches once", use == 1 && PadScreen.IsOpen);
        screen.GetProperty("Current").SetValue(null, WheelWindow.Journal);
        Tap(JoyButton.X); Tap(JoyButton.Y); Tap(JoyButton.Back);
        Check("non-item modal blocks inventory/world commands", drop == 1 && equip == 1 && context == 1 && PadScreen.IsOpen);
        Tap(JoyButton.LeftShoulder); Tap(JoyButton.RightShoulder);
        Check("single-page modal shoulder commands preserve screen", PadScreen.IsOpen && PadScreen.Focus == 0);
        Tap(JoyButton.B);
        Check("modal cancel still closes screen", !PadScreen.IsOpen);
    }
}
