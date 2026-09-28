// SPDX-License-Identifier: BSD-2-Clause

// Compiled into GUO, and into the out-of-tree ClassicUO build (with
// PERF_DUMP_CUO defined, through tools\render_dump\inject.targets), as
// PerfDump is: both clients photograph the same spot the same way, with their
// own screenshot code, and nothing types into either window. sources\ is never
// edited.

#if PERF_DUMP_CUO
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClientRoot = ClassicUO.Client;
using Env = ClassicUO.CUOEnviroment;
#else
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using ClientRoot = GUO.Client;
using Env = GUO.CUOEnviroment;
#endif

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

#if PERF_DUMP_CUO
namespace ClassicUO.PerfDumpInjected;
#else
namespace GUO.Host;
#endif

/// <summary>
/// Visual A/B (B7): stand in one spot, do a few scripted things, and save the
/// client's own screenshot after each, so ClassicUO and GUO (with any renderer
/// flags) can be laid side by side.
/// </summary>
/// <remarks>
/// Off unless GUO_SHOT_DUMP is set to "DIR;LABEL;X;Y;Z;ZOOM;STEPS" (Z may be
/// empty). Once in the world, on the game loop: [go X Y Z, the zoom, a wait
/// for the fade-in after the jump, then STEPS, separated by '|':
/// <list type="bullet">
/// <item>shot:NAME -- the client's own screenshot (TakeScreenshot), moved to DIR\LABEL\VARIANT_NAME.png</item>
/// <item>anim:GROUP -- the player loops animation group GROUP (0x6E-style, client-side)</item>
/// <item>wait:MS</item>
/// <item>say:TEXT -- said as the player would (a shard command with '['); {x}, {y}, {z}, {x+2},
/// {y-1}, ... become the player's position</item>
/// <item>set:PROPERTY=VALUE -- a profile property (DrawRoofs=false, ...), put back as it was
/// before the run ends, so a saved profile never keeps it</item>
/// <item>door[:X,Y] -- double-click the nearest door item within 6 tiles of the player (or within 2 of X,Y)</item>
/// <item>find:X,Y -- only note the doors within 2 tiles of X,Y</item>
/// <item>land:X,Y -- note the land tile there: graphic, z, stretched, texmap, and in GUO the
/// UVs its chunk mesh holds against the texmap's own</item>
/// <item>hide -- hide every gump but the game window, for this run only (nothing is closed or saved)</item>
/// <item>walk:DIRECTION[:run] -- one step (North, Right, East, Down, South, Left, West, Up); the
/// next step follows 120 ms on, so a shot right after it lands mid-step</item>
/// <item>goto:X,Y,Z -- the client's pathfinder walks the player to within a tile of X,Y,Z; the
/// next step follows once it stops (60 s at most). A player without [go reaches a place this way</item>
/// </list>
/// VARIANT is "cuo" or "guo", or GUO_SHOT_VARIANT when set (guo-array, ...).
/// DIR\LABEL\VARIANT.done is written last, with a line per step.
/// </remarks>
internal static class ShotDump
{
#if PERF_DUMP_CUO
    private const string ClientName = "cuo";
#else
    private const string ClientName = "guo";
#endif

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static string[] _args;
    private static Timer _timer;
    private static object _attached;
    private static Queue<string> _steps;
    private static readonly List<string> _log = new();
    private static string _variant, _dir;
    private static long _fadeStart = -1;
    private static readonly List<(PropertyInfo, object)> _restore = new();

    [ModuleInitializer]
    internal static void Start()
    {
        string spec = Environment.GetEnvironmentVariable("GUO_SHOT_DUMP");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        _args = spec.Split(';', 7);
        if (_args.Length < 7)
        {
            Console.WriteLine("[shot_dump] GUO_SHOT_DUMP wants DIR;LABEL;X;Y;Z;ZOOM;STEPS");
            return;
        }

        string v = Environment.GetEnvironmentVariable("GUO_SHOT_VARIANT");
        _variant = string.IsNullOrWhiteSpace(v) ? ClientName : v;
        _dir = Path.Combine(_args[0], _args[1]);
        _steps = new Queue<string>(_args[6].Split('|', StringSplitOptions.RemoveEmptyEntries));
        _timer = new Timer(_ => Attach(), null, 1000, 1000);
    }

