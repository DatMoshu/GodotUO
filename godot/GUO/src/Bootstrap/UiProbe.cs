// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;

namespace GUO.Host;

/// <summary>
/// Get into the world, open the backpack, and say on the log what the
/// profile's platform defaults came out as.
/// </summary>
/// <remarks>
/// The check behind a claim about a screen's defaults: which platform table
/// the profile went through, the fields it set, and what the world viewport
/// and the backpack actually look like with them. On a desktop the probe
/// logs in itself (InputProbe.EnterTheWorld). On a device nothing can click
/// the login screen at desktop coordinates, so the probe waits for someone
/// (adb, a finger) to get the character in, then carries on.
/// </remarks>
internal static class UiProbe
{
    public static bool Passed { get; private set; }

    /// <summary>Frames to wait for the world when something else is logging in.</summary>
    private const int WaitForWorld = 36000;

    public static async System.Threading.Tasks.Task Run(Node host, bool logInHere)
    {
        if (logInHere)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        int frame = 0;

        while (!(Client.Game?.UO?.World?.InGame ?? false) && frame < WaitForWorld)
        {
            await InputProbe.Wait(host, 1);
            frame++;
        }

        World world = Client.Game?.UO?.World;

        if (world == null || !world.InGame)
        {
            GD.PrintErr("[GUO] ui probe: FAIL never got into the world");

            return;
        }

        // The world and its gumps settle before anything is opened.
        await InputProbe.Wait(host, 240);

        Profile p = ProfileManager.CurrentProfile;

        GD.Print(
            $"[GUO] ui probe: platform {PlatformDefaults.Platform}, profile v{p.ProfileVersion}; "
            + $"full size {p.GameWindowFullSize}, borderless {p.WindowBorderless}, world {p.GameWindowSize.X}x{p.GameWindowSize.Y} at {p.GameWindowPosition.X},{p.GameWindowPosition.Y}; "
            + $"wheel zoom {p.EnableMousewheelScaleZoom}, keep zoom {p.SaveScaleAfterClose}; "
            + $"large containers {p.UseLargeContainerGumps}, container scale {p.ContainersScale}, scale items {p.ScaleItemsInsideContainers}; "
            + $"grid loot {p.GridLootType}"
        );

        GameActions.OpenBackpack(world);

        await InputProbe.Wait(host, 120);

        ContainerGump backpack = world.Player?.FindItemByLayer(Game.Data.Layer.Backpack) is { } bag
            ? UIManager.GetGump<ContainerGump>(bag.Serial)
            : null;

        WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();
        Compat.Rectangle bounds = Client.Game.Window.ClientBounds;

        GD.Print(
            $"[GUO] ui probe: window {bounds.Width}x{bounds.Height}, screen scale {Client.Game.ScreenScale}; "
            + $"viewport {viewport?.Width}x{viewport?.Height} at {viewport?.X},{viewport?.Y}; "
            + (backpack == null ? "backpack not open" : $"backpack gump 0x{backpack.Graphic:X4} {backpack.Width}x{backpack.Height} at {backpack.X},{backpack.Y}")
        );

        Passed = backpack != null && viewport != null;

        GD.Print($"[GUO] ui probe: {(Passed ? "ok" : "FAIL")}");
    }
}
