// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Shut and open every door on screen, photographing each frame after.
/// </summary>
/// <remarks>
/// A door opened by an NPC was seen to pop over the roof in front of it, and a
/// door already standing open draws correctly (parity night 2026-09-26, D1).
/// So if it happens, it happens in the frames between the server moving the
/// door and the scene settling. Every one of them is kept, not only the last,
/// and the capture starts as the command is sent rather than when the server
/// answers: its reply comes after the door packets, too late.
/// </remarks>
internal static class DoorProbe
{
    public static bool Passed { get; private set; }

    private const int FramesAfter = 40;

    public static async System.Threading.Tasks.Task Run(Node host, string dir)
    {
        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        if (!Client.Game.UO.World.InGame)
        {
            GD.PrintErr("[GUO] door probe: never got into the world.");

            return;
        }

        dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots/door_probe" : dir;
        DirAccess.MakeDirRecursiveAbsolute(dir);

        // Let the scene settle after the [go before the first picture.
        await InputProbe.Wait(host, 120);

        bool[] states = { false, true, false, true };

        for (int cycle = 0; cycle < states.Length; cycle++)
        {
            string open = states[cycle] ? "true" : "false";

            GD.Print($"[GUO] door probe: cycle {cycle}, Open {open}");

            await InputProbe.Say(host, $"[Screen set Open {open} where BaseDoor");

            for (int frame = 0; frame < FramesAfter; frame++)
            {
                await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);

                Image image = host.GetViewport().GetTexture().GetImage();
                string path = dir.PathJoin($"door_{cycle}_{open}_{frame:D2}.png");

                if (image.SavePng(path) != Error.Ok)
                {
                    GD.PrintErr($"[GUO] door probe: could not write {path}");

                    return;
                }
            }

            // Past any door animation and the server's reply, before the next.
            await InputProbe.Wait(host, 120);
        }

        GD.Print($"[GUO] door probe: frames in {ProjectSettings.GlobalizePath(dir)}");
        Passed = true;
    }
}
