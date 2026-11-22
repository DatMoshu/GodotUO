// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner water sprint 2026-11-22): --water-look-shot --water-look-only coast|pan.
// Matched captures of coasts for the seam work: stills at fixed spots under a pinned light
// level and a frozen water clock, and a cell-by-cell pan that measures what changes on screen
// when the water windows re-centre. Dev shards only (staff [go). The profile is put back.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using GUO.Configuration;
using GUO.Renderer.Water;

namespace GUO.Host;

internal static class WaterSprintShot
{
    // Trammel: the Haven pier coast (the owner's reference), a Haven beach, Britain's docks.
    private const string DefaultSpots = "haven-pier:3515 2598 -5;haven-beach:3496 2610 -5;britain-docks:1489 1757 -5";

    private static string Env(string name, string fallback) =>
        System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public static async System.Threading.Tasks.Task<bool> Run(Node host, Game.World world, string dir, string mode)
    {
        var profile = ProfileManager.CurrentProfile;
        int overall = world.Light.Overall, real = world.Light.RealOverall;
        float? frozen = WaterRender.FrozenSeconds;
        bool speedMode = profile.LightingSpeedMode;
        try
        {
            // GUO_WSS_SPEEDMODE=1|0: realtime lighting (lighting speed mode) on or off for the shots; unset keeps the profile's.
            int sm = EnvInt("GUO_WSS_SPEEDMODE", -1);
            if (sm >= 0) profile.LightingSpeedMode = sm != 0;
            // GUO_WSS_LIGHT: the world light level, 0 full day to 30 the darkest night, set on the
            // client only and again after every move; unset keeps the shard's own light.
            _light = EnvInt("GUO_WSS_LIGHT", -1);
            // The swell is a function of the water clock; frozen, two frames differ only by what moved.
            if (EnvInt("GUO_WSS_FREEZE", 1) != 0) WaterRender.FrozenSeconds = 40f;
            string style = Env("GUO_WSS_STYLE", "");
            var look = WaterLookShot.FromProfile(Profile.CreatePlayerDefaults());
            if (style != "") look = look with { Style = style };
            WaterLookShot.Apply(world, profile, look);
            await InputProbe.Say(host, "[go " + Env("GUO_WSS_FACET", "Trammel"));
            await InputProbe.Wait(host, 120);
            GUO.Renderer.CleanShots.Set(true);
            float zoom = float.TryParse(Env("GUO_WSS_ZOOM", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out float zz) ? zz : 1f;
            if (Client.Game.GetScene<Game.Scenes.GameScene>()?.Camera is { } cam) cam.Zoom = zoom;
            return mode == "pan" ? await Pan(host, world, dir) : mode == "bench" ? await Bench(host, world, dir) : await Coast(host, world, dir);
        }
        finally
        {
            GUO.Renderer.CleanShots.Set(false);
            WaterRender.FrozenSeconds = frozen;
            world.Light.Overall = overall; world.Light.RealOverall = real;
            profile.LightingSpeedMode = speedMode;
        }
    }

    private static int _light = -1;

    private static void PinLight(Game.World world)
    {
        if (_light >= 0) world.Light.Overall = world.Light.RealOverall = Math.Clamp(_light, 0, 30);
    }

    private static IEnumerable<(string Name, int X, int Y, int Z)> Spots()
    {
        foreach (string part in Env("GUO_WSS_SPOTS", DefaultSpots).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] nv = part.Split(':');
            string[] c = nv[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (c.Length < 2 || !int.TryParse(c[0], out int x) || !int.TryParse(c[1], out int y)) continue;
            int z = c.Length > 2 && int.TryParse(c[2], out int pz) ? pz : -5;
            yield return (nv.Length > 1 ? nv[0] : $"{x}_{y}", x, y, z);
        }
    }

    private static async System.Threading.Tasks.Task<bool> GoTo(Node host, Game.World world, int x, int y, int z, int settle)
    {
        await InputProbe.Say(host, $"[go {x} {y} {z}");
        for (int i = 0; i < 60 && !(world.Player.X == x && world.Player.Y == y); i++) await InputProbe.Wait(host, 5);
        PinLight(world);
        await InputProbe.Wait(host, settle);
        PinLight(world);
        await InputProbe.Wait(host, 4);
        return world.Player.X == x && world.Player.Y == y;
    }

    private static async System.Threading.Tasks.Task<Image> Grab(Node host)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return host.GetViewport().GetTexture().GetImage();
    }

