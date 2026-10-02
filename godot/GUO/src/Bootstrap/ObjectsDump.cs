// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Host;

/// <summary>
/// Writes what the client holds of the world around the player: every item
/// and mobile within <see cref="Range"/> tiles, as the server sent them.
/// </summary>
/// <remarks>
/// The machine-checkable half of "the client shows it" for the editor's world
/// objects (ADR-0014): tools/editor_objects_proof looks for the exported
/// decoration and the spawner's creature here, and keeps the frame beside it.
/// </remarks>
internal static class ObjectsDump
{
    public const int Range = 12;

    /// <summary>
    /// Stays online and writes a dump each time a tool asks: a file
    /// <c>&lt;dir&gt;/&lt;name&gt;.request</c> is answered with
    /// <c>&lt;dir&gt;/&lt;name&gt;.json</c> (the request is removed first), and
    /// <c>&lt;dir&gt;/&lt;name&gt;.shot</c> with <c>&lt;name&gt;.png</c>, and
    /// <c>&lt;dir&gt;/quit</c> ends the watch. How tools/editor_objects_proof
    /// --live looks at the client's world before and after a live edit
    /// without restarting it.
    /// </summary>
    public static async System.Threading.Tasks.Task Watch(Godot.Node host, string dir, double maxSeconds = 900)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "watching"), "");
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed.TotalSeconds < maxSeconds && !File.Exists(Path.Combine(dir, "quit")))
        {
            foreach (string request in Directory.GetFiles(dir, "*.request"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                File.Delete(request);
                Write(Path.Combine(dir, name + ".json"));
            }

            // <name>.shot: the frame, as CaptureFrame takes it (a windowed
            // client only: a headless one never draws, and this would wait).
            foreach (string request in Directory.GetFiles(dir, "*.shot"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                File.Delete(request);
                await host.ToSignal(Godot.RenderingServer.Singleton, Godot.RenderingServerInstance.SignalName.FramePostDraw);
                host.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name + ".png"));
            }

            // <name>.walk8 holding frames per direction (default 70): the eight
            // screen directions (InputProbe.WalkEight), then <name>.walked.
            foreach (string request in Directory.GetFiles(dir, "*.walk8"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                int frames = int.TryParse(File.ReadAllText(request).Trim(), out int fr) && fr > 0 ? fr : 70;
                File.Delete(request);
                bool walked = await InputProbe.WalkEight(host, frames);
                File.WriteAllText(Path.Combine(dir, name + ".walked"), walked ? "moved" : "did not move");
            }

            // <name>.paperdoll: the player's paperdoll opened (where worn gear shows).
            foreach (string request in Directory.GetFiles(dir, "*.paperdoll"))
            {
                File.Delete(request);
                if (Client.Game?.UO?.World is World pw && pw.Player != null)
                {
                    GameActions.OpenPaperdoll(pw, pw.Player.Serial);
                }
            }

            // <name>.backpack: the player's backpack opened (where a created item lands).
            foreach (string request in Directory.GetFiles(dir, "*.backpack"))
            {
                File.Delete(request);
                if (Client.Game?.UO?.World is World bw && bw.Player != null)
                {
                    GameActions.OpenBackpack(bw);
                }
            }

            // <name>.options holding a page number: Options opened on that page
            // (3 is Video, where "Change UO folder..." sits).
            foreach (string request in Directory.GetFiles(dir, "*.options"))
            {
                int page = int.TryParse(File.ReadAllText(request).Trim(), out int p) ? p : 0;
                File.Delete(request);
                if (Client.Game?.UO?.World is World w)
                {
                    GameActions.OpenSettings(w, page);
                    // Down to the page's last section, where the GUO rows are.
                    for (int f = 0; f < 3; f++)
                    {
                        await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
                    }

                    if (GUO.Game.Managers.UIManager.GetGump<GUO.Game.UI.Gumps.OptionsGump>() is { } g)
                    {
                        foreach (var c in g.Children)
                        {
                            if (c is GUO.Game.UI.Controls.ScrollArea area && c.Page == page)
                            {
                                for (int i = 0; i < 400; i++)
                                {
                                    area.Scroll(false);
                                }
                            }
                        }
                    }
                }
            }

            // <name>.changefolder holding a folder: the Options button's action
            // (FirstRunScreen.OpenChange), scripted to pick that folder and Save;
            // its screenshots go to <dir>/<name>/.
            foreach (string request in Directory.GetFiles(dir, "*.changefolder"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                string folder = File.ReadAllText(request).Trim();
                File.Delete(request);
                FirstRunScreen.OpenChange(folder, Path.Combine(dir, name));
            }

            // <name>.walk: the character walks the four screen diagonals (InputProbe's
            // walk), then <name>.walked. A recording started before it keeps going.
            foreach (string request in Directory.GetFiles(dir, "*.walk"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                File.Delete(request);
                bool walked = await InputProbe.WalkAround(host);
                File.WriteAllText(Path.Combine(dir, name + ".walked"), walked ? "moved" : "did not move");
            }

            // <name>.say holding a line: the player says it (a shard command such as
            // "[go x y z" on a staff account), then <name>.said once it has gone.
            foreach (string request in Directory.GetFiles(dir, "*.say"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                string line = File.ReadAllText(request).Trim();
                File.Delete(request);
                if (Client.Game?.UO?.World?.Player != null && line.Length > 0)
                {
                    GameActions.Say(line);
                }

                await host.ToSignal(host.GetTree().CreateTimer(1.0), Godot.SceneTreeTimer.SignalName.Timeout);
                File.WriteAllText(Path.Combine(dir, name + ".said"), line);
            }

            // <name>.closegumps: every open gump closes (the paperdoll a shard opens at
            // login, the status bar), then <name>.closed holding how many. For review
            // frames and films that show the world alone.
            foreach (string request in Directory.GetFiles(dir, "*.closegumps"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                File.Delete(request);
                // not the world viewport: it is a gump too, and draws the world
                var open = new List<GUO.Game.UI.Gumps.Gump>();
                foreach (var g in GUO.Game.Managers.UIManager.Gumps)
                {
                    if (g is not GUO.Game.UI.Gumps.WorldViewportGump)
                    {
                        open.Add(g);
                    }
                }

                foreach (var g in open)
                {
                    g.Dispose();
                }

                File.WriteAllText(Path.Combine(dir, name + ".closed"), open.Count.ToString());
            }

            // <name>.goto holding "x y z [distance]": the player's pathfinder walks
            // there (doors on the way open as the profile's auto_open_doors and
            // smooth_doors allow), then <name>.arrived holding "path|no path x y z",
            // where it stopped. How tools/multi walks through an authored multi.
            foreach (string request in Directory.GetFiles(dir, "*.goto"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                string[] words = File.ReadAllText(request).Split((char[]) null, System.StringSplitOptions.RemoveEmptyEntries);
                File.Delete(request);
                PlayerMobile player = Client.Game?.UO?.World?.Player;
                string result = "no player";
                if (player != null && words.Length >= 3)
                {
                    bool path = player.Pathfinder.WalkTo(int.Parse(words[0]), int.Parse(words[1]), int.Parse(words[2]),
                                                         words.Length > 3 ? int.Parse(words[3]) : 0);
                    // Autowalk is stepped by the game scene; this only waits for it to stop.
                    for (int wait = 0; path && wait < 1800 && player.Pathfinder.AutoWalking; wait++)
                    {
                        await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
                    }

                    result = $"{(path ? "path" : "no path")} {player.X} {player.Y} {player.Z}";
                }

                File.WriteAllText(Path.Combine(dir, name + ".arrived"), result);
            }

            // <name>.rec holding "seconds fps": frames into <dir>/<name>/0001.png ...,
            // until the seconds run out or <name>.stop appears,
            // for a clip (tools/editor_objects_proof --live --clip). Windowed only.
            // Runs beside the watch, so a walk can be recorded.
            foreach (string request in Directory.GetFiles(dir, "*.rec"))
            {
                string name = Path.GetFileNameWithoutExtension(request);
                string[] args = File.ReadAllText(request).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                File.Delete(request);
                _ = Record(host, dir, name, args);
            }

            await host.ToSignal(host.GetTree().CreateTimer(0.25), Godot.SceneTreeTimer.SignalName.Timeout);
        }
    }

    private static async System.Threading.Tasks.Task Record(Godot.Node host, string dir, string name, string[] args)
    {
        {
                double seconds = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 10;
                double fps = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10;
                // A third word "jpg" saves JPEGs (quality 0.92): much quicker to write
                // than PNG, so a 1024x768 recording keeps its frame rate.
                bool jpg = args.Length > 2 && args[2] == "jpg";
                string frames = Path.Combine(dir, name);
                Directory.CreateDirectory(frames);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int n = 0;
                // <name>.stop ends it early: a walk of unknown length asks for plenty, then stops it
                string stop = Path.Combine(dir, name + ".stop");
                while (clock.Elapsed.TotalSeconds < seconds && !File.Exists(stop))
                {
                    await host.ToSignal(Godot.RenderingServer.Singleton, Godot.RenderingServerInstance.SignalName.FramePostDraw);
                    if (clock.Elapsed.TotalSeconds * fps >= n)
                    {
                        Godot.Image frame = host.GetViewport().GetTexture().GetImage();
                        if (jpg)
                        {
                            frame.SaveJpg(Path.Combine(frames, $"{++n:D4}.jpg"), 0.92f);
                        }
                        else
                        {
                            frame.SavePng(Path.Combine(frames, $"{++n:D4}.png"));
                        }
                    }
                }

                // "frames seconds": the rate the frames were actually taken at.
                File.WriteAllText(Path.Combine(dir, name + ".recorded"),
                    $"{n} {clock.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    public static void Write(string path)
    {
        World world = Client.Game?.UO?.World;
        var items = new List<Dictionary<string, object>>();
        var mobiles = new List<Dictionary<string, object>>();
        if (world?.Player != null)
        {
            int px = world.Player.X, py = world.Player.Y;
            foreach (Item i in world.Items.Values)
            {
                if (i.OnGround && System.Math.Abs(i.X - px) <= Range && System.Math.Abs(i.Y - py) <= Range)
                {
                    items.Add(new() { ["serial"] = i.Serial, ["graphic"] = $"0x{i.Graphic:X4}", ["hue"] = $"0x{i.Hue:X4}",
                        ["x"] = i.X, ["y"] = i.Y, ["z"] = i.Z, ["name"] = i.Name ?? "" });
                }
            }

            foreach (Mobile m in world.Mobiles.Values)
            {
                if (m != world.Player && System.Math.Abs(m.X - px) <= Range && System.Math.Abs(m.Y - py) <= Range)
                {
                    mobiles.Add(new() { ["serial"] = m.Serial, ["body"] = $"0x{m.Graphic:X4}",
                        ["x"] = m.X, ["y"] = m.Y, ["z"] = m.Z, ["name"] = m.Name ?? "" });
                }
            }
        }

        // What the player wears (items whose container is the player), with the layer.
        var worn = new List<Dictionary<string, object>>();
        var backpack = new List<Dictionary<string, object>>();
        if (world?.Player != null)
        {
            Item pack = world.Player.FindItemByLayer(GUO.Game.Data.Layer.Backpack);
            foreach (Item i in world.Items.Values)
            {
                if (i.Container == world.Player.Serial)
                {
                    worn.Add(new() { ["serial"] = i.Serial, ["graphic"] = $"0x{i.Graphic:X4}", ["layer"] = i.Layer.ToString(),
                        ["hue"] = $"0x{i.Hue:X4}" });
                }
                // what the backpack holds, at its top level (a pack's created item lands there)
                else if (pack != null && i.Container == pack.Serial)
                {
                    backpack.Add(new() { ["serial"] = i.Serial, ["graphic"] = $"0x{i.Graphic:X4}", ["hue"] = $"0x{i.Hue:X4}",
                        ["name"] = i.Name ?? "" });
                }
            }
        }

        var report = new Dictionary<string, object>
        {
            ["worn"] = worn,
            ["backpack"] = backpack,
            ["map"] = world?.MapIndex ?? -1,
            ["player"] = world?.Player != null ? new[] { (int)world.Player.X, world.Player.Y, world.Player.Z } : null,
            ["items"] = items,
            ["mobiles"] = mobiles,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Godot.GD.Print($"[GUO] objects dump: {items.Count} item(s), {mobiles.Count} mobile(s) near the player -> {path}");
    }
}
