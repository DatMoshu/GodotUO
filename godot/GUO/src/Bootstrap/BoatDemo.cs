// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner request 2026-11-21): --boat-demo logs in on a dev-shard GM
// account, goes to the Haven docks, puts a small boat on open water, stands
// the character on its deck, turns on styled water with shallow shores and
// ripples, opens the live Water panel and hands the client to the player.
// Dev shards only: it uses staff commands ([go, [add).
using System;
using Godot;
using GUO.Assets;
using GUO.Configuration;

namespace GUO.Host;

internal static class BoatDemo
{
    // Haven docks, Trammel: open water south-west of the pier.
    private const int StartX = 3503, StartY = 2574;

    public static async System.Threading.Tasks.Task Run(Node host, string shotDir = "")
    {
        string capture = System.Environment.GetEnvironmentVariable("GUO_BOAT_CAPTURE_DIR");
        bool passed = await RunDemo(host, shotDir, capture);
        if (!string.IsNullOrWhiteSpace(capture)) host.GetTree().Quit(passed ? 0 : 1);
    }

    private static async System.Threading.Tasks.Task<bool> RunDemo(Node host, string shotDir, string capture)
    {
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++) await InputProbe.Wait(host, 1);
        if (Client.Game?.UO?.World == null) return false;
        if (!Client.Game.UO.World.InGame) await InputProbe.EnterTheWorld(host, 200);
        var world = Client.Game.UO.World;
        if (!world.InGame) { GD.PrintErr("[GUO] boat demo: never got into the world."); return false; }

        await InputProbe.Say(host, "[go Trammel");
        await InputProbe.Wait(host, 120);
        await InputProbe.Say(host, $"[go {StartX} {StartY} 0");
        for (int i = 0; i < 30 && !(world.Player.X == StartX && world.Player.Y == StartY); i++) await InputProbe.Wait(host, 60);
        await InputProbe.Wait(host, 90);

        if (!FindOpenWater(world, out int bx, out int by, out sbyte bz, out ushort graphic))
        {
            GD.PrintErr("[GUO] boat demo: no open water near the docks.");
            Game.GameActions.Print(world, "Boat demo: no open water found nearby.");
            return false;
        }
        GD.Print($"[GUO] boat demo: open water at {bx},{by},{bz}");
        // A boat deed's placement target only reaches 7 tiles: stand just off the
        // boat's beam first (staff can stand on water).
        int sx = bx + 5, sy = by;
        await InputProbe.Say(host, $"[go {sx} {sy} {bz}");
        for (int i = 0; i < 30 && !(world.Player.X == sx && world.Player.Y == sy); i++) await InputProbe.Wait(host, 10);

        // The way a player does it: a deed in the pack, used, aimed at the water.
        var backpack = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
        if (backpack == null) { Fail(world, "no backpack"); return false; }
        await InputProbe.Say(host, "[AddToPack SmallBoatDeed");
        if (!await WaitTarget(host, world)) { Fail(world, "[AddToPack gave no target cursor (is this a staff account?)"); return false; }
        world.TargetManager.Target(world.Player.Serial);
        Game.GameObjects.Item deed = null;
        for (int i = 0; i < 60 && deed == null; i++)
        {
            await InputProbe.Wait(host, 5);
            foreach (var it in world.Items.Values)
                if (it.Container == backpack.Serial && it.Graphic == 0x14F2 && (deed == null || it.Serial > deed.Serial)) deed = it;
        }
        if (deed == null) { Fail(world, "no boat deed arrived in the backpack"); return false; }

        var before = new System.Collections.Generic.HashSet<uint>();
        foreach (var it in world.Items.Values) if (it.IsMulti) before.Add(it.Serial);
        Game.GameActions.DoubleClick(world, deed.Serial);
        if (!await WaitTarget(host, world)) { Fail(world, "the deed gave no placement cursor"); return false; }
        // Answer as a player's click on that water would (GameSceneInputHandler):
        // the water static when there is one, else the land with graphic 0. The
        // shard re-reads a land target's z from the terrain, so sea drawn as a
        // static over deeper land must be targeted as that static.
        world.TargetManager.Target(graphic, (ushort)bx, (ushort)by, bz, true);

        Game.GameObjects.Item boat = null;
        for (int i = 0; i < 60 && boat == null; i++)
        {
            await InputProbe.Wait(host, 5);
            foreach (var it in world.Items.Values)
                if (it.IsMulti && !before.Contains(it.Serial) && Math.Abs(it.X - bx) <= 3 && Math.Abs(it.Y - by) <= 3) boat = it;
        }
        if (boat == null)
        {
            for (int yy = -6; yy <= 6; yy += 3)
            for (int xx = -2; xx <= 2; xx += 2)
            {
                var sb = new System.Text.StringBuilder();
                for (var o = world.Map.GetTile(bx + xx, by + yy); o != null; o = o.TNext)
                    sb.Append($" {o.GetType().Name}:0x{o.Graphic:X4}@{o.Z}");
                GD.PrintErr($"[GUO] boat demo tile {bx + xx},{by + yy}:{sb}");
            }
            GD.PrintErr($"[GUO] boat demo player {world.Player.X},{world.Player.Y},{world.Player.Z} map {world.MapIndex}");
            Fail(world, $"the shard placed no boat at {bx},{by},{bz} (see the journal)");
            return false;
        }
        GD.Print($"[GUO] boat demo: boat 0x{boat.Serial:X8} at {boat.X},{boat.Y},{boat.Z}");

