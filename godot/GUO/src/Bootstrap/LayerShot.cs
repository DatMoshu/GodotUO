// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Go look at a terrain layer and photograph it
/// (--layer-shot --layer-at "x y z"): logs in on the probe account, says the
/// shard [go, lets the scene settle, saves two frames, quits. For iterating
/// on overlay/underlay rendering without a human at the screen. Use a GM
/// probe account (--account guoeffects), never the player's: the shard
/// refuses a second character from one account.
/// </summary>
internal static class LayerShot
{
    public static bool Passed { get; private set; }

    public static string At { get; set; } = "1438 1696 0 0";

    /// <summary>--layer-swap PATH: copy this layers.json over the live one mid-run, then -relayers.</summary>
    public static string SwapManifest { get; set; } = "";

    /// <summary>Pin the camera zoom for deterministic framing (0 leaves it alone).</summary>
    public static float Zoom { get; set; }

    /// <summary>--layer-capture "x0 y0 x1 y1": resample that tile rect out of
    /// the live viewport through the same math the editor dock uses, and save
    /// it. Proves the capture mapping where screenshots exist.</summary>
    public static string CaptureRect { get; set; } = "";

    /// <summary>--layer-light: photograph lighting speed mode off then on at a client-side night level (plus its G-buffer view), then while walking.</summary>
    public static bool Light { get; set; }
    /// <summary>
    /// --layer-openings: stage three identical wall bays at the arrival point, centred
    /// on an arrow slit, a true window and a solid piece, then photograph a carried
    /// torch behind and in front of each, slit fix off/on (LightOpenings).
    /// </summary>
    public static bool Openings { get; set; }
    /// <summary>--layer-water: photograph realtime water with shallow shores off, then on.</summary>
    public static bool Water { get; set; }

    public static async System.Threading.Tasks.Task Run(Node host, string dir)
    {
        // Started as soon as Main is ready, before the client has booted.
        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        if (!Client.Game.UO.World.InGame)
        {
            GD.PrintErr("[GUO] layer shot: never got into the world.");

            return;
        }

        string[] parts = At.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int wantX = parts.Length > 0 && int.TryParse(parts[0], out int px) ? px : 1438;
        int wantY = parts.Length > 1 && int.TryParse(parts[1], out int py) ? py : 1696;
        string z = parts.Length > 2 ? parts[2] : "0";
        int facet = parts.Length > 3 && int.TryParse(parts[3], out int pf) ? pf : 0;

        // Upstream's [go takes x y [z] on the current facet only; the facet
        // switches by map name first (Felucca 0, Trammel 1, Ilshenar 2,
        // Malas 3, Tokuno 4, TerMur 5).
        string[] facets = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };
        if (facet >= 0 && facet < facets.Length)
        {
            GD.Print($"[GUO] layer shot: [go {facets[facet]}");
            await InputProbe.Say(host, $"[go {facets[facet]}");
            await InputProbe.Wait(host, 120);
        }

        string go = $"[go {wantX} {wantY} {z}";
        GD.Print($"[GUO] layer shot: {go}");
        await InputProbe.Say(host, go);
        await InputProbe.Wait(host, 60);
        await InputProbe.Say(host, "hail from layershot");
        await InputProbe.Wait(host, 60);
        await InputProbe.Say(host, "[time");
        await InputProbe.Wait(host, 120);

        // The journal holds the server's copy: if the hail is missing,
        // speech itself is broken; if the hail is there but we never move,
        // the [go never fires.
        var entries = Game.Managers.JournalManager.Entries;
        for (int j = System.Math.Max(0, entries.Count - 14); j < entries.Count; j++)
        {
            GD.Print($"[GUO] layer shot journal: {entries[j].Name}: {entries[j].Text}");
        }

        // A teleport reloads the scene; wait until we arrive (or give up).
        var world = Client.Game.UO.World;
        bool arrived = false;
        for (int i = 0; i < 30 && !arrived; i++)
        {
            await InputProbe.Wait(host, 60);
            arrived = world.Player.X == wantX && world.Player.Y == wantY;
        }

        GD.Print($"[GUO] layer shot: at {world.Player.X},{world.Player.Y},{world.Player.Z} facet {world.MapIndex} (arrived={arrived})");

        if (Zoom > 0)
        {
            var scene = Client.Game.GetScene<Game.Scenes.GameScene>();
            if (scene?.Camera != null)
            {
                scene.Camera.Zoom = Zoom;
                GD.Print($"[GUO] layer shot: zoom {Zoom}");
                await InputProbe.Wait(host, 60);
            }
        }

