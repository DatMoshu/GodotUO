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

            // <name>.rec holding "seconds fps": frames into <dir>/<name>/0001.png ...,
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
                string frames = Path.Combine(dir, name);
                Directory.CreateDirectory(frames);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int n = 0;
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    await host.ToSignal(Godot.RenderingServer.Singleton, Godot.RenderingServerInstance.SignalName.FramePostDraw);
                    if (clock.Elapsed.TotalSeconds * fps >= n)
                    {
                        host.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(frames, $"{++n:D4}.png"));
                    }
                }

                File.WriteAllText(Path.Combine(dir, name + ".recorded"), n.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        if (world?.Player != null)
        {
            foreach (Item i in world.Items.Values)
            {
                if (i.Container == world.Player.Serial)
                {
                    worn.Add(new() { ["serial"] = i.Serial, ["graphic"] = $"0x{i.Graphic:X4}", ["layer"] = i.Layer.ToString(),
                        ["hue"] = $"0x{i.Hue:X4}" });
                }
            }
        }

        var report = new Dictionary<string, object>
        {
            ["worn"] = worn,
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
