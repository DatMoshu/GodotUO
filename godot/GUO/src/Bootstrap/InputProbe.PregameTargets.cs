// SPDX-License-Identifier: BSD-2-Clause
// GUO probe only: actual Pregame3D controls use window coordinates already.
using System;
using System.Threading.Tasks;
using Godot;
using GUO.Pregame3D;

namespace GUO.Host;

internal static partial class InputProbe
{
    private enum LoginTarget { Account, Password, Login, Server }

    // Freeze the mode at entry. Losing a 3D stage must never fall back to a
    // guessed Classic click. The Classic Click/Send path itself is unchanged.
    private static async Task<bool> ClickLoginTarget(Node host, Vector2 classic,
        LoginTarget target, bool pregame)
    {
        if (!pregame)
        {
            if (PregameScreen.Active) return LoginTargetFailure(target, "mode changed");
            await Click(host, classic);
            return true;
        }

        PregameScreen screen = PregameScreen.Instance;
        Stage stage = screen?.Stage;
        if (!TryLoginTarget(screen, stage, target, out Control control, out IFocusable focus, out Vector2 at))
            return LoginTargetFailure(target, "not ready or hit identity differs");

        SendWindowPointer(new InputEventMouseMotion { Position = at });
        await Frames(host, 2);
        // Hover, painting drift, layout and focus lift may all move the target.
        if (!TryLoginTarget(screen, stage, target, out Control current, out IFocusable currentFocus, out at)
            || !ReferenceEquals(current, control) || !ReferenceEquals(currentFocus, focus))
            return LoginTargetFailure(target, "stale target before press");

        SendWindowPointer(new InputEventMouseMotion { Position = at });
        bool pressed = false;
        try
        {
            // No await between the final readiness check and the raw press.
            if (!TryLoginTarget(screen, stage, target, out current, out currentFocus, out at)
                || !ReferenceEquals(current, control) || !ReferenceEquals(currentFocus, focus))
                return LoginTargetFailure(target, "target changed before press");
            pressed = true;
            SendWindowPointer(new InputEventMouseButton
                { ButtonIndex = MouseButton.Left, Position = at, Pressed = true });
            await Frames(host, 6);
        }
        finally
        {
            // Release exactly the submitted press, even if it changed stage.
            // Never resolve another control or send another press during cleanup.
            if (pressed) SendWindowPointer(new InputEventMouseButton
                { ButtonIndex = MouseButton.Left, Position = at, Pressed = false });
        }
        await Frames(host, 40);

        bool field = target is LoginTarget.Account or LoginTarget.Password;
        bool ok = !field || FieldReady(screen, stage, focus);
        Check("pregame target " + target, ok, field ? "actual field focus and keyboard readiness" : "raw window click delivered");
        return ok;
    }

    private static bool TryLoginTarget(PregameScreen screen, Stage stage, LoginTarget target,
        out Control control, out IFocusable focus, out Vector2 at)
    {
        control = null; focus = null; at = default;
        if (!ScreenReady(screen, stage) || screen.Keyboard?.IsOpen == true) return false;
        control = target switch
        {
            LoginTarget.Account => LoginStage.ProbeAccountField,
            LoginTarget.Password => LoginStage.ProbePasswordField,
            LoginTarget.Login => LoginStage.ProbeLoginButton,
            LoginTarget.Server => ServerStage.ProbeFirstServer,
            _ => null
        };
        if (!LoginTargetControlReady(control, out at)) return false;
        foreach (IOverlayFocusable item in stage.OverlayItems)
        {
            if (ReferenceEquals(item.Control, control)) { focus = item; break; }
        }
        return focus?.CanFocus == true && ReferenceEquals(screen.HitAt(at), focus);
    }