        if (!string.IsNullOrWhiteSpace(SwapManifest))
        {
            // Mid-session manifest swap: proves -relayers re-reads layers.json
            // without a restart (boot-load alone would prove nothing).
            if (!GUO.Renderer.TerrainLayerPaths.TryResolve(GUO.Renderer.TerrainLayers.LayerDir(), "layers.json",
                out string manifest, out _, out string reason))
            {
                GD.PrintErr($"[GUO] layer shot: manifest swap rejected: {reason}");
                return;
            }
            System.IO.File.Copy(SwapManifest, manifest, true);
            GD.Print("[GUO] layer shot: manifest swapped; saying -relayers");
            await InputProbe.Say(host, "-relayers");
            await InputProbe.Wait(host, 180);
        }

        if (!string.IsNullOrWhiteSpace(CaptureRect))
        {
            CaptureArea(host, dir);
            await InputProbe.Wait(host, 30);
        }

        // Let the freshly loaded chunks settle before the picture.
        await InputProbe.Wait(host, 180);

        dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots/layer_shot" : dir;
        DirAccess.MakeDirRecursiveAbsolute(dir);

        for (int i = 0; i < 2; i++)
        {
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string path = dir.PathJoin($"layer_{world.Player.X}_{world.Player.Y}_{i}.png");
            host.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[GUO] layer shot -> {ProjectSettings.GlobalizePath(path)}");
            await InputProbe.Wait(host, 30);
        }

        if (Light)
        {
            await LightSequence(host, dir, world);
        }
        if (Openings)
        {
            await OpeningsSequence(host, dir, world);
        }
        if (Water)
        {
            string was = GUO.Renderer.Water.WaterRender.Override;
            int? shallowWas = GUO.Renderer.Water.WaterRender.ShallowOverride;
            try
            {
                GUO.Renderer.Water.WaterRender.Override = "uo";
                foreach ((string name, int strength) in new[] { ("water_before", 0), ("water_after", 100) })
                {
                    GUO.Renderer.Water.WaterRender.ShallowOverride = strength;
                    await InputProbe.Wait(host, 90);
                    await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    string path = dir.PathJoin(name + ".png");
                    host.GetViewport().GetTexture().GetImage().SavePng(path);
                    GD.Print($"[GUO] layer water -> {ProjectSettings.GlobalizePath(path)}");
                }
            }
            finally
            {
                GUO.Renderer.Water.WaterRender.Override = was;
                GUO.Renderer.Water.WaterRender.ShallowOverride = shallowWas;
            }
        }

