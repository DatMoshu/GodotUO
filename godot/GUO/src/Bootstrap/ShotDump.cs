// SPDX-License-Identifier: BSD-2-Clause

// Compiled into GUO, and into the out-of-tree ClassicUO build (with
// PERF_DUMP_CUO defined, through tools\render_dump\inject.targets), as
// PerfDump is: both clients photograph the same spot the same way, with their
// own screenshot code, and nothing types into either window. sources\ is never
// edited.

#if PERF_DUMP_CUO
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Scenes;
using ClientRoot = ClassicUO.Client;
using Env = ClassicUO.CUOEnviroment;
#else
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
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
/// <item>wait:MS</item>
/// <item>say:TEXT -- said as the player would (a shard command with '[')</item>
/// <item>set:PROPERTY=VALUE -- a profile property (DrawRoofs=false, ...)</item>
/// <item>door -- double-click the nearest door item within 6 tiles</item>
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
                    GameActions.Say(arg);
                    Note($"said {arg}");
                    delay = 1500;
                    break;
                case "set":
                    Set(arg);
                    break;
                case "door":
                    Door();
                    // The door's own open/close animation is a couple of frames.
                    delay = 1500;
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
        prop!.SetValue(ProfileManager.CurrentProfile, Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture));
        Note($"profile {name} = {value}");
    }

    private static void Door()
    {
        var player = World.Player;
        Item best = null;
        int bestDistance = int.MaxValue;
        foreach (Item item in World.Items.Values)
        {
            if (!item.OnGround || !item.ItemData.IsDoor)
            {
                continue;
            }

            int d = Math.Max(Math.Abs(item.X - player.X), Math.Abs(item.Y - player.Y));
            if (d <= 6 && d < bestDistance)
            {
                best = item;
                bestDistance = d;
            }
        }

        if (best == null)
        {
            Note("door: none within 6 tiles");
            return;
        }

        GameActions.DoubleClick(World, best.Serial);
        Note($"door 0x{best.Graphic:X4} at {best.X},{best.Y},{best.Z} double-clicked");
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