    // No painting/overlay/DPI multiplier: GetGlobalRect is already in the
    // viewport coordinate system consumed by PregameScreen.Main/HitAt.
    internal static bool LoginTargetPoint(Rect2 rect, Rect2 viewport, out Vector2 at)
    {
        at = rect.GetCenter();
        return rect.Position.IsFinite() && rect.Size.IsFinite()
            && viewport.Position.IsFinite() && viewport.Size.IsFinite()
            && rect.Size.X > 0 && rect.Size.Y > 0 && at.IsFinite()
            && viewport.HasPoint(at);
    }

    internal static bool LoginTargetControlReady(Control control, out Vector2 at)
    {
        at = default;
        if (control == null || !GodotObject.IsInstanceValid(control)
            || control.IsQueuedForDeletion() || !control.IsInsideTree() || !control.IsVisibleInTree())
            return false;
        if (!LoginTargetPoint(control.GetGlobalRect(), control.GetViewport().GetVisibleRect(), out at))
            return false;
        // IsVisibleInTree does not imply that a scrolled row is inside its clip.
        for (Node parent = control.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is Control clip && clip.ClipContents && !clip.GetGlobalRect().HasPoint(at))
                return false;
        }
        return true;
    }

    private static bool ScreenReady(PregameScreen screen, Stage stage) =>
        PregameScreen.Active && ReferenceEquals(PregameScreen.Instance, screen)
        && screen != null && GodotObject.IsInstanceValid(screen) && !screen.IsQueuedForDeletion()
        && screen.IsInsideTree() && stage != null && ReferenceEquals(screen.Stage, stage) && !screen.ModalOpen;

    private static bool FieldReady(PregameScreen screen, Stage stage, IFocusable field) =>
        ScreenReady(screen, stage) && screen.Keyboard?.IsOpen == true
        && field?.CanFocus == true && ReferenceEquals(screen.Focus.Current, field);

    private static async Task<bool> TypeLoginField(Node host, string text, LoginTarget target, bool pregame)
    {
        if (!pregame)
        {
            if (PregameScreen.Active) return LoginTargetFailure(target, "mode changed before typing");
            await Type(host, text);
            return true;
        }
        PregameScreen screen = PregameScreen.Instance;
        Stage stage = screen?.Stage;
        IFocusable field = screen?.Focus.Current;
        bool expected = target == LoginTarget.Account ? LoginStage.ProbeOnAccount : LoginStage.ProbeOnPassword;
        if (!expected || !FieldReady(screen, stage, field))
            return LoginTargetFailure(target, "field focus missing before typing");
        foreach (char c in text)
        {
            if (!FieldReady(screen, stage, field))
                return LoginTargetFailure(target, "field focus lost while typing");
            FieldInputs++;
            try
            {
                Send(new InputEventKey { Keycode = (Key)char.ToUpperInvariant(c), Unicode = c, Pressed = true });
                await Frames(host, 2);
            }
            finally
            {
                Send(new InputEventKey { Keycode = (Key)char.ToUpperInvariant(c), Unicode = c, Pressed = false });
            }
            await Frames(host, 2);
        }
        if (!FieldReady(screen, stage, field))
            return LoginTargetFailure(target, "field focus lost before keyboard completion");
        // Use the physical-key completion route, never Keyboard.Finish/Focus.Set.
        try
        {
            Send(new InputEventKey { Keycode = Key.Enter, Pressed = true });
            await Frames(host, 2);
        }
        finally { Send(new InputEventKey { Keycode = Key.Enter, Pressed = false }); }
        await Frames(host, 2);
        bool ok = ScreenReady(screen, stage) && screen.Keyboard?.IsOpen == false;
        if (!ok) return LoginTargetFailure(target, "keyboard completion not observed");
        return true;
    }

    private static void SendWindowPointer(InputEventMouse e)
    {
        // Deliberately bypass Send's Classic LoginOrigin and PointerScale.
        Godot.Input.ParseInputEvent(e);
    }

    private static bool LoginTargetFailure(LoginTarget target, string reason)
    {
        Check("pregame target " + target, false, reason);
        return false; // Leave PerfProbe able to serialize an honest login-only result.
    }
}
