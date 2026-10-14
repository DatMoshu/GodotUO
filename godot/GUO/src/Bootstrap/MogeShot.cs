// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Prove the -moge gump works in the running game (--moge-shot
/// --screenshot-dir DIR): logs in on the probe account, says the client
/// command -moge, selects the nearest artwork-bearing static through the
/// gump's own Select/targeting path, runs Generate (the real
/// tools/moge/moge_to_ply.py bridge), waits for the live replacement splat
/// and screenshots each stage. Testing tool for a testing gump; never
/// touches the shard (placements are client-side only).
///
/// The gump's buttons are driven through its own OnButtonClick handler with
/// the Select/Generate ids (0/1 in its Buttons enum) -- the same handler a
/// synthetic click would reach -- while the command itself goes through real
/// typed chat (InputProbe.Say), so command registration is proven too. The
/// world-aim click is replaced by TargetManager.Target on the chosen static,
/// which is the call the mouse path makes once it has resolved its aim.
/// </summary>
internal static class MogeShot
{
    public static bool Passed { get; private set; }

    // MogePlacerGump.Buttons: Select is 0, Generate is 1.
    private const int SelectButton = 0;
    private const int GenerateButton = 1;

    private static readonly System.Collections.Generic.List<(string What, bool Ok)> Checks =
        new();

    private static void Check(string what, bool ok, string detail = null)
    {
        Checks.Add((what, ok));
        GD.Print($"[GUO] moge shot check: {(ok ? "ok  " : "FAIL")} {what}"
            + (detail == null ? "" : $" -- {detail}"));
    }

    public static async System.Threading.Tasks.Task Run(Node host, string dir)
    {
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
            GD.PrintErr("[GUO] moge shot: never got into the world.");
            return;
        }

        var world = Client.Game.UO.World;
        GD.Print($"[GUO] moge shot: at {world.Player.X},{world.Player.Y},{world.Player.Z} facet {world.MapIndex}");
        GD.Print($"[GUO] moge shot: tool {GUO.Comfy.MogeClient.ToolPath() ?? "<none>"}, "
            + $"model '{GUO.Comfy.MogeClient.ResolveModel()}' (empty defers to the tool default), "
            + $"stage {GUO.Comfy.MogeClient.ResolveStageDir()}");

        string[] before = System.IO.Directory.GetFiles(
            GUO.Comfy.MogeClient.ResolveStageDir(), "*.ply");
        var seen = new System.Collections.Generic.HashSet<string>(before);

        // The command itself goes through real typed chat: '-' selects
        // ChatMode.ClientCommand, which runs CommandManager.Execute("moge").
        await InputProbe.Say(host, "-moge");
        await InputProbe.Wait(host, 60);

        Game.UI.Gumps.MogePlacerGump gump = null;
        for (int i = 0; i < 20 && gump == null; i++)
        {
            gump = Game.Managers.UIManager.GetGump<Game.UI.Gumps.MogePlacerGump>();
            if (gump == null)
            {
                await InputProbe.Wait(host, 30);
            }
        }

        Check("the -moge gump opens", gump != null,
            gump == null ? "no MogePlacerGump" : $"{gump.Width}x{gump.Height} with {gump.Children.Count} children");
        if (gump == null)
        {
            return;
        }

        dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots/moge_shot" : dir;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        await Shot(host, dir, "moge_open");

        // Nearest static with artwork the bridge can read: the same
        // ExtractArtShot call Generate gates on, tried closest-first so a
        // pixel-less graphic fails the search, not the run.
        Game.GameObjects.Static victim = FindArtworkStatic(world);
        Check("a static with artwork is nearby", victim != null,
            victim == null ? "none found" : $"0x{victim.Graphic:X4} at {victim.X},{victim.Y},{victim.Z}");
        if (victim == null)
        {
            return;
        }

        ushort graphic = victim.Graphic;
        ushort vx = victim.X, vy = victim.Y;
        short vz = victim.Z;

        gump.OnButtonClick(SelectButton);
        await InputProbe.Wait(host, 30);
        Check("Select raises the target cursor", world.TargetManager.IsTargeting);
        if (!world.TargetManager.IsTargeting)
        {
            return;
        }

        world.TargetManager.Target(graphic, vx, vy, vz);
        await InputProbe.Wait(host, 30);

        bool selected = GetField<bool>(gump, "_hasTile")
            && GetField<ushort>(gump, "_targetGraphic") == graphic;
        Check("the target lands on the static", selected,
            $"hasTile={GetField<bool>(gump, "_hasTile")} graphic=0x{GetField<ushort>(gump, "_targetGraphic"):X4}");
        await Shot(host, dir, "moge_selected");
        if (!selected)
        {
            return;
        }

        gump.OnButtonClick(GenerateButton);
        await InputProbe.Wait(host, 60);