        // Stand on the deck at the boat's centre, then check we are there.
        await InputProbe.Say(host, $"[go {boat.X} {boat.Y} {boat.Z + 3}");
        for (int i = 0; i < 30 && !(world.Player.X == boat.X && world.Player.Y == boat.Y); i++) await InputProbe.Wait(host, 10);
        if (!(world.Player.X == boat.X && world.Player.Y == boat.Y)) { Fail(world, "could not step onto the boat"); return false; }

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
            var startHull = (boat.X, boat.Y);
            GD.Print($"[harbor] boarded at hull z={boat.Z}, passenger z={world.Player.Z}");
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
            moved &= (boat.X, boat.Y) != startHull;
            GD.Print($"[harbor] travel observed={moved} start={start} end=({world.Player.X},{world.Player.Y})");
            GD.Print($"[harbor] maximum hull heave={peak}px mismatched passenger samples={mismatch}");
            bool posePassed = System.Environment.GetEnvironmentVariable("GUO_HARBOR_POSE") != "1" || (peak > 0 && mismatch == 0);
            return moved && posePassed;

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
                    heave = boat.HarborPixels;
                    passenger = world.Player.HarborPixels;
                    peak = Math.Max(peak, Math.Abs(heave));
                    if (passenger != heave) mismatch++;
#endif
                    csv.WriteLine($"{frame},{Godot.Time.GetTicksMsec()},{boat.X},{boat.Y},{world.Player.X},{world.Player.Y},{heave},{passenger}");
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

        if (!string.IsNullOrWhiteSpace(shotDir))
        {
            await InputProbe.Wait(host, 120);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string path = shotDir.PathJoin("boat_demo.png");
            host.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[GUO] boat demo shot -> {ProjectSettings.GlobalizePath(path)}");
        }
        GD.Print($"[GUO] boat demo: aboard at {world.Player.X},{world.Player.Y},{world.Player.Z}");
        Game.GameActions.Print(world, "Your boat is ready (anchored). Say 'raise anchor', then steer: forward, back, left, right, forward left, forward right, turn left, turn right, come about, stop. Type -water to show or hide the Water panel.");
        return true;
    }

    private static async System.Threading.Tasks.Task<bool> WaitTarget(Node host, Game.World world)
    {
        for (int i = 0; i < 120 && !world.TargetManager.IsTargeting; i++) await InputProbe.Wait(host, 2);
        return world.TargetManager.IsTargeting;
    }

    private static void Fail(Game.World world, string why)
    {
        GD.PrintErr("[GUO] boat demo: FAIL " + why);
        var entries = Game.Managers.JournalManager.Entries;
        for (int j = Math.Max(0, entries.Count - 8); j < entries.Count; j++)
            GD.PrintErr($"[GUO] boat demo journal: {entries[j].Name}: {entries[j].Text}");
        Game.GameActions.Print(world, "Boat demo: " + why + ".");
    }

    // The nearest spot where a small boat fits by the shard's own rule
    // (ModernUO BaseBoat.CanFit): every tile under the hull is sea water at
    // one z -- land 168-171/310-311 or a water static 0x1796-0x17B2 at that z --
    // with nothing else standing at or above it. Searched in rings from the start.
    private static bool FindOpenWater(Game.World world, out int bx, out int by, out sbyte bz, out ushort graphic)
    {
        bool Sea(int x, int y, out sbyte z) => SeaTile(x, y, out z, out _);
        bool SeaTile(int x, int y, out sbyte z, out ushort target)
        {
            z = 0; target = 0; ushort waterG = 0;
            sbyte landZ = 0; ushort landG = 0; bool haveLand = false;
            sbyte? staticWaterZ = null; sbyte otherTop = sbyte.MinValue;
            for (var o = world.Map.GetTile(x, y); o != null; o = o.TNext)
            {
                if (o is Game.GameObjects.Land land) { landZ = land.Z; landG = land.Graphic; haveLand = true; }
                else if (o is Game.GameObjects.Static or Game.GameObjects.Multi)
                {
                    if (o.Graphic >= 0x1796 && o.Graphic <= 0x17B2) { staticWaterZ = o.Z; waterG = o.Graphic; }
                    else otherTop = Math.Max(otherTop, o.Z);
                }
            }
            bool seaLand = haveLand && (landG is >= 168 and <= 171 or >= 310 and <= 311);
            if (staticWaterZ.HasValue) { z = staticWaterZ.Value; target = waterG; }
            else if (seaLand) z = landZ;
            else return false;
            return otherTop < z;
        }

        for (int r = 2; r < 48; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                int cx = StartX + dx, cy = StartY + dy;
                if (!SeaTile(cx, cy, out bz, out graphic)) continue;
                // A north-facing small boat spans about 5 x 13 tiles: keep a
                // margin of one tile all round.
                bool room = true;
                for (int yy = -7; yy <= 7 && room; yy++)
                for (int xx = -3; xx <= 3 && room; xx++)
                    room = Sea(cx + xx, cy + yy, out sbyte z2) && z2 == bz;
                if (!room) continue;
                bx = cx; by = cy;
                return true;
            }
        }
        bx = by = 0; bz = 0; graphic = 0;
        return false;
    }
}