        Passed = true;
    }

    private static async System.Threading.Tasks.Task LightSequence(Node host, string dir, Game.World world)
    {
        var profile = GUO.Configuration.ProfileManager.CurrentProfile;
        int lo = world.Light.Overall, lr = world.Light.RealOverall; bool mode = profile.LightingSpeedMode;
        async System.Threading.Tasks.Task Shot(string name)
        {
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string path = dir.PathJoin(name + ".png");
            host.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[GUO] layer light -> {ProjectSettings.GlobalizePath(path)}");
        }
        try
        {
            world.Light.Overall = world.Light.RealOverall = 26;
            profile.LightingSpeedMode = false; await InputProbe.Wait(host, 30); await Shot("light_off");
            profile.LightingSpeedMode = true; await InputProbe.Wait(host, 30); await Shot("light_on");
            { bool sunWas = profile.SpeedModeSunShadows; profile.SpeedModeSunShadows = false; await InputProbe.Wait(host, 30); await Shot("light_on_nosun"); profile.SpeedModeSunShadows = sunWas; int lampWas = profile.SpeedModeLampShadows; profile.SpeedModeLampShadows = 0; await InputProbe.Wait(host, 30); await Shot("light_on_nolampshadow"); profile.SpeedModeLampShadows = lampWas; Renderer.SpeedMode.SpeedModeDebug.GBuffer = true; await InputProbe.Wait(host, 10); await Shot("light_gbuffer"); Renderer.SpeedMode.SpeedModeDebug.GBuffer = false; await InputProbe.Wait(host, 10); }
            var scene = Client.Game.GetScene<Game.Scenes.GameScene>();
            GD.Print($"[GUO] layer light: offers={scene?.SpeedLightOfferCount ?? -1}");
            int n = 0;
            for (int d = 0; d < 4; d++)
            {
                await InputProbe.Pull(host, new Vector2(1, 0), 20);
                await Shot($"light_walk_{n++:00}");
                await InputProbe.Wait(host, 10);
                await Shot($"light_walk_{n++:00}");
            }
            GD.Print($"[GUO] layer light: offers after walking={scene?.SpeedLightOfferCount ?? -1}");
        }
        finally
        {
            world.Light.Overall = lo; world.Light.RealOverall = lr; profile.LightingSpeedMode = mode;
        }
    }

    // Fixture pieces, cut-stone set: south-facing wall, arrow slit, window.
    private const ushort OpeningWall = 0x0058, OpeningSlit = 0x005C, OpeningWindow = 0x005E;
    private const ushort OpeningFloor = 0x051A;

    private static async System.Threading.Tasks.Task OpeningsSequence(Node host, string dir, Game.World world)
    {
        var profile = GUO.Configuration.ProfileManager.CurrentProfile;
        int x0 = world.Player.X, y0 = world.Player.Y, floorZ = world.Player.Z;
        int lo = world.Light.Overall, lr = world.Light.RealOverall;
        bool mode = profile.LightingSpeedMode; int lampWas = profile.SpeedModeLampShadows;
        (string Name, ushort Centre, int X)[] bays =
        {
            ("slit", OpeningSlit, x0 - 9), ("window", OpeningWindow, x0), ("solid", OpeningWall, x0 + 9),
        };
        async System.Threading.Tasks.Task Shot(string name)
        {
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string path = dir.PathJoin(name + ".png");
            host.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[GUO] layer openings -> {ProjectSettings.GlobalizePath(path)} at {world.Player.X},{world.Player.Y},{world.Player.Z}");
        }
        async System.Threading.Tasks.Task Go(int x, int y)
        {
            await InputProbe.Say(host, $"[go {x} {y} {floorZ}");
            for (int i = 0; i < 20 && (world.Player.X != x || world.Player.Y != y); i++) await InputProbe.Wait(host, 15);
            await InputProbe.Wait(host, 60);
        }
        try
        {
            // Each bay is staged afresh after arriving at it: the earlier run found
            // items staged from afar gone by the time the camera arrived.
            async System.Threading.Tasks.Task Stage(string name, ushort centre, int bx)
            {
                await InputProbe.Say(host, $"[TileXYZ {bx - 5} {y0 - 4} 11 11 {floorZ} Static {OpeningFloor}");
                await InputProbe.Wait(host, 40);
                for (int dx = -2; dx <= 2; dx++)
                {
                    await InputProbe.Say(host, $"[TileXYZ {bx + dx} {y0} 1 1 {floorZ} Static {(dx == 0 ? centre : OpeningWall)}");
                    await InputProbe.Wait(host, 20);
                }
                GD.Print($"[GUO] layer openings: bay {name} centre 0x{centre:X4} at {bx},{y0},{floorZ}");
            }

            // Carry a lit torch: igniting one in the pack equips it (ModernUO BaseEquipableLight).
            await InputProbe.Say(host, "[addtopack Torch");
            for (int i = 0; i < 12 && !world.TargetManager.IsTargeting; i++) await InputProbe.Wait(host, 15);
            if (world.TargetManager.IsTargeting) world.TargetManager.Target(world.Player.Serial);
            await InputProbe.Wait(host, 60);
            Game.GameObjects.Item torch = null;
            var bag = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
            for (var i = bag?.Items; i != null && torch == null; i = i.Next)
                if (i is Game.GameObjects.Item it && it.Graphic == 0x0F6B) torch = it;
            if (torch != null) { Game.GameActions.DoubleClick(world, torch.Serial); await InputProbe.Wait(host, 60); }
            var held = world.Player.FindItemByLayer(Game.Data.Layer.TwoHanded) ?? world.Player.FindItemByLayer(Game.Data.Layer.OneHanded);
            GD.Print($"[GUO] layer openings: torch {(torch == null ? "missing" : $"0x{torch.Serial:X}")} held 0x{held?.Graphic ?? 0:X4}");

            world.Light.Overall = world.Light.RealOverall = 26;
            foreach ((string side, int dy) in new[] { ("behind", -1), ("front", 2) })
            {
                foreach (var bay in bays)
                {
                    await Go(bay.X, y0 + dy);
                    await Stage(bay.Name, bay.Centre, bay.X);
                    world.Light.Overall = world.Light.RealOverall = 26;
                    string n = $"{bay.Name}_{side}";
                    profile.LightingSpeedMode = false; await InputProbe.Wait(host, 30); await Shot(n + "_classic");
                    // Classic-off parity: the slit rule must not reach the classic lights.
                    GUO.Renderer.Lights.LightOpenings.ProbeSlitsTransmit = true;
                    await InputProbe.Wait(host, 30); await Shot(n + "_classic_slitsold");
                    GUO.Renderer.Lights.LightOpenings.ProbeSlitsTransmit = false;
                    profile.LightingSpeedMode = true;
                    GUO.Renderer.Lights.LightOpenings.ProbeSlitsTransmit = true;
                    await InputProbe.Wait(host, 30); await Shot(n + "_before");
                    GUO.Renderer.Lights.LightOpenings.ProbeSlitsTransmit = false;
                    await InputProbe.Wait(host, 30); await Shot(n + "_after");
                    profile.SpeedModeLampShadows = 0; await InputProbe.Wait(host, 30); await Shot(n + "_after_nolampshadow");
                    profile.SpeedModeLampShadows = lampWas;
                    Renderer.SpeedMode.SpeedModeDebug.GBuffer = true; await InputProbe.Wait(host, 10); await Shot(n + "_gbuffer");
                    Renderer.SpeedMode.SpeedModeDebug.GBuffer = false; await InputProbe.Wait(host, 10);
                }
            }
            // Walk the torch past the slit bay, outside to inside, to see it move through reach.
            await Go(bays[0].X - 3, y0 - 1);
            for (int step = 0; step < 6; step++)
            {
                await InputProbe.Pull(host, new Vector2(1, 0), 20);
                await Shot($"slit_walk_{step:00}");
            }
        }
        finally
        {
            GUO.Renderer.Lights.LightOpenings.ProbeSlitsTransmit = false;
            Renderer.SpeedMode.SpeedModeDebug.GBuffer = false;
            world.Light.Overall = lo; world.Light.RealOverall = lr;
            profile.LightingSpeedMode = mode; profile.SpeedModeLampShadows = lampWas;
        }
    }

    private static void CaptureArea(Node host, string dir)
    {
        try
        {
            string[] parts = CaptureRect.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                GD.Print("[GUO] layer shot: --layer-capture wants \"x0 y0 x1 y1\"");
                return;
            }

            int x0 = int.Parse(parts[0]), y0 = int.Parse(parts[1]);
            int x1 = int.Parse(parts[2]), y1 = int.Parse(parts[3]);
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            var world = Client.Game.UO.World;
            var scene = Client.Game.GetScene<Game.Scenes.GameScene>();
            var cam = scene?.Camera;
            if (cam == null)
            {
                GD.Print("[GUO] layer shot: no camera for capture");
                return;
            }

            int TileZ(int x, int y)
            {
                try
                {
                    return world.Map.GetTileZ(x, y);
                }
                catch (System.Exception)
                {
                    return 0;
                }
            }

            int w = System.Math.Clamp((x1 - x0 + 1) * 64, 256, 1024);
            int h = System.Math.Clamp((y1 - y0 + 1) * 64, 256, 1024);
            int ztop = System.Math.Max(System.Math.Max(TileZ(x0, y0), TileZ(x1, y0)), System.Math.Max(TileZ(x0, y1), TileZ(x1, y1)));
            using var shot = host.GetViewport().GetTexture().GetImage();
            var off = scene.DrawOffset;
            var view = cam.ViewTransform;
            // Sanity anchor: the player's tile must map near the viewport
            // center (the camera follows it). If it doesn't, the mapping
            // (not the rect) is wrong.
            var anchor = view * new Godot.Vector2(
                (world.Player.X - world.Player.Y) * 22 - 22 - off.X,
                (world.Player.X + world.Player.Y) * 22 - (world.Player.Z << 2) - 22 - off.Y);
            GD.Print($"[GUO] layer shot: player maps to {anchor.X:0},{anchor.Y:0} vs center {shot.GetWidth() / 2},{shot.GetHeight() / 2}");
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            using var img = GUO.Renderer.TerrainLayers.CaptureRect(shot, (tx, ty) =>
            {
                var s = view * new Godot.Vector2((tx - ty) * 22f - 22f - off.X, (tx + ty) * 22f - (ztop << 2) - 22f - off.Y);
                if (s.X < minX) minX = s.X;
                if (s.Y < minY) minY = s.Y;
                if (s.X > maxX) maxX = s.X;
                if (s.Y > maxY) maxY = s.Y;
                return (s.X, s.Y);
            }, x0, y0, x1, y1, ztop, w, h);
            dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots/layer_shot" : dir;
            DirAccess.MakeDirRecursiveAbsolute(dir);
            string path = dir.PathJoin($"capture_{x0}_{y0}_{x1}_{y1}.png");
            img.SavePng(path);
            GD.Print($"[GUO] layer shot: capture {w}x{h} over ({x0},{y0})-({x1},{y1}) z {ztop}; "
                + $"shot {shot.GetWidth()}x{shot.GetHeight()}, samples {minX:0}-{maxX:0},{minY:0}-{maxY:0} -> {ProjectSettings.GlobalizePath(path)}");
        }
        catch (System.Exception ex)
        {
            GD.Print($"[GUO] layer shot: capture failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
