// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner request 2026-11-21): --water-look-shot photographs the Haven
// dock under candidate water presets from one camera, then films a boat under
// way, stopping, beside moored boats and swimming dolphins, with per-frame water
// timings. Dev shards only: it uses staff commands ([go, [add). Scratch profile.
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using GUO.Configuration;
using GUO.Renderer.Water;

namespace GUO.Host;

internal static class WaterLookShot
{
    // The owner's reference camera: standing on the Haven pier among the moored boats.
    private const int DockX = 3522, DockY = 2591, DockZ = -3;

    // Where the boat is looked for (open sea); GUO_WAKE_START="x y" moves it when the harbor is crowded.
    private static readonly (int X, int Y) WakeStart = ParseStart();
    private static int WakeStartX => WakeStart.X;
    private static int WakeStartY => WakeStart.Y;
    private static (int, int) ParseStart()
    {
        var v = System.Environment.GetEnvironmentVariable("GUO_WAKE_START")?.Split(' ');
        return v is { Length: 2 } && int.TryParse(v[0], out int a) && int.TryParse(v[1], out int b) ? (a, b) : (3503, 2574);
    }

    public static bool Passed { get; private set; }

    /// <summary>--water-look-only presets|wake: run one half.</summary>
    public static string Only { get; set; } = "";

    internal sealed record Preset(string Name, string Style, int Speed, int Wave, int Foam, int Glint, int Ripples, int Shallow);

    // Candidates for the owner's right-hand reference. "default" is
    // Profile.CreatePlayerDefaults; the others are the styles' authored values
    // and the earlier fresh-profile default (7742dc7f).
    internal static readonly Preset[] Presets =
    {
        new("classic", "classic", 100, 100, 100, 100, 0, 0),
        new("default", "", 0, 0, 0, 0, 0, 0),
        new("uo-default-no-shallow", "uo", 56, 21, 26, 15, 65, 0),
        new("lighting-7742", "lighting", 80, 85, 55, 30, 65, 45),
        new("lighting-authored", "lighting", 100, 100, 100, 100, 100, 100),
        new("lighting-7742-shallow25", "lighting", 80, 85, 55, 30, 65, 25),
        new("lighting-7742-no-shallow", "lighting", 80, 85, 55, 30, 65, 0),
        new("lighting-7742-glint15", "lighting", 80, 85, 55, 15, 65, 45),
        new("lighting-s10", "lighting", 80, 85, 55, 30, 65, 10),
        new("lighting-s20-foam25", "lighting", 80, 85, 25, 20, 65, 20),
        new("lighting-s20-foam25-wave60", "lighting", 60, 60, 25, 15, 65, 20),
        new("lighting-s30-foam25-wave60", "lighting", 60, 60, 25, 15, 65, 30),
        new("lighting-s0-foam25-wave60", "lighting", 60, 60, 25, 15, 65, 0),
        new("uo-s20", "uo", 56, 21, 26, 15, 65, 20),
    };

