// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps.Login;
using GUO.Input;

namespace GUO.Host;

/// <summary>
/// Wait for the login gump to be built and drawn, then say so on the log.
/// </summary>
/// <remarks>
/// The smallest claim the client can make about itself on a platform where
/// nobody is watching the screen: it booted, read the archives, built the
/// login scene and got a frame out. On a device the line this prints is what
/// the Android smoke reads back through logcat; on a desktop it is a cheap
/// gate for the same fact, with the exit code as the verdict.
/// </remarks>
internal static class LoginProbe
{
    public static bool Passed { get; private set; }

    /// <summary>Frames to wait before giving up. A slow phone takes a while to read the archives.</summary>
    private const int Budget = 1800;

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        int frame = 0;

        while (frame < Budget)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            frame++;

            if (Client.Game?.Scene != null && UIManager.GetGump<LoginGump>() != null)
            {
                break;
            }
        }

        LoginGump gump = UIManager.GetGump<LoginGump>();

        if (gump == null)
        {
            GD.PrintErr($"[GUO] login probe: FAIL no login gump after {frame} frames");

            return;
        }

        // The gump exists; now a frame with it on. FramePostDraw is the
        // rendering server's word that a frame reached the swap chain.
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);

        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;

        Passed = await InjectedInputArrives(host);

        GD.Print(
            $"[GUO] login probe: {(Passed ? "ok" : "FAIL")} login gump rendered after {frame} frames; "
            + $"window {bounds.Width}x{bounds.Height}, screen scale {Client.Game.ScreenScale}, "
            + $"dpi scale {Client.Game.DpiScale:F2}, gump {gump.Width}x{gump.Height} at {gump.X},{gump.Y}"
        );
    }

    /// <summary>
    /// Push one mouse move and one Shift press through the queue the probes
    /// use, and read them back out of the client.
    /// </summary>
    /// <remarks>
    /// The probes drive the client through <c>Input.ParseInputEvent</c>, in
    /// process, and a scripted run's window is kept unfocusable so it cannot
    /// take the keyboard from whoever is at the desktop (Main._EnterTree).
    /// Those two facts only fit together if in-process events reach the
    /// client with no OS focus at all -- which is what this checks, on the
    /// login screen, with no shard involved. A mouse move lands in
    /// Mouse.Position and a Shift press in Keyboard.Shift; both are cleared
    /// again so the login gump is left as it was found.
    /// </remarks>
    private static async System.Threading.Tasks.Task<bool> InjectedInputArrives(Node host)
    {
        var at = new Vector2(37f, 23f) * (float)Client.Game.DpiScale;

        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at, Relative = Vector2.Zero });
        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, ShiftPressed = true, Pressed = true });

        // ParseInputEvent queues; the events are dispatched at the next
        // input flush, which is before the next frame's process.
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        bool mouseArrived = Mouse.Position.X == (int)(at.X / Client.Game.DpiScale)
            && Mouse.Position.Y == (int)(at.Y / Client.Game.DpiScale);
        bool keyArrived = Keyboard.Shift;

        Godot.Input.ParseInputEvent(new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, ShiftPressed = false, Pressed = false });
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        GD.Print(
            $"[GUO] login probe: injected input {(mouseArrived && keyArrived ? "reached" : "did NOT reach")} the client "
            + $"(mouse {(mouseArrived ? "ok" : "lost")}, key {(keyArrived ? "ok" : "lost")}, "
            + $"window focused {DisplayServer.WindowIsFocused()}, no-focus flag {DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.NoFocus)})"
        );

        return mouseArrived && keyArrived;
    }
}
