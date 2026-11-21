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
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++) await InputProbe.Wait(host, 1);
        if (!Client.Game.UO.World.InGame) await InputProbe.EnterTheWorld(host, 200);
        var world = Client.Game.UO.World;
        if (!world.InGame) { GD.PrintErr("[GUO] boat demo: never got into the world."); return; }

        await InputProbe.Say(host, "[go Trammel");
        await InputProbe.Wait(host, 120);
        await InputProbe.Say(host, $"[go {StartX} {StartY} 0");
        for (int i = 0; i < 30 && !(world.Player.X == StartX && world.Player.Y == StartY); i++) await InputProbe.Wait(host, 60);
        await InputProbe.Wait(host, 90);

        if (!FindOpenWater(world, out int bx, out int by, out sbyte bz, out ushort graphic))
        {
            GD.PrintErr("[GUO] boat demo: no open water near the docks.");
            Game.GameActions.Print(world, "Boat demo: no open water found nearby.");
            return;
        }
        GD.Print($"[GUO] boat demo: open water at {bx},{by},{bz}");

        // The way a player does it: a deed in the pack, used, aimed at the water.
        var backpack = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
        if (backpack == null) { Fail(world, "no backpack"); return; }
        await InputProbe.Say(host, "[AddToPack SmallBoatDeed");
        if (!await WaitTarget(host, world)) { Fail(world, "[AddToPack gave no target cursor (is this a staff account?)"); return; }
        world.TargetManager.Target(world.Player.Serial);
        Game.GameObjects.Item deed = null;
        for (int i = 0; i < 60 && deed == null; i++)
        {
            await InputProbe.Wait(host, 5);
            foreach (var it in world.Items.Values)
                if (it.Container == backpack.Serial && it.Graphic == 0x14F2 && (deed == null || it.Serial > deed.Serial)) deed = it;
        }
        if (deed == null) { Fail(world, "no boat deed arrived in the backpack"); return; }

        var before = new System.Collections.Generic.HashSet<uint>();
        foreach (var it in world.Items.Values) if (it.IsMulti) before.Add(it.Serial);
        Game.GameActions.DoubleClick(world, deed.Serial);
        if (!await WaitTarget(host, world)) { Fail(world, "the deed gave no placement cursor"); return; }
        world.TargetManager.Target(graphic, (ushort)bx, (ushort)by, bz, true);

        Game.GameObjects.Item boat = null;
        for (int i = 0; i < 60 && boat == null; i++)
        {
            await InputProbe.Wait(host, 5);
            foreach (var it in world.Items.Values)
                if (it.IsMulti && !before.Contains(it.Serial) && Math.Abs(it.X - bx) <= 3 && Math.Abs(it.Y - by) <= 3) boat = it;
        }
        if (boat == null) { Fail(world, $"the shard placed no boat at {bx},{by} (see the journal)"); return; }
        GD.Print($"[GUO] boat demo: boat 0x{boat.Serial:X8} at {boat.X},{boat.Y},{boat.Z}");

        // Stand on the deck at the boat's centre, then check we are there.
        await InputProbe.Say(host, $"[go {boat.X} {boat.Y} {boat.Z + 3}");
        for (int i = 0; i < 30 && !(world.Player.X == boat.X && world.Player.Y == boat.Y); i++) await InputProbe.Wait(host, 10);
        if (!(world.Player.X == boat.X && world.Player.Y == boat.Y)) { Fail(world, "could not step onto the boat"); return; }

        var p = ProfileManager.CurrentProfile;
        if (p != null)
        {
            if (!Renderer.Water.WaterRender.IsStyled(p)) Renderer.Water.WaterRender.SetPlayerChoice(p, "uo");
            UoPlusOptions.Set(p, UoPlusFeature.WaterRipples, true, UoPlusOptions.Density(p, UoPlusFeature.WaterRipples));
            UoPlusOptions.Set(p, UoPlusFeature.ShallowShores, true, Math.Max(60, UoPlusOptions.Density(p, UoPlusFeature.ShallowShores)));
            Renderer.Water.WaterRender.Changed(world);
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

    // The nearest wet land tile whose 7x7 neighbourhood is all wet land and
    // free of statics (room for a small boat), searched in rings from the start.
    private static bool FindOpenWater(Game.World world, out int bx, out int by, out sbyte bz, out ushort graphic)
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
                int cx = StartX + dx, cy = StartY + dy;
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
