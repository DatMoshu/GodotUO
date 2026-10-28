// SPDX-License-Identifier: BSD-2-Clause
#if DEBUG
using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Data;

namespace GUO.Host;

/// <summary>Two isolated dev-shard clients exchange real movement updates, alternating mover/observer.</summary>
internal static class MobileSyncProbe
{
    internal static async Task<bool> Run(Node host)
    {
        string role = System.Environment.GetEnvironmentVariable("GUO_MOBILE_PROBE");
        string folder = Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("GUO_MOBILE_TRACE"));
        await InputProbe.EnterTheWorld(host, 120);
        var world = Client.Game.UO.World;
        if (!world.InGame) return false;
        GUO.Configuration.ProfileManager.CurrentProfile.ReduceFPSWhenInactive = false;
        var original = world.Player;
        int originalX = original.X, originalY = original.Y, originalZ = original.Z;
        int originalMap = world.MapIndex;
        bool originalHidden = original.IsHidden;
        try { return await Exercise(host, role, folder); }
        finally
        {
            string[] maps = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };
            if (world.InGame && originalMap >= 0 && originalMap < maps.Length)
            {
                await InputProbe.Say(host, "[self set map " + maps[originalMap]);
                await InputProbe.Wait(host, 30);
                await InputProbe.Say(host, $"[go {originalX} {originalY} {originalZ}");
                await InputProbe.Say(host, originalHidden ? "[self hide" : "[self unhide");
                await InputProbe.Wait(host, 60);
            }
        }
    }

    private static async Task<bool> Exercise(Node host, string role, string folder)
    {
        var world = Client.Game.UO.World;
        GD.Print($"[mobile-sync] display screenScale={DisplayServer.ScreenGetScale()} dpiScale={Client.Game.DpiScale} gameScale={Client.Game.ScreenScale} window={DisplayServer.WindowGetSize()} viewport={host.GetViewport().GetVisibleRect().Size}");
        InputProbe.FullSizeGameWindow();
        await InputProbe.Say(host, "[self set map Felucca");
        await InputProbe.Wait(host, 60);
        await InputProbe.Say(host, "[self unhide");
        await InputProbe.Wait(host, 60);
        await InputProbe.Say(host, role == "a" ? "[go 1434 1697" : "[go 1436 1697");
        await InputProbe.Wait(host, 120);
        var player = world.Player;
        GD.Print($"[mobile-sync] setup role={role} map={world.MapIndex} hidden={player.IsHidden} position={player.X},{player.Y},{player.Z}");
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using (var setupImage = host.GetViewport().GetTexture().GetImage())
            setupImage.SavePng(Path.Combine(folder, role + "-setup.png"));
        File.WriteAllText(Path.Combine(folder, role + ".tmp"), player.Serial.ToString());
        File.Move(Path.Combine(folder, role + ".tmp"), Path.Combine(folder, role + ".ready"));
        string peerPath = Path.Combine(folder, (role == "a" ? "b" : "a") + ".ready");
        for (int i = 0; i < 1800 && !File.Exists(peerPath); i++) await InputProbe.Wait(host, 1);
        if (!File.Exists(peerPath)) return false;
        uint peer = uint.Parse(File.ReadAllText(peerPath));
        MobileRenderTrace.PeerSerial = peer;
        MobileRenderTrace.CaptureHost = host;
        for (int i = 0; i < 600 && !world.Mobiles.ContainsKey(peer); i++) await InputProbe.Wait(host, 1);
        if (!world.Mobiles.ContainsKey(peer)) return false;
        GD.Print($"[mobile-sync] ready role={role} local={player.Serial} peer={peer}");
        bool moved = false;
        int capture = 0;
        int laps = System.Environment.GetEnvironmentVariable("GUO_MOBILE_LAPS") == "2" ? 2 : 1;
        for (int phase = 0; phase < 2 * laps; phase++)
        {
            File.WriteAllText(Path.Combine(folder, $"{role}-phase{phase}.ready"), "ready");
            string barrier = Path.Combine(folder, $"{(role == "a" ? "b" : "a")}-phase{phase}.ready");
            for (int i = 0; i < 1800 && !File.Exists(barrier); i++) await InputProbe.Wait(host, 1);
            if (!File.Exists(barrier)) return false;
            GD.Print($"[mobile-sync] phase={phase} frame={Engine.GetProcessFrames()}");
            bool active = (phase % 2 == 0) == (role == "a");
            for (int direction = 0; direction < 8; direction++)
            {
                int x = player.X, y = player.Y;
                // Opposite pairs keep both clients within interest range. All movement uses the normal walker.
                for (int leg = 0; leg < 2; leg++)
                {
                    for (int f = 0; f < 40; f++)
                    {
                        if (active) player.Walk((Direction)((direction + leg * 4) & 7), true);
                        await InputProbe.Wait(host, 1);
                        moved |= player.X != x || player.Y != y;
                        if (f % 10 == 0)
                        {
                            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                            using var frame = host.GetViewport().GetTexture().GetImage();
                            frame.SavePng(Path.Combine(folder, $"{role}-frame{capture++:D4}.png"));
                        }
                    }
                    await InputProbe.Wait(host, 12);
                }
                moved |= player.X != x || player.Y != y;
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = host.GetViewport().GetTexture().GetImage();
                image.SavePng(Path.Combine(folder, $"{role}-phase{phase}-dir{direction}.png"));
            }
        }
        MobileRenderTrace.Close();
        GD.Print($"[mobile-sync] complete role={role} moved={moved} peerVisible={world.Mobiles.ContainsKey(peer)} staleFrames={MobileRenderTrace.StaleFrames}");
        return moved && world.Mobiles.ContainsKey(peer) && MobileRenderTrace.StaleFrames == 0
            && MobileRenderTrace.PixelMismatches == 0;
    }
}
#endif
