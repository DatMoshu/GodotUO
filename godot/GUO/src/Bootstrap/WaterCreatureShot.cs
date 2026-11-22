// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (task 2387, creature ripples): --water-look-shot --water-look-only creatures|scout|audit.
// Dev shards only: it uses staff commands ([go, [add, [remove). Scratch profile.
//   scout      look around GUO_CREATURE_SITES ("x y;x y") for a pier beside a beach and open water, and print them
//   audit      stand at the Haven dock for a moment and list the multis in view (so test boats can be found)
//   creatures  add GUO_CREATURE_COUNT creatures (GUO_CREATURE_NAME) at open water near GUO_CREATURE_SITE ("x y"),
//              film GUO_CREATURE_SECS seconds with per-frame ripple numbers, remove them again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;
using GUO.Renderer.Water;

namespace GUO.Host;

internal static class WaterCreatureShot
{
    private static string Env(string name, string fallback) =>
        System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static (int X, int Y) Pair(string s)
    {
        var v = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(v[0]), int.Parse(v[1]));
    }

    internal static async System.Threading.Tasks.Task<bool> Run(Node host, Game.World world, string dir, string mode)
    {
        if (mode == "audit") return await Audit(host, world);
        if (mode == "scout") return await Scout(host, world);
        return await Creatures(host, world, dir);
    }

    // ---- terrain questions ------------------------------------------------

    private static bool Water(Game.World world, int x, int y, out sbyte z)
    {
        z = 0; bool any = false;
        sbyte? sw = null;
        for (var o = world.Map.GetTile(x, y); o != null; o = o.TNext)
        {
            if (o is Land l && (l.Graphic is >= 168 and <= 171 or >= 310 and <= 311)) { z = l.Z; any = true; }
            else if ((o is Static or Multi or Item) && o.Graphic >= 0x1796 && o.Graphic <= 0x17B2) sw = o.Z;
        }
        if (sw.HasValue) { z = sw.Value; any = true; }
        return any;
    }

    // A static standing at the water's surface or above it: a plank, a pier post, a house.
    private static bool Raised(Game.World world, int x, int y, sbyte waterZ)
    {
        for (var o = world.Map.GetTile(x, y); o != null; o = o.TNext)
            if (o is Static s && !s.ItemData.IsWet && s.Z + 1 >= waterZ && s.ItemData.Height > 0) return true;
        return false;
    }

    private static bool Open(Game.World world, int x, int y) => Water(world, x, y, out sbyte z) && !Raised(world, x, y, z);

    private static bool Beach(Game.World world, int x, int y)
    {
        bool land = false;
        for (var o = world.Map.GetTile(x, y); o != null; o = o.TNext)
        {
            if (o is Land l && !(l.Graphic is >= 168 and <= 171 or >= 310 and <= 311)) land = true;
            else if (o is Static s && (s.ItemData.IsWet || s.Graphic is >= 0x1796 and <= 0x17B2)) return false;
        }
        return land;
    }

    private static async System.Threading.Tasks.Task<bool> Scout(Node host, Game.World world)
    {
        foreach (string site in Env("GUO_CREATURE_SITES", "2890 680").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (sx, sy) = Pair(site);
            await InputProbe.Say(host, $"[go {sx} {sy} 0");
            await InputProbe.Wait(host, 90);
            int found = 0;
            for (int r = 0; r < 28 && found < 4; r++)
            for (int dy = -r; dy <= r && found < 4; dy++)
            for (int dx = -r; dx <= r && found < 4; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                int x = world.Player.X + dx, y = world.Player.Y + dy;
                // A pier cell: raised over the water, with open water within 2 and a beach within 6.
                if (!Water(world, x, y, out sbyte z) || !Raised(world, x, y, z)) continue;
                bool open = false, beach = false; int ox = 0, oy = 0;
                for (int j = -2; j <= 2 && !open; j++) for (int i = -2; i <= 2; i++) if (Open(world, x + i, y + j)) { open = true; ox = x + i; oy = y + j; break; }
                for (int j = -6; j <= 6 && !beach; j++) for (int i = -6; i <= 6; i++) if (Beach(world, x + i, y + j)) { beach = true; break; }
                if (open && beach) { found++; GD.Print($"[creature scout] site {site}: pier {x},{y} z={z}, open water {ox},{oy}"); }
            }
            if (found == 0) GD.Print($"[creature scout] site {site}: nothing (player at {world.Player.X},{world.Player.Y})");
        }
        return true;
    }

    private static async System.Threading.Tasks.Task<bool> Audit(Node host, Game.World world)
    {
        await InputProbe.Say(host, "[go 3522 2591 -3");
        await InputProbe.Wait(host, 150);
        foreach (var it in world.Items.Values)
            if (it.IsMulti && it.OnGround && Math.Abs(it.X - 3513) < 60 && Math.Abs(it.Y - 2615) < 60)
                GD.Print($"[creature audit] multi 0x{it.Serial:X8} graphic 0x{it.Graphic:X4} at {it.X},{it.Y},{it.Z}");
        await InputProbe.Say(host, "[go 2890 680 0");
        await InputProbe.Wait(host, 60);
        return true;
    }

    // ---- the film ---------------------------------------------------------

    private static async System.Threading.Tasks.Task<bool> Creatures(Node host, Game.World world, string dir)
    {
        var (sx, sy) = Pair(Env("GUO_CREATURE_SITE", "2890 680"));
        int count = int.Parse(Env("GUO_CREATURE_COUNT", "1"));
        string name = Env("GUO_CREATURE_NAME", "Dolphin");
        double secs = double.Parse(Env("GUO_CREATURE_SECS", "30"), System.Globalization.CultureInfo.InvariantCulture);
        string tag = Env("GUO_CREATURE_TAG", "swim");

        // The water look: the fresh-launch default, or a named preset (GUO_CREATURE_PRESET) to compare.
        var look = Env("GUO_CREATURE_PRESET", "") is { Length: > 0 } pn
            ? Array.Find(WaterLookShot.Presets, q => q.Name == pn)
            : WaterLookShot.FromProfile(GUO.Configuration.Profile.CreatePlayerDefaults());
        WaterLookShot.Apply(world, GUO.Configuration.ProfileManager.CurrentProfile, look);
        await InputProbe.Say(host, $"[go {sx} {sy} 0");
        await InputProbe.Wait(host, 120);
        // Open water for the swimmers: the cell nearest the site that is water without a pier over it,
        // or GUO_CREATURE_SPOT="x y" exactly (a water cell beside a pier).
        int wx = 0, wy = 0; bool have = false;
        if (Env("GUO_CREATURE_SPOT", "") is { Length: > 0 } spot) { (wx, wy) = Pair(spot); have = true; }
        for (int r = 0; r < 40 && !have; r++)
        for (int dy = -r; dy <= r && !have; dy++)
        for (int dx = -r; dx <= r && !have; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = world.Player.X + dx, y = world.Player.Y + dy;
            if (!Open(world, x, y)) continue;
            bool room = true;
            for (int j = -4; j <= 4 && room; j++) for (int i = -4; i <= 4 && room; i++) room = Open(world, x + i, y + j);
            if (room) { wx = x; wy = y; have = true; }
        }
        if (!have) { GD.PrintErr("[creature] no open water near the site"); return false; }
        GD.Print($"[creature] open water at {wx},{wy}; player at {world.Player.X},{world.Player.Y}; {count} x {name}");
        Water(world, wx, wy, out sbyte wz);
        await InputProbe.Say(host, $"[go {wx} {wy} {wz}");
        await InputProbe.Wait(host, 90);

        var before = new HashSet<uint>(world.Mobiles.Keys);
        for (int i = 0; i < count; i++)
        {
            await InputProbe.Say(host, $"[add {name}");
            for (int f = 0; f < 120 && !world.TargetManager.IsTargeting; f++) await InputProbe.Wait(host, 2);
            if (!world.TargetManager.IsTargeting) { GD.PrintErr($"[creature] [add {name} gave no target"); break; }
            int tx = wx + (i % 3) - 1, ty = wy + 2 + (i / 3);
            if (!Water(world, tx, ty, out sbyte tz)) { tx = wx; ty = wy + 1; tz = wz; }
            ushort g = 0;
            for (var o = world.Map.GetTile(tx, ty); o != null; o = o.TNext)
                if (o is Static && o.Graphic >= 0x1796 && o.Graphic <= 0x17B2) { g = o.Graphic; tz = o.Z; }
            world.TargetManager.Target(g, (ushort)tx, (ushort)ty, tz, true);
            await InputProbe.Wait(host, 20);
        }
        await InputProbe.Wait(host, 60);
        var added = world.Mobiles.Keys.Where(k => !before.Contains(k)).ToList();
        GD.Print($"[creature] added {added.Count}: {string.Join(",", added.Select(k => "0x" + k.ToString("X8")))}");

        string frames = Path.Combine(dir, "frames-" + tag);
        Directory.CreateDirectory(frames);
        using var csv = new StreamWriter(Path.Combine(dir, $"frames-{tag}.csv"));
        csv.WriteLine("frame,ms,frameMs,rippleMs,stamps,steps,swimStamps,moving,still,peak,dryTexels");
        int frame = 0, shots = 0, stillShots = 0;
        ulong t0 = Godot.Time.GetTicksMsec();
        var frameMsList = new List<double>(); var rippleMs = new List<double>();
        float peakAll = 0; int dryAll = 0, maxMoving = 0;
        ulong last = Godot.Time.GetTicksUsec();
        while (Godot.Time.GetTicksMsec() - t0 < secs * 1000)
        {
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            ulong now = Godot.Time.GetTicksUsec();
            double fms = (now - last) / 1000.0;
            int moving = 0, still = 0;
            foreach (uint s in added)
            {
                if (!world.Mobiles.TryGetValue(s, out var m) || m.IsDestroyed) continue;
                if (m.Steps.Count > 0 || m.Offset.X != 0 || m.Offset.Y != 0) moving++; else still++;
            }
            var r = WaterRender.Ripples;
            maxMoving = Math.Max(maxMoving, moving);
            peakAll = Math.Max(peakAll, r.PeakHeight); dryAll = Math.Max(dryAll, r.DryTexels);
            csv.WriteLine($"{frame},{Godot.Time.GetTicksMsec() - t0},{fms:F2},{r.LastUpdateMs:F3},{r.Stamps},{r.Steps},{r.SwimStamps},{moving},{still},{r.PeakHeight:F3},{r.DryTexels}");
            frameMsList.Add(fms); rippleMs.Add(r.LastUpdateMs);
            using (var shot = host.GetViewport().GetTexture().GetImage())
            {
                string kind = moving > 0 ? "moving" : (still > 0 ? "still" : "none");
                if (kind == "moving" && shots < 6 && frame % 40 == 0) shot.SavePng(Path.Combine(dir, $"still-{tag}-moving-{shots++}.png"));
                else if (kind == "still" && stillShots < 4 && frame % 40 == 0) shot.SavePng(Path.Combine(dir, $"still-{tag}-calm-{stillShots++}.png"));
                if (frame % 3 == 0)
                {
                    shot.Resize(shot.GetWidth() / 2, shot.GetHeight() / 2, Image.Interpolation.Nearest);
                    shot.SavePng(Path.Combine(frames, $"frame-{frame:D4}.png"));
                }
            }
            frame++;
            await InputProbe.Wait(host, 1);
            last = Godot.Time.GetTicksUsec();
        }
        frameMsList.Sort(); rippleMs.Sort();
        GD.Print($"[creature] {tag}: frames={frame} frame ms median={frameMsList[frameMsList.Count / 2]:F2} p95={frameMsList[(int)(frameMsList.Count * 0.95)]:F2}; ripple update ms median={rippleMs[rippleMs.Count / 2]:F3} p95={rippleMs[(int)(rippleMs.Count * 0.95)]:F3} max={rippleMs[^1]:F3}");
        GD.Print($"[creature] {tag}: swimStamps={WaterRender.Ripples.SwimStamps} maxMoving={maxMoving} peakHeight={peakAll:F3} maxDryTexels={dryAll}");

        // Leave nothing behind.
        foreach (uint s in added)
            if (world.Mobiles.ContainsKey(s)) await RemoveMobile(host, world, s);
        await InputProbe.Say(host, "[go 2890 680 0");
        await InputProbe.Wait(host, 30);
        return dryAll == 0;
    }

    private static async System.Threading.Tasks.Task RemoveMobile(Node host, Game.World world, uint serial)
    {
        await InputProbe.Say(host, "[remove");
        for (int f = 0; f < 120 && !world.TargetManager.IsTargeting; f++) await InputProbe.Wait(host, 2);
        if (!world.TargetManager.IsTargeting) { GD.PrintErr($"[creature] [remove gave no target for 0x{serial:X8}"); return; }
        world.TargetManager.Target(serial);
        await InputProbe.Wait(host, 30);
        GD.Print($"[creature] removed 0x{serial:X8}");
    }
}