    private static async System.Threading.Tasks.Task<bool> Coast(Node host, Game.World world, string dir)
    {
        bool ok = true;
        foreach (var s in Spots())
        {
            bool at = await GoTo(host, world, s.X, s.Y, s.Z, 240);
            ok &= at;
            using Image shot = await Grab(host);
            shot.SavePng(Path.Combine(dir, $"coast-{s.Name}.png"));
            var (ms, rebuilds) = WaterRender.MaskStats;
            GD.Print($"[water sprint] coast {s.Name} at {world.Player.X},{world.Player.Y} arrived={at} maskMs={ms:F2} maskRebuilds={rebuilds}");
        }
        return ok;
    }

    /// <summary>
    /// Steps the camera a cell at a time along GUO_WSS_PAN_DIR ("dx dy", default "1 0") from the first
    /// spot and compares each frame with the one before, moved by the step's screen offset: with the
    /// water clock frozen the world should only slide. Pixels that changed are counted per step, with
    /// whether the water mask or the ripple window re-centred on it; frames go to pan/ at half size.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> Pan(Node host, Game.World world, string dir)
    {
        (string Name, int X, int Y, int Z) s = default;
        foreach (var first in Spots()) { s = first; break; }
        string[] d = Env("GUO_WSS_PAN_DIR", "1 0").Split(' ');
        int dx = int.Parse(d[0]), dy = int.Parse(d[1]);
        int steps = EnvInt("GUO_WSS_PAN_STEPS", 40);
        string frames = Path.Combine(dir, "pan");
        Directory.CreateDirectory(frames);
        if (!await GoTo(host, world, s.X, s.Y, s.Z, 240)) return false;
        using var csv = new StreamWriter(Path.Combine(dir, "pan.csv"));
        csv.WriteLine("step,x,y,maskRebuilds,maskMs,rippleOriginX,rippleOriginY,changedPixels,changedShare,maxBlock,recentred");
        Image before = null;
        int lastRebuilds = WaterRender.MaskStats.Rebuilds, lastOx = WaterRender.Ripples.OriginX, lastOy = WaterRender.Ripples.OriginY;
        // One cell along X is (+22, +22) on screen, along Y (-22, +22), times the zoom the view draws at.
        float scale = 1f / (Client.Game.GetScene<Game.Scenes.GameScene>()?.Camera?.Zoom ?? 1f);
        int sx = (int)MathF.Round((dx - dy) * 22 * scale), sy = (int)MathF.Round((dx + dy) * 22 * scale);
        long worst = 0; int worstStep = -1;
        for (int i = 0; i <= steps; i++)
        {
            if (i > 0 && !await GoTo(host, world, s.X + dx * i, s.Y + dy * i, s.Z, EnvInt("GUO_WSS_PAN_SETTLE", 45))) { GD.PrintErr($"[water sprint] pan step {i} did not arrive"); break; }
            Image now = await Grab(host);
            var (ms, rebuilds) = WaterRender.MaskStats;
            var r = WaterRender.Ripples;
            long changed = 0; int maxBlock = 0; long total = 0;
            if (before != null) Compare(before, now, sx, sy, out changed, out total, out maxBlock);
            bool recentred = rebuilds != lastRebuilds || r.OriginX != lastOx || r.OriginY != lastOy;
            csv.WriteLine($"{i},{world.Player.X},{world.Player.Y},{rebuilds},{ms:F2},{r.OriginX},{r.OriginY},{changed},{(total > 0 ? (double)changed / total : 0):F5},{maxBlock},{(recentred ? 1 : 0)}");
            if (changed > worst) { worst = changed; worstStep = i; }
            lastRebuilds = rebuilds; lastOx = r.OriginX; lastOy = r.OriginY;
            using (Image half = (Image)now.Duplicate())
            {
                half.Resize(now.GetWidth() / 2, now.GetHeight() / 2, Image.Interpolation.Nearest);
                half.SavePng(Path.Combine(frames, $"pan-{i:D3}.png"));
            }
            before?.Dispose();
            before = now;
        }
        before?.Dispose();
        GD.Print($"[water sprint] pan {s.Name} dir {dx},{dy} steps {steps}: worst step {worstStep} changed {worst} px");
        return true;
    }

