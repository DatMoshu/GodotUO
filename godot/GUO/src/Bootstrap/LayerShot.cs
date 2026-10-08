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
            System.IO.File.Copy(SwapManifest, System.IO.Path.Combine(GUO.Renderer.TerrainLayers.LayerDir(), "layers.json"), true);
            GD.Print("[GUO] layer shot: manifest swapped; saying -relayers");
            await InputProbe.Say(host, "-relayers");
            await InputProbe.Wait(host, 180);
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

        Passed = true;
    }
}