        // Generate runs the bridge on a worker; the gump polls it in Update
        // and prints "MogePlacer: moge_XXXX replaced ..." on success. Wait
        // for the placed entry (same assembly, read by reflection), the new
        // PLY artifact, and the hidden original -- any one alone could lie.
        string stageDir = GUO.Comfy.MogeClient.ResolveStageDir();
        bool placed = false;
        string newPly = null;
        for (int i = 0; i < 1200 && !placed; i++)
        {
            await InputProbe.Wait(host, 30);
            int count = PlacedCount(gump);
            // The run's own artifact: a stage file not present at startup,
            // newest first (an alphabetical pick once named a stale file).
            if (newPly == null)
            {
                string newest = null;
                System.DateTime newestAt = System.DateTime.MinValue;
                foreach (string f in System.IO.Directory.GetFiles(stageDir, "*.ply"))
                {
                    if (seen.Contains(f))
                    {
                        continue;
                    }

                    System.DateTime at = System.IO.File.GetLastWriteTimeUtc(f);
                    if (newest == null || at > newestAt)
                    {
                        newest = f;
                        newestAt = at;
                    }
                }

                newPly = newest;
            }

            placed = count > 0 && !victim.AllowedToDraw && newPly != null;
        }

        Check("Generate places a replacement splat", placed,
            $"placed={PlacedCount(gump)} hidden={!victim.AllowedToDraw} ply={newPly ?? "none"}");
        if (!placed)
        {
            GD.Print($"[GUO] moge shot: status '{GetStatus(gump)}'");
            await Shot(host, dir, "moge_failed");
            return;
        }

        GD.Print($"[GUO] moge shot: status '{GetStatus(gump)}'");
        GD.Print($"[GUO] moge shot: artifact {newPly} ({new System.IO.FileInfo(newPly).Length} bytes)");

        // The replacement draws through the ordinary SplatObject path: prove
        // it is queued, then let the chunks settle before the picture.
        string liveKey = GetField<string>(gump, "_liveKey");
        var scene = Client.Game.GetScene<Game.Scenes.GameScene>();
        await InputProbe.Wait(host, 180);
        (int Level, int Drawn) drawn = (-2, 0);
        if (scene != null && liveKey != null)
        {
            drawn = scene.SplatDrawn(liveKey);
        }
        Check("the replacement splat draws", drawn.Drawn > 0, $"{liveKey} level {drawn.Level} drawn {drawn.Drawn}");

        await Shot(host, dir, "moge_placed_0");
        await InputProbe.Wait(host, 30);
        await Shot(host, dir, "moge_placed_1");

        int passed = 0;
        foreach ((string _, bool ok) in Checks)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                GD.PrintErr("[GUO] moge shot FAILED");
            }
        }

        Passed = passed == Checks.Count;
        GD.Print($"[GUO] moge shot: {passed}/{Checks.Count} checks passed");
    }

    private static Game.GameObjects.Static FindArtworkStatic(Game.World world)
    {
        var player = world.Player;
        Game.GameObjects.Static best = null;
        int bestDist = int.MaxValue;
        var candidates = new System.Collections.Generic.List<Game.GameObjects.Static>();

        foreach (Game.Map.Chunk chunk in world.Map.GetUsedChunks())
        {
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (Game.GameObjects.GameObject o = chunk?.GetHeadObject(x, y);
                        o != null; o = o.TNext)
                    {
                        if (o is Game.GameObjects.Static s
                            && !s.IsDestroyed && s.AllowedToDraw && s.Graphic != 0)
                        {
                            int d = System.Math.Max(
                                System.Math.Abs(s.X - player.X),
                                System.Math.Abs(s.Y - player.Y));
                            if (d < bestDist && d <= 20)
                            {
                                candidates.Add(s);
                            }
                        }
                    }
                }
            }
        }

        candidates.Sort((a, b) =>
            System.Math.Max(System.Math.Abs(a.X - player.X), System.Math.Abs(a.Y - player.Y)).CompareTo(
            System.Math.Max(System.Math.Abs(b.X - player.X), System.Math.Abs(b.Y - player.Y))));

        foreach (Game.GameObjects.Static s in candidates)
        {
            if (candidates.IndexOf(s) >= 10)
            {
                break;
            }

            try
            {
                var shot = GUO.Comfy.ComfyClient.ExtractArtShot(s.Graphic, false);
                if (shot?.Png != null && shot.TightWidth > 0)
                {
                    best = s;
                    bestDist = System.Math.Max(
                        System.Math.Abs(s.X - player.X),
                        System.Math.Abs(s.Y - player.Y));
                    break;
                }
            }
            catch (System.Exception)
            {
            }
        }

        if (best != null)
        {
            GD.Print($"[GUO] moge shot: victim 0x{best.Graphic:X4} at {best.X},{best.Y},{best.Z} ({bestDist} tiles off)");
        }

        return best;
    }

    private static async System.Threading.Tasks.Task Shot(Node host, string dir, string name)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        string path = dir.PathJoin($"{name}.png");
        host.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[GUO] moge shot -> {ProjectSettings.GlobalizePath(path)}");
    }

    private static T GetField<T>(object o, string name)
    {
        var f = o.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return f == null ? default : (T)f.GetValue(o);
    }

    private static int PlacedCount(object gump)
    {
        var f = gump.GetType().GetField("_placed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (f?.GetValue(gump) as System.Collections.IList)?.Count ?? 0;
    }

    private static string GetStatus(object gump)
    {
        var label = GetField<Game.UI.Controls.Label>(gump, "_statusLabel");
        return label?.Text ?? "";
    }
}