    /// <summary>
    /// Counts the pixels of <paramref name="now"/> that differ from <paramref name="before"/> moved by
    /// the camera step (the world slid by -sx, -sy), over the part both frames hold, away from the
    /// centre where the player's own sprite stands. maxBlock is the most changed pixels in one
    /// 32 x 32 block: a pop is local, an animation is everywhere.
    /// </summary>
    /// <summary>
    /// Frame times along the pan's route with nothing captured: at each step the camera settles,
    /// then GUO_WSS_BENCH_FRAMES frames are timed with vsync off. Writes bench.csv (one row a step)
    /// and prints the median and p95 over all timed frames. Run it once a style (GUO_WSS_STYLE)
    /// on the same build to compare; the water clock is left running unless GUO_WSS_FREEZE=1.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> Bench(Node host, Game.World world, string dir)
    {
        (string Name, int X, int Y, int Z) s = default;
        foreach (var first in Spots()) { s = first; break; }
        string[] d = Env("GUO_WSS_PAN_DIR", "1 0").Split(' ');
        int dx = int.Parse(d[0]), dy = int.Parse(d[1]);
        int steps = EnvInt("GUO_WSS_PAN_STEPS", 24), frames = EnvInt("GUO_WSS_BENCH_FRAMES", 90);
        if (!await GoTo(host, world, s.X, s.Y, s.Z, 240)) return false;
        var vsync = DisplayServer.WindowGetVsyncMode();
        int cap = Engine.MaxFps;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        var all = new List<double>();
        try
        {
            using var csv = new StreamWriter(Path.Combine(dir, "bench.csv"));
            csv.WriteLine("step,x,y,frames,medianMs,p95Ms,maxMs,maskMs");
            for (int i = 0; i <= steps; i++)
            {
                if (i > 0 && !await GoTo(host, world, s.X + dx * i, s.Y + dy * i, s.Z, 30)) break;
                var t = new List<double>(frames);
                ulong last = Godot.Time.GetTicksUsec();
                for (int f = 0; f < frames; f++)
                {
                    PinLight(world);
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    ulong now = Godot.Time.GetTicksUsec();
                    t.Add((now - last) / 1000.0);
                    last = now;
                }
                all.AddRange(t);
                t.Sort();
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},{world.Player.X},{world.Player.Y},{t.Count},{t[t.Count / 2]:F3},{t[(int)(t.Count * 0.95)]:F3},{t[^1]:F3},{WaterRender.MaskStats.Ms:F2}"));
            }
        }
        finally
        {
            DisplayServer.WindowSetVsyncMode(vsync);
            Engine.MaxFps = cap;
        }
        if (all.Count == 0) return false;
        all.Sort();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[water sprint] bench {s.Name} style={Env("GUO_WSS_STYLE", "default")} frames={all.Count} median={all[all.Count / 2]:F3} ms p95={all[(int)(all.Count * 0.95)]:F3} ms max={all[^1]:F3} ms"));
        return true;
    }

    private static void Compare(Image before, Image now, int sx, int sy, out long changed, out long total, out int maxBlock)
    {
        int w = now.GetWidth(), h = now.GetHeight();
        byte[] a = before.GetData(), b = now.GetData();
        int bpp = a.Length / (w * h);
        int cx = w / 2, cy = h / 2;
        var blocks = new int[(w / 32 + 1) * (h / 32 + 1)];
        changed = 0; total = 0;
        for (int y = Math.Max(0, -sy); y < Math.Min(h, h - sy); y += 1)
        {
            for (int x = Math.Max(0, -sx); x < Math.Min(w, w - sx); x += 1)
            {
                if (Math.Abs(x - cx) < 140 && Math.Abs(y - cy) < 200) continue;
                int i = (y * w + x) * bpp, j = ((y + sy) * w + x + sx) * bpp;
                int diff = Math.Abs(b[i] - a[j]) + Math.Abs(b[i + 1] - a[j + 1]) + Math.Abs(b[i + 2] - a[j + 2]);
                total++;
                if (diff > 24)
                {
                    changed++;
                    blocks[(y / 32) * (w / 32 + 1) + x / 32]++;
                }
            }
        }
        maxBlock = 0;
        foreach (int n in blocks) if (n > maxBlock) maxBlock = n;
    }
}
