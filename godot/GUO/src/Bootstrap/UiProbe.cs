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
            + $"grid loot {p.GridLootType}; grid containers {p.GridContainers}, slot {p.GridContainerSlotSize}"
        );

        GameActions.OpenBackpack(world);

        await InputProbe.Wait(host, 120);

        Gump backpack = world.Player?.FindItemByLayer(Game.Data.Layer.Backpack) is { } bag
            ? ContainerFor(bag.Serial)
            : null;

        WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();
        Compat.Rectangle bounds = Client.Game.ClientBounds;

        GD.Print(
            $"[GUO] ui probe: window {bounds.Width}x{bounds.Height}, screen scale {Client.Game.ScreenScale}; "
            + $"viewport {viewport?.Width}x{viewport?.Height} at {viewport?.X},{viewport?.Y}; "
            + (backpack == null ? "backpack not open" : $"backpack {Kind(backpack)} {backpack.Width}x{backpack.Height} at {backpack.X},{backpack.Y}")
        );

        Passed = backpack != null && viewport != null;

        // A second container, from inside the backpack: where it lands
        // against the first, the character and the screen's edges.
        Gump inner = null;
        Game.GameObjects.Item bagItem = null;

        if (backpack != null && world.Items.TryGetValue(backpack.LocalSerial, out var pack))
        {
            for (LinkedObject i = pack.Items; i != null; i = i.Next)
            {
                if (i is Game.GameObjects.Item it && it.ItemData.IsContainer)
                {
                    bagItem = it;

                    break;
                }
            }
        }

        if (bagItem != null)
        {
            GameActions.DoubleClick(world, bagItem.Serial);

            await InputProbe.Wait(host, 120);

            inner = ContainerFor(bagItem.Serial);
        }

        string innerName = "inner bag";

        if (inner == null && backpack != null)
        {
            // No bag in the pack: the bank box is a container every
            // character has, and the probe accounts are GMs, so ask for it.
            GD.Print("[GUO] ui probe: no container inside the backpack; saying [bank for a second one");
            GameActions.Say("[bank");

            // The command asks whose bank box; answer with the player.
            for (int f = 0; f < 120 && !world.TargetManager.IsTargeting; f++)
            {
                await InputProbe.Wait(host, 1);
            }

            if (world.TargetManager.IsTargeting)
            {
                world.TargetManager.Target(world.Player.Serial);
            }

            await InputProbe.Wait(host, 180);

            foreach (Gump g in UIManager.Gumps)
            {
                if ((g is ContainerGump || g is GridContainerGump) && !g.IsDisposed && g.LocalSerial != backpack.LocalSerial)
                {
                    inner = g;
                    innerName = "bank box";

                    break;
                }
            }
        }

        if (inner == null)
        {
            GD.Print("[GUO] ui probe: no second container opened");
        }

        if (backpack != null && ContainerPlacement.Active(p))
        {
            Passed &= CheckPlacement("backpack", backpack, viewport, bounds);

            if (inner != null)
            {
                Compat.Rectangle a = Rect(backpack), b = Rect(inner);
                int bottom = bounds.Height - (int)System.Math.Ceiling((GUO.Input.Touch.TouchInput.Bar?.ReservedFraction ?? 0f) * bounds.Height);

                // Two containers whose widths and heights both add up past
                // the screen cannot be apart; then the backpack must stay
                // mostly visible and the new one above the touch bar.
                bool canBeApart = a.Width + b.Width <= bounds.Width || a.Height + b.Height <= bottom;

                if (canBeApart)
                {
                    Passed &= CheckPlacement(innerName, inner, viewport, bounds);

                    bool apart = !a.Intersects(b);

                    GD.Print($"[GUO] ui probe: placement: backpack and {innerName} {(apart ? "apart" : "OVERLAP")}");

                    Passed &= apart;
                }
                else
                {
                    CheckPlacement(innerName, inner, viewport, bounds);

                    Compat.Rectangle o = Compat.Rectangle.Intersect(a, b);
                    long covered = (long)o.Width * o.Height;
                    bool visible = covered * 2 <= (long)a.Width * a.Height;
                    bool aboveBar = b.X >= 0 && b.Right <= bounds.Width && b.Bottom <= bottom;

                    GD.Print(
                        $"[GUO] ui probe: placement: NOTE {innerName} {b.Width}x{b.Height} and backpack cannot both fit a {bounds.Width}x{bottom} client apart; "
                        + $"it covers {covered * 100 / ((long)a.Width * a.Height)}% of the backpack ({(visible ? "backpack mostly visible" : "BACKPACK HIDDEN")}), "
                        + $"{(aboveBar ? "above the bar" : "INTO THE BAR")}"
                    );

                    Passed &= visible && aboveBar;
                }
            }
        }

        // The grid view: the gump the profile asked for, a slot per item.
        if (p.GridContainers && backpack != null)
        {
            bool isGrid = backpack is GridContainerGump;
            int shown = 0, slots = 0, slotSize = 0;

            foreach (Game.UI.Controls.Control c in backpack.Children)
            {
                if (c.GetType().Name == "GridSlot")
                {
                    slots++;
                    slotSize = c.Width;
                    shown += c.LocalSerial != 0 ? 1 : 0;
                }
            }

            int items = 0;

            if (world.Items.TryGetValue(backpack.LocalSerial, out var packItem))
            {
                for (LinkedObject i = packItem.Items; i != null; i = i.Next)
                {
                    items++;
                }
            }

            float physical = slotSize * Client.Game.ScreenScale * (float)DisplayServer.ScreenGetScale();

            GD.Print(
                $"[GUO] ui probe: grid: backpack is {(isGrid ? "a grid" : "NOT A GRID")}; {slots} slots of {slotSize} client px (~{physical:0} physical px), "
                + $"{shown} filled for {items} items in the pack"
            );

            Passed &= isGrid && slots > 0 && shown <= items;
        }

        GD.Print($"[GUO] ui probe: {(Passed ? "ok" : "FAIL")}");
    }

    private static Gump ContainerFor(uint serial) =>
        (Gump)UIManager.GetGump<ContainerGump>(serial) ?? UIManager.GetGump<GridContainerGump>(serial);

    private static string Kind(Gump g) =>
        g is ContainerGump c ? $"classic gump 0x{c.Graphic:X4}" : g is GridContainerGump ? "grid" : g.GetType().Name;

    private static Compat.Rectangle Rect(Gump g) => new Compat.Rectangle(g.X, g.Y, g.Width, g.Height);

    /// <summary>
    /// Inside the client area, above the touch bar, and clear of the middle
    /// of the world view where the character stands.
    /// </summary>
    private static bool CheckPlacement(string name, Gump g, WorldViewportGump viewport, Compat.Rectangle bounds)
    {
        Compat.Rectangle r = Rect(g);
        float bar = GUO.Input.Touch.TouchInput.Bar?.ReservedFraction ?? 0f;
        int bottom = bounds.Height - (int)System.Math.Ceiling(bar * bounds.Height);
        bool inside = r.X >= 0 && r.Y >= 0 && r.Right <= bounds.Width && r.Bottom <= bottom;
        int cx = viewport.X + viewport.Width / 2, cy = viewport.Y + viewport.Height / 2;
        bool clear = !r.Contains(cx, cy);

        GD.Print(
            $"[GUO] ui probe: placement: {name} {r.Width}x{r.Height} at {r.X},{r.Y}; "
            + $"client {bounds.Width}x{bottom} above the bar; character at {cx},{cy}; "
            + $"{(inside ? "inside" : "OUTSIDE")}, {(clear ? "clear of the character" : "OVER THE CHARACTER")}"
        );

        return inside && clear;
    }
}