    private static void Attach()
    {
        try
        {
            if (ClientRoot.Game?.Scene is not GameScene scene || Field(scene, "_world") is not World world
                || ReferenceEquals(world, _attached) || world.Player == null)
            {
                return;
            }

            // As PerfDump: onto the game loop a few seconds on, once.
            _attached = world;
            _timer.Dispose();
            _timer = null;
            ClientRoot.Game.EnqueueAction(3000, Go);
            Console.WriteLine($"[shot_dump] {_variant}: in the world; starting in 3 s");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[shot_dump] attach failed: {e.Message}");
        }
    }

    private static World World => _attached as World;

    private static void Go()
    {
        float zoom = float.Parse(_args[5], CultureInfo.InvariantCulture);
        var camera = ClientRoot.Game.Scene.Camera;
        if (zoom > 0)
        {
            camera.Zoom = Math.Clamp(zoom, camera.ZoomMin, camera.ZoomMax);
        }

        string go = $"[go {_args[2]} {_args[3]}" + (string.IsNullOrWhiteSpace(_args[4]) ? "" : $" {_args[4]}");
        GameActions.Say(go);
        Note($"{go}, zoom {camera.Zoom:F2}");
        ClientRoot.Game.EnqueueAction(3000, WaitForFade);
    }

    // After a jump everything in view fades in; a shot mid-fade shows the fade.
    private static void WaitForFade()
    {
        long now = Environment.TickCount64;
        if (_fadeStart < 0)
        {
            _fadeStart = now;
        }

        int fading = Field(ClientRoot.Game.Scene, "_renderLists") is object lists
                     && Field(lists, "_transparentObjects") is System.Collections.ICollection c ? c.Count : -1;
        if (fading <= 20 || now - _fadeStart >= 30_000)
        {
            Note($"{fading} fading after {now - _fadeStart} ms");
            ClientRoot.Game.EnqueueAction(1000, Next);
            return;
        }

        ClientRoot.Game.EnqueueAction(500, WaitForFade);
    }

    private static void Next()
    {
        if (_steps.Count == 0)
        {
            for (int i = _restore.Count - 1; i >= 0; i--)
            {
                _restore[i].Item1.SetValue(ProfileManager.CurrentProfile, _restore[i].Item2);
            }

            Directory.CreateDirectory(_dir);
            File.WriteAllLines(Path.Combine(_dir, _variant + ".done"), _log);
            Console.WriteLine($"[shot_dump] {_variant}: done");
            return;
        }

        string step = _steps.Dequeue();
        uint delay = 300;
        try
        {
            int colon = step.IndexOf(':');
            string verb = colon < 0 ? step : step.Substring(0, colon);
            string arg = colon < 0 ? "" : step.Substring(colon + 1);
            switch (verb)
            {
                case "shot":
                    Shot(arg);
                    // Screenshot names carry the second; the next one must not share it.
                    delay = 1200;
                    break;
                case "wait":
                    delay = uint.Parse(arg, CultureInfo.InvariantCulture);
                    Note($"wait {arg} ms");
                    break;
                case "say":
                    arg = Placeholders(arg);
                    GameActions.Say(arg);
                    Note($"said {arg}");
                    delay = 1500;
                    break;
                case "set":
                    Set(arg);
                    break;
                case "door":
                    Door(arg, true);
                    // The door's own open/close animation is a couple of frames.
                    delay = 1500;
                    break;
                case "find":
                    Door(arg, false);
                    break;
                case "land":
                    LandAt(arg);
                    break;
                case "hide":
                    Hide();
                    break;
                case "walk":
                    Walk(arg);
                    delay = 120;
                    break;
                case "goto":
                    {
                        string[] xyz = arg.Split(',');
                        int gx = int.Parse(xyz[0], CultureInfo.InvariantCulture);
                        int gy = int.Parse(xyz[1], CultureInfo.InvariantCulture);
                        int gz = xyz.Length > 2 ? int.Parse(xyz[2], CultureInfo.InvariantCulture) : World.Player.Z;
                        var p = World.Player;
                        bool ok = p.Pathfinder.WalkTo(gx, gy, gz, 1);
                        Note($"goto {gx},{gy},{gz} from {p.X},{p.Y},{p.Z}: {(ok ? "walking" : "no path")}");
                        if (ok)
                        {
                            long until = Environment.TickCount64 + 60_000;
                            ClientRoot.Game.EnqueueAction(500, () => Arrive(until));
                            return;
                        }
                    }
                    break;
                case "anim":
                    {
                        // The player plays animation group GROUP on a loop, as a
                        // server's 0x6E would ask: a look at a body's actions
                        // (an attack) without a fight. Client-side only.
                        byte group = byte.Parse(arg, CultureInfo.InvariantCulture);
                        World.Player.SetAnimation(group, 2, 0, 20, true, true, true);
                        Note($"anim {group}");
                    }
                    break;
                default:
                    Note($"unknown step {step}");
                    break;
            }
        }
        catch (Exception e)
        {
            Note($"{step} failed: {e.Message}");
        }

        ClientRoot.Game.EnqueueAction(delay, Next);
    }

