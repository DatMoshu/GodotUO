// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner request 2026-11-21): --boat-demo logs in on a dev-shard GM
// account, goes to the Haven docks, puts a small boat on open water, stands
// the character on its deck, turns on styled water with shallow shores and
// ripples, opens the live Water panel and hands the client to the player.
// Dev shards only: it uses staff commands ([go, [add).
using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;

namespace GUO.Host;

internal static class BoatDemo
{
    // Haven docks, Trammel: open water south-west of the pier.
    private const int StartX = 3503, StartY = 2574;

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        string capture = System.Environment.GetEnvironmentVariable("GUO_BOAT_CAPTURE_DIR");
        void Failed(string reason)
        {
            GD.PrintErr("[GUO] boat demo: " + reason);
            if (!string.IsNullOrWhiteSpace(capture)) host.GetTree().Quit(1);
        }
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++) await InputProbe.Wait(host, 1);
        if (Client.Game?.UO?.World == null) { Failed("client did not start."); return; }
        if (!Client.Game.UO.World.InGame) await InputProbe.EnterTheWorld(host, 200);
        var world = Client.Game.UO.World;
        if (!world.InGame) { Failed("never got into the world."); return; }

        await InputProbe.Say(host, "[go Trammel");
        await InputProbe.Wait(host, 120);
        await InputProbe.Say(host, $"[go {StartX} {StartY} 0");
        for (int i = 0; i < 30 && !(world.Player.X == StartX && world.Player.Y == StartY); i++) await InputProbe.Wait(host, 60);
        await InputProbe.Wait(host, 90);

        if (!FindOpenWater(world, StartX, StartY, false, out int bx, out int by, out sbyte bz, out _))
        {
            Failed("no open water near the docks.");
            Game.GameActions.Print(world, "Boat demo: no open water found nearby.");
            return;
        }
        GD.Print($"[GUO] boat demo: open water at {bx},{by},{bz}");

        // Bring the target into interaction range and wait for the server to
        // stream the area before distinguishing the newly placed multi.
        await InputProbe.Say(host, $"[go {bx} {by} {bz}");
        for (int i = 0; i < 180 && (world.Player.X != bx || world.Player.Y != by); i++)
            await InputProbe.Wait(host, 1);
        await InputProbe.Wait(host, 60);
        // Boats outside the player's original update range only arrive after
        // teleport. Check clearance against the now-streamed multis.
        bool tooClose = false;
        foreach (var other in world.Items.Values)
            if (other.IsMulti && Math.Abs(other.X - bx) < 10 && Math.Abs(other.Y - by) < 10)
                tooClose = true;
        if (tooClose && !FindOpenWater(world, bx, by, true, out bx, out by, out bz, out _))
        {
            Failed("no clear berth near the pier.");
            return;
        }
        var existing = new HashSet<uint>();
        foreach (var item in world.Items.Values)
            if (item.IsMulti) existing.Add(item.Serial);
        await InputProbe.Say(host, "[add SmallBoat");
        for (int i = 0; i < 120 && !world.TargetManager.IsTargeting; i++) await InputProbe.Wait(host, 2);
        if (!world.TargetManager.IsTargeting)
        {
            Failed("[add gave no target cursor.");
            return;
        }
        world.TargetManager.Target(0, (ushort)bx, (ushort)by, bz, true);
        await InputProbe.Wait(host, 90);

        Game.GameObjects.Item hull = null;
        foreach (var item in world.Items.Values)
            if (item.IsMulti && !existing.Contains(item.Serial) && Math.Abs(item.X - bx) <= 3 && Math.Abs(item.Y - by) <= 3)
            {
                hull = item;
                break;
            }
        if (hull == null)
        {
            Failed("shard did not return a newly placed boat at the target.");


            return;
        }
        bx = hull.X;
        by = hull.Y;
        bz = hull.Z;
        GD.Print($"[harbor] hull at {bx},{by},{bz}");
        // Stand on the deck: the boat's centre tile, a little above the water.
        await InputProbe.Say(host, $"[go {bx} {by} {bz + 3}");
        await InputProbe.Wait(host, 120);

        var p = ProfileManager.CurrentProfile;
        if (p != null)
        {
            if (!Renderer.Water.WaterRender.IsStyled(p)) Renderer.Water.WaterRender.SetPlayerChoice(p, "uo");
            UoPlusOptions.Set(p, UoPlusFeature.WaterRipples, true, UoPlusOptions.Density(p, UoPlusFeature.WaterRipples));
            UoPlusOptions.Set(p, UoPlusFeature.ShallowShores, true, Math.Max(60, UoPlusOptions.Density(p, UoPlusFeature.ShallowShores)));
            Renderer.Water.WaterRender.Changed(world);
        }
        if (!string.IsNullOrWhiteSpace(capture))
        {
            System.IO.Directory.CreateDirectory(capture);
            Renderer.Water.WaterRender.SetPlayerChoice(p, "lighting");
            Renderer.Water.WaterRender.Changed(world);
            await InputProbe.Wait(host, 60);
            await Take("01-aboard");
            var start = (world.Player.X, world.Player.Y);
            var startHull = (hull.X, hull.Y);
            GD.Print($"[harbor] boarded at hull z={hull.Z}, passenger z={world.Player.Z}");
            bool recording = true;
            int mismatch = 0, peak = 0;
            var film = Record();
            await InputProbe.Wait(host, 35);
            await InputProbe.Say(host, "raise anchor");
            await InputProbe.Wait(host, 20);
            await InputProbe.Say(host, "back");
            for (int frame = 0; frame < 160 && start == (world.Player.X, world.Player.Y); frame++)
                await InputProbe.Wait(host, 1);
            bool moved = start != (world.Player.X, world.Player.Y);
            await Take("02-under-way");
            await InputProbe.Say(host, "turn right");
            await InputProbe.Wait(host, 40);
            await Take("03-turn");
            await InputProbe.Say(host, "stop");
            await InputProbe.Wait(host, 100);
            await Take("04-stopped");
            recording = false;
            await film;
            moved &= (hull.X, hull.Y) != startHull;
            GD.Print($"[harbor] travel observed={moved} start={start} end=({world.Player.X},{world.Player.Y})");
            GD.Print($"[harbor] maximum hull heave={peak}px mismatched passenger samples={mismatch}");
            bool posePassed = System.Environment.GetEnvironmentVariable("GUO_HARBOR_POSE") != "1" || (peak > 0 && mismatch == 0);
            host.GetTree().Quit(moved && posePassed ? 0 : 1);
            return;

            async System.Threading.Tasks.Task Record()
            {
                string frames = System.IO.Path.Combine(capture, "frames");
                System.IO.Directory.CreateDirectory(frames);
                using var csv = new System.IO.StreamWriter(System.IO.Path.Combine(capture, "frames.csv"));
                csv.WriteLine("frame,ms,hullX,hullY,playerX,playerY,hullHeave,playerHeave");
                int frame = 0;
                while (recording)
                {
                    await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var shot = host.GetViewport().GetTexture().GetImage();
                    shot.Resize(960, 554, Image.Interpolation.Nearest);
                    shot.SavePng(System.IO.Path.Combine(frames, $"frame-{frame:D4}.png"));
                    int heave = 0, passenger = 0;
#if GUO_LABS
                    heave = hull.HarborPixels;
                    passenger = world.Player.HarborPixels;
                    peak = Math.Max(peak, Math.Abs(heave));
                    if (passenger != heave) mismatch++;
#endif
                    csv.WriteLine($"{frame},{Godot.Time.GetTicksMsec()},{hull.X},{hull.Y},{world.Player.X},{world.Player.Y},{heave},{passenger}");
                    frame++;
                    await InputProbe.Wait(host, 3);
                }
            }

            async System.Threading.Tasks.Task Take(string name)
            {
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = host.GetViewport().GetTexture().GetImage();
                var error = image.SavePng(System.IO.Path.Combine(capture, name + ".png"));
                if (error != Error.Ok) GD.PrintErr($"[harbor] capture {name}: {error}");
            }
        }
        Game.UI.Gumps.WaterQuickGump.Toggle(world);

        GD.Print($"[GUO] boat demo: aboard at {world.Player.X},{world.Player.Y},{world.Player.Z}");
        Game.GameActions.Print(world, "Your boat is ready. Steer by saying: forward, back, left, right, forward left, forward right, turn left, turn right, come about, stop. Type -water to show or hide the Water panel.");
    }

    // The nearest wet land tile whose 7x7 neighbourhood is all wet land and
    // free of statics (room for a small boat), searched in rings from the start.
    private static bool FindOpenWater(Game.World world, int centerX, int centerY, bool checkBoats, out int bx, out int by, out sbyte bz, out ushort graphic)
    {
        var tiles = Client.Game.UO.FileManager.TileData;
        bool Wet(int x, int y, out sbyte z, out ushort g)
        {
            z = 0; g = 0;
            var o = world.Map.GetTile(x, y);
            bool wetLand = false;
            for (; o != null; o = o.TNext)
            {
                if (o is Game.GameObjects.Land land)
                {
                    g = land.Graphic; z = land.Z;
                    wetLand = land.Graphic < tiles.LandData.Length && (tiles.LandData[land.Graphic].Flags & TileFlag.Wet) != 0;
                }
                else if (o is Game.GameObjects.Static or Game.GameObjects.Multi) return false;
            }
            return wetLand;
        }

        for (int r = 4; r < 40; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                int cx = centerX + dx, cy = centerY + dy;
                if (checkBoats)
                {
                    bool occupied = false;
                    foreach (var other in world.Items.Values)
                        if (other.IsMulti && Math.Abs(other.X - cx) < 10 && Math.Abs(other.Y - cy) < 10)
                            occupied = true;
                    if (occupied) continue;
                }
                if (!Wet(cx, cy, out bz, out graphic)) continue;
                bool room = true;
                for (int yy = -3; yy <= 3 && room; yy++)
                for (int xx = -3; xx <= 3 && room; xx++)
                    room = Wet(cx + xx, cy + yy, out _, out _);
                if (!room) continue;
                bx = cx; by = cy;
                return true;
            }
        }
        bx = by = 0; bz = 0; graphic = 0;
        return false;
    }
}
