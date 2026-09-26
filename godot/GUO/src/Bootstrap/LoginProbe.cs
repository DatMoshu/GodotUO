// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps.Login;

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

        Passed = true;

        GD.Print(
            $"[GUO] login probe: ok login gump rendered after {frame} frames; "
            + $"window {bounds.Width}x{bounds.Height}, screen scale {Client.Game.ScreenScale}, "
            + $"dpi scale {Client.Game.DpiScale:F2}, gump {gump.Width}x{gump.Height} at {gump.X},{gump.Y}"
        );
    }
}