    public static async System.Threading.Tasks.Task Run(Node host, string dir)
    {
        try
        {
            Passed = await RunAll(host, dir);
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water look] FAIL " + ex);
            Passed = false;
        }
        finally
        {
            // A look applied for a capture must not outlive it in a saved profile.
            if (Client.Game?.UO?.World is { } w && ProfileManager.CurrentProfile is { } cp)
                Apply(w, cp, FromProfile(Profile.CreatePlayerDefaults()));
        }
    }

    private static async System.Threading.Tasks.Task<bool> RunAll(Node host, string dir)
    {
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++) await InputProbe.Wait(host, 1);
        if (Client.Game?.UO?.World == null) return false;
        if (!Client.Game.UO.World.InGame) await InputProbe.EnterTheWorld(host, 200);
        var world = Client.Game.UO.World;
        if (!world.InGame) { GD.PrintErr("[water look] never got into the world."); return false; }
        dir = string.IsNullOrWhiteSpace(dir) ? ProjectSettings.GlobalizePath("user://water_look") : dir;
        Directory.CreateDirectory(dir);
        var profile = ProfileManager.CurrentProfile;
        // GUO_WATER_LIGHT=0..30 forces an absolute client light level (0 = full daylight) for the capture.
        if (byte.TryParse(System.Environment.GetEnvironmentVariable("GUO_WATER_LIGHT"), out byte forcedLight))
        { profile.UseCustomLightLevel = true; profile.LightLevel = forcedLight; profile.LightLevelType = 0; }

        InputProbe.FullSizeGameWindow();
        if (float.TryParse(System.Environment.GetEnvironmentVariable("GUO_WAKE_ZOOM"), out float zoom) && zoom > 0
            && Client.Game.GetScene<Game.Scenes.GameScene>()?.Camera is { } cam) cam.Zoom = zoom;
        var amps = System.Environment.GetEnvironmentVariable("GUO_WAKE_AMPS")?.Split(' ');
        if (amps is { Length: 3 } && float.TryParse(amps[0], out float wt) && float.TryParse(amps[1], out float wa) && float.TryParse(amps[2], out float wb))
        {
            WaterRipples.WakeTrough = wt; WaterRipples.WakeArm = wa; WaterRipples.WakeBow = wb;
            GD.Print($"[water look] wake amplitudes {wt} {wa} {wb}");
        }
        if (Only is "creatures" or "scout" or "audit")
            return await WaterCreatureShot.Run(host, world, dir, Only);
        await InputProbe.Say(host, "[go Trammel");
        await InputProbe.Wait(host, 120);
        string cleanup = System.Environment.GetEnvironmentVariable("GUO_WAKE_REMOVE");
        if (!string.IsNullOrWhiteSpace(cleanup))
        {
            // Staff [remove on test boats an earlier run left (serials, hex, comma separated).
            foreach (string t in cleanup.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await RemoveItem(host, world, Convert.ToUInt32(t, 16));
            return true;
        }
        bool ok = true;
        if (Only != "wake")
        {
            await GoTo(host, world, DockX, DockY, DockZ);
            var fresh = Profile.CreatePlayerDefaults();
            foreach (Preset p in Presets)
            {
                Apply(world, profile, p.Style == "" ? FromProfile(fresh) : p);
                await InputProbe.Wait(host, 150);
                await Take(host, Path.Combine(dir, $"preset-{p.Name}.png"));
                GD.Print($"[water look] preset {p.Name} captured");
            }
        }
        if (Only != "presets")
        {
            Apply(world, profile, FromProfile(Profile.CreatePlayerDefaults()));
            ok &= await Wake(host, world, dir);
        }
        return ok;
    }

    internal static Preset FromProfile(Profile f) => new("default", f.WaterStyle, f.WaterTuneSpeed, f.WaterTuneWave,
        f.WaterTuneFoam, f.WaterTuneGlint,
        UoPlusOptions.Enabled(f, UoPlusFeature.WaterRipples) ? UoPlusOptions.Density(f, UoPlusFeature.WaterRipples) : 0,
        UoPlusOptions.Enabled(f, UoPlusFeature.ShallowShores) ? UoPlusOptions.Density(f, UoPlusFeature.ShallowShores) : 0);

    internal static void Apply(Game.World world, Profile profile, Preset p)
    {
        WaterRender.SetPlayerChoice(profile, p.Style);
        profile.WaterTuneSpeed = p.Speed;
        profile.WaterTuneWave = p.Wave;
        profile.WaterTuneFoam = p.Foam;
        profile.WaterTuneGlint = p.Glint;
        UoPlusOptions.Set(profile, UoPlusFeature.WaterRipples, p.Ripples > 0, Math.Max(1, p.Ripples));
        UoPlusOptions.Set(profile, UoPlusFeature.ShallowShores, p.Shallow > 0, Math.Max(1, p.Shallow));
        WaterRender.Changed(world);
        GD.Print($"[water look] apply {p}");
    }

    private static async System.Threading.Tasks.Task GoTo(Node host, Game.World world, int x, int y, int z)
    {
        await InputProbe.Say(host, $"[go {x} {y} {z}");
        for (int i = 0; i < 40 && !(world.Player.X == x && world.Player.Y == y); i++) await InputProbe.Wait(host, 15);
        await InputProbe.Wait(host, 60);
    }

    /// <summary>
    /// Places a small boat on open water by the dock, adds dolphins beside it,
    /// boards, sails forward, stops and waits for the wake to fade, filming all
    /// of it. The moored boats in view are the stationary case.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> Wake(Node host, Game.World world, string dir)
    {
        if (!BoatDemo.FindOpenWaterNear(world, WakeStartX, WakeStartY, out int bx, out int by, out sbyte bz, out ushort graphic))
        {
            GD.PrintErr("[water look] no open water for the boat");
            return false;
        }
        GD.Print($"[water look] open water for the boat at {bx},{by},{bz} (search from {WakeStartX},{WakeStartY})");
        int sx = bx + 5, sy = by;
        await GoTo(host, world, sx, sy, bz);
        var boat = await BoatDemo.PlaceBoat(host, world, bx, by, bz, graphic);
        if (boat == null) return false;
        GD.Print($"[water look] boat 0x{boat.Serial:X8} at {boat.X},{boat.Y},{boat.Z}");

        // Three swimmers off the boat's beam.
        for (int i = 0; i < 3; i++)
        {
            await InputProbe.Say(host, "[add Dolphin");
            for (int f = 0; f < 120 && !world.TargetManager.IsTargeting; f++) await InputProbe.Wait(host, 2);
            if (!world.TargetManager.IsTargeting) { GD.PrintErr("[water look] [add Dolphin gave no target"); break; }
            int tx = bx + 4 + i, ty = by - 3 + 3 * i;
            ushort g = 0; sbyte tz = bz;
            for (var o = world.Map.GetTile(tx, ty); o != null; o = o.TNext)
                if (o is Game.GameObjects.Static && o.Graphic >= 0x1796 && o.Graphic <= 0x17B2) { g = o.Graphic; tz = o.Z; }
            world.TargetManager.Target(g, (ushort)tx, (ushort)ty, tz, true);
            await InputProbe.Wait(host, 20);
        }

        await InputProbe.Say(host, $"[go {boat.X} {boat.Y} {boat.Z + 3}");
        for (int i = 0; i < 30 && !(world.Player.X == boat.X && world.Player.Y == boat.Y); i++) await InputProbe.Wait(host, 10);
        if (!(world.Player.X == boat.X && world.Player.Y == boat.Y)) { GD.PrintErr("[water look] could not board"); return false; }
        await InputProbe.Wait(host, 120);

        string frames = Path.Combine(dir, "frames");
        Directory.CreateDirectory(frames);
        bool recording = true;
        string phase = "moored";
        var film = Record();
        await InputProbe.Wait(host, 90);
        var start = (boat.X, boat.Y);
        phase = "under-way";
        await InputProbe.Say(host, "raise anchor");
        await InputProbe.Wait(host, 20);
        await InputProbe.Say(host, System.Environment.GetEnvironmentVariable("GUO_WAKE_CMD") ?? "forward");
        await InputProbe.Wait(host, 300);
        await Take(host, Path.Combine(dir, "wake-under-way.png"));
        phase = "stopping";
        await InputProbe.Say(host, "stop");
        await InputProbe.Wait(host, 60);
        await Take(host, Path.Combine(dir, "wake-just-stopped.png"));
        phase = "stopped";
        await InputProbe.Wait(host, 240);
        await Take(host, Path.Combine(dir, "wake-settled.png"));
        recording = false;
        await film;
        bool moved = (boat.X, boat.Y) != start;
        // Leave nothing behind: step off, then remove the test boat.
        await InputProbe.Say(host, "[go Trammel");
        await InputProbe.Wait(host, 90);
        await RemoveItem(host, world, boat.Serial);
        GD.Print($"[water look] boat travel observed={moved} start={start} end=({boat.X},{boat.Y})");
        return moved;

        async System.Threading.Tasks.Task Record()
        {
            using var csv = new StreamWriter(Path.Combine(dir, "frames.csv"));
            csv.WriteLine("frame,ms,phase,frameMs,rippleMs,rippleStamps,rippleSteps,swimStamps,wetMs,hullX,hullY");
            int frame = 0;
            ulong last = Godot.Time.GetTicksUsec();
            var times = new List<double>();
            while (recording)
            {
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                ulong now = Godot.Time.GetTicksUsec();
                double frameMs = (now - last) / 1000.0;
                last = now;
                using var shot = host.GetViewport().GetTexture().GetImage();
                shot.Resize(shot.GetWidth() / 2, shot.GetHeight() / 2, Image.Interpolation.Nearest);
                shot.SavePng(Path.Combine(frames, $"frame-{frame:D4}.png"));
                var r = WaterRender.Ripples;
                csv.WriteLine($"{frame},{Godot.Time.GetTicksMsec()},{phase},{frameMs:F2},{r.LastUpdateMs:F3},{r.Stamps},{r.Steps},{r.SwimStamps},{r.LastWetMs:F3},{boat.X},{boat.Y}");
                frame++;
                // Settle frames between captures measure the live frame time,
                // not the PNG write.
                await InputProbe.Wait(host, 2);
                ulong a = Godot.Time.GetTicksUsec();
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                times.Add((Godot.Time.GetTicksUsec() - a) / 1000.0);
                last = Godot.Time.GetTicksUsec();
            }
            times.Sort();
            if (times.Count > 0)
                GD.Print($"[water look] settled frame ms: median={times[times.Count / 2]:F2} p95={times[(int)(times.Count * 0.95)]:F2} n={times.Count}");
        }
    }

    private static async System.Threading.Tasks.Task RemoveItem(Node host, Game.World world, uint serial)
    {
        await InputProbe.Say(host, "[remove");
        for (int f = 0; f < 120 && !world.TargetManager.IsTargeting; f++) await InputProbe.Wait(host, 2);
        if (!world.TargetManager.IsTargeting) { GD.PrintErr($"[water look] [remove gave no target for 0x{serial:X8}"); return; }
        world.TargetManager.Target(serial);
        await InputProbe.Wait(host, 30);
        GD.Print($"[water look] removed 0x{serial:X8}");
    }

    private static async System.Threading.Tasks.Task Take(Node host, string path)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = host.GetViewport().GetTexture().GetImage();
        var error = image.SavePng(path);
        if (error != Error.Ok) GD.PrintErr($"[water look] capture {path}: {error}");
    }
}