    private static void Shot(string name)
    {
        string folder = Path.Combine(Env.ExecutablePath, "Data", "Client", "Screenshots");
        DateTime before = DateTime.Now.AddSeconds(-1);
        ClientRoot.Game.GetType().GetMethod("TakeScreenshot", Any)!.Invoke(ClientRoot.Game, null);
        FileInfo shot = new DirectoryInfo(folder).GetFiles("screenshot_*.png")
            .Where(f => f.LastWriteTime >= before).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (shot == null)
        {
            Note($"shot {name}: no screenshot appeared in {folder}");
            return;
        }

        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, $"{_variant}_{name}.png");
        File.Copy(shot.FullName, target, true);
        shot.Delete();
        var p = World?.Player;
        Note($"shot {name} at {p?.X},{p?.Y},{p?.Z}");
    }

    private static void Set(string assignment)
    {
        int eq = assignment.IndexOf('=');
        string name = assignment.Substring(0, eq);
        string value = assignment.Substring(eq + 1);
        PropertyInfo prop = typeof(Profile).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        _restore.Add((prop, prop!.GetValue(ProfileManager.CurrentProfile)));
        prop.SetValue(ProfileManager.CurrentProfile, Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture));
        Note($"profile {name} = {value}");
    }

    private static void Door(string at, bool click)
    {
        var player = World.Player;
        int x = player.X, y = player.Y, range = 6;
        if (!string.IsNullOrEmpty(at))
        {
            string[] xy = at.Split(',');
            x = int.Parse(xy[0], CultureInfo.InvariantCulture);
            y = int.Parse(xy[1], CultureInfo.InvariantCulture);
            range = 2;
        }

        Item best = null;
        int bestDistance = int.MaxValue;
        foreach (Item item in World.Items.Values)
        {
            if (!item.OnGround || !item.ItemData.IsDoor)
            {
                continue;
            }

            int d = Math.Max(Math.Abs(item.X - x), Math.Abs(item.Y - y));
            if (d <= range && d < bestDistance)
            {
                best = item;
                bestDistance = d;
            }
        }

        if (best == null)
        {
            Note($"door: none within {range} tiles of {x},{y}");
            return;
        }

        if (click)
        {
            GameActions.DoubleClick(World, best.Serial);
        }

        Note($"door 0x{best.Graphic:X4} at {best.X},{best.Y},{best.Z} {(click ? "double-clicked" : "found")}");
    }

    private static void LandAt(string at)
    {
        string[] xy = at.Split(',');
        int x = int.Parse(xy[0], CultureInfo.InvariantCulture), y = int.Parse(xy[1], CultureInfo.InvariantCulture);
        Land land = null;
        for (GameObject o = World.Map.GetTile(x, y, false); o != null; o = o.TNext)
        {
            if (o is Land l)
            {
                land = l;
                break;
            }
        }

        if (land == null)
        {
            Note($"land {x},{y}: none loaded");
            return;
        }

        ushort tex = land.TileData.TexID;
        ref readonly var texmap = ref ClientRoot.Game.UO.Texmaps.GetTexmap(tex);
        string line = $"land {x},{y} at screen {land.RealScreenPosition.X},{land.RealScreenPosition.Y}: 0x{land.Graphic:X4} z {land.Z} stretched {land.IsStretched} texmap 0x{tex:X4} "
                      + $"uv {texmap.UV.X},{texmap.UV.Y},{texmap.UV.Width},{texmap.UV.Height} "
                      + $"avgz {land.AverageZ} minz {land.MinZ} yoff {land.YOffsets.Top},{land.YOffsets.Right},{land.YOffsets.Bottom},{land.YOffsets.Left} "
                      + $"n {N(land.NormalTop)} {N(land.NormalRight)} {N(land.NormalBottom)} {N(land.NormalLeft)}";
#if !PERF_DUMP_CUO
        var chunk = World.Map.GetChunk(x, y, false);
        int i = land.MeshSpriteIndex;
        if (chunk != null && land.InChunkMesh && i >= 0 && i < chunk.Mesh.Land.Count)
        {
            var q = chunk.Mesh.Land.Vertices[i];
            var t = chunk.Mesh.Land.Textures[i];
            line += $"; mesh #{i} tex {(ReferenceEquals(t, texmap.Texture) ? "same" : "OTHER")} {t?.GetWidth()}x{t?.GetHeight()} "
                    + $"uv0 {q.TextureCoordinate0.X * t?.GetWidth():F1},{q.TextureCoordinate0.Y * t?.GetHeight():F1} "
                    + $"uv3 {q.TextureCoordinate3.X * t?.GetWidth():F1},{q.TextureCoordinate3.Y * t?.GetHeight():F1}";
            if (!land.IsStretched && t != null)
            {
                ref readonly var art = ref ClientRoot.Game.UO.Arts.GetLand(land.Graphic);
                int cx = (int)(q.TextureCoordinate0.X * t.GetWidth()) + 22, cy = (int)(q.TextureCoordinate0.Y * t.GetHeight()) + 22;
                var gpu = t.GetImage()?.GetPixel(cx, cy);
                var cpu = TextureAtlasPage(t)?.GetPixel(cx, cy);
                line += $"; art {(ReferenceEquals(art.Texture, t) ? "same" : "OTHER")} uv {art.UV.X},{art.UV.Y}"
                        + $"; gpu {gpu?.ToHtml(false)} cpu {cpu?.ToHtml(false)}";
            }
        }
        else
        {
            line += $"; not in a mesh (index {i}, in mesh {land.InChunkMesh})";
        }
#endif
        Note(line);
    }

#if !PERF_DUMP_CUO
    private static Godot.Image TextureAtlasPage(Godot.Texture2D t) =>
        GUO.Renderer.TextureAtlas.TryGetPage(t, out Godot.Image page, out _) ? page : null;
#endif

    private static string N(object v) => v.ToString()?.Replace(" ", "");

    private static string Placeholders(string text)
    {
        var p = World.Player;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{([xyz])([+-]\d+)?\}", m =>
        {
            int v = m.Groups[1].Value switch { "x" => p.X, "y" => p.Y, _ => p.Z };
            if (m.Groups[2].Success)
            {
                v += int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            return v.ToString(CultureInfo.InvariantCulture);
        });
    }

    private static void Hide()
    {
        int hidden = 0;
        foreach (Gump gump in UIManager.Gumps)
        {
            if (gump is not WorldViewportGump && gump.IsVisible)
            {
                gump.IsVisible = false;
                hidden++;
            }
        }

        Note($"hid {hidden} gumps");
    }

    // goto: next step once the pathfinder has stopped, or the time is up.
    private static void Arrive(long until)
    {
        var p = World.Player;
        if (p.Pathfinder.AutoWalking && Environment.TickCount64 < until)
        {
            ClientRoot.Game.EnqueueAction(500, () => Arrive(until));
            return;
        }

        Note($"goto ended at {p.X},{p.Y},{p.Z}{(p.Pathfinder.AutoWalking ? " (timed out)" : "")}");
        ClientRoot.Game.EnqueueAction(500, Next);
    }

    private static void Walk(string arg)
    {
        string[] parts = arg.Split(':');
        var direction = (Direction)Enum.Parse(typeof(Direction), parts[0], true);
        var p = World.Player;
        bool ok = p.Walk(direction, parts.Length > 1 && parts[1] == "run");
        Note($"walk {direction} from {p.X},{p.Y},{p.Z}: {(ok ? "started" : "refused")}");
    }

    private static void Note(string line)
    {
        _log.Add(line);
        Console.WriteLine($"[shot_dump] {_variant}: {line}");
    }

    private static object Field(object target, string name)
    {
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
            if (f != null)
            {
                return f.GetValue(target);
            }
        }

        return null;
    }
}
