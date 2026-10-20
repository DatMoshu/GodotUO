#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039 demo): torch-bearers walking the World tab for a lighting clip without a shard.
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Assets;

public partial class EditorSmoke
{
    /// <summary>
    /// With the world shot: FRAMES walks a torch-bearer along the street with
    /// the camera on them, a second torch-bearer the other way and a lantern
    /// carrier across, saving <c>world_pan/frame_NNNN.png</c> each frame;
    /// <c>FRAMES,fixed</c> keeps the camera on the shot cell and the walkers
    /// cross the frame. The
    /// walkers take real steps (walk animation, facing) through the mobile's
    /// own step queue; their lit hand-held light feeds the scene's lights.
    /// </summary>
    public const string LightsDemoFlag = "--guo-editor-lights-demo";

    private sealed class DemoActor
    {
        public Mobile Mobile;
        public (int x, int y)[] Path;
        public int Next;
        public int Dir = 1;
    }

    private readonly List<DemoActor> _demoActors = new();
    private int _demoFrames, _demoTaken;
    private int _demoFacet;
    private bool _demoFixed;
    private ulong _demoStartMs, _demoLastMs;
    private uint _demoClockStart;
    private const int DemoFps = 30;
    private readonly List<System.Threading.Tasks.Task> _demoWrites = new();
    private System.IO.StreamWriter _demoTimes;
    private StreamWriter _demoMotion;

    private void StartLightsDemo(int facet, int x, int y)
    {
        string[] demo = ArgValue(LightsDemoFlag).Split(',');
        _demoFrames = int.Parse(demo[0]);
        _demoFixed = demo.Length > 1 && demo[1] == "fixed";
        _demoTaken = 0;
        _demoFacet = facet;
        _demoClockStart = GUO.Time.Ticks;
        _world.Host.CaptureTicks = _demoClockStart;
        World world = _world.Host.World;
        if (world?.Map == null)
        {
            _failures.Add("lights demo: no world");
            _demoFrames = 0;
            return;
        }

        // The editor runs no audio: a walker's footsteps must not look for it.
        if (ProfileManager.CurrentProfile != null) ProfileManager.CurrentProfile.EnableFootstepsSound = false;
        // No shard packets or Options Apply run in the editor. Initialize the
        // classic light state too, exactly as the light override does there,
        // so ON/OFF compare the same night rather than night against daylight.
        Profile profile = ProfileManager.CurrentProfile;
        if (profile?.UseCustomLightLevel == true)
        {
            world.Light.Overall = profile.LightLevelType == 1
                ? Math.Min(world.Light.RealOverall, profile.LightLevel)
                : profile.LightLevel;
            world.Light.Personal = 0;
        }
        GD.Print($"[lights-demo] realtime={profile?.RealtimeLights} overall={world.Light.Overall} personal={world.Light.Personal} classicAmbient={world.Light.IsometricLevel}");
        ushort torch = FirstLight(0x0A12, 0x0A0F, 0x0F6B);
        ushort lantern = FirstLight(0x0A25, 0x0A22, 0x0A18, 0x0A15, torch);
        LightColors.GetHue(torch, out ushort torchColor, out bool torchIsHue);
        LightColors.GetHue(lantern, out ushort lanternColor, out bool lanternIsHue);
        GD.Print($"[lights-demo] torch=0x{torch:X4} (light colour {torchColor}, hue {torchIsHue}) lantern=0x{lantern:X4} (light colour {lanternColor}, hue {lanternIsHue})");
        foreach (ushort graphic in new[] { torch, lantern })
        {
            int lightId = _data.Files.TileData.StaticData[graphic].LightIndex;
            ref readonly var info = ref Client.Game.UO.Lights.GetLight((byte)lightId);
            GD.Print($"[lights-demo] graphic=0x{graphic:X4} lightId={lightId} lightWidth={info.UV.Width}");
        }

        // The street: of the two screen axes through the camera cell, the
        // one with the longest open run (no wall or impassable static at
        // walking height) is the walk, the other the crossing. Out and back;
        // the camera rides actor 0, and every path stays inside the others'
        // view range. The passability map goes to the log for checking.
        LogPassability(world, x, y, 20);
        (int ax, int ay, int adx, int ady, int alen) = BestRun(world, x, y, 1, -1, 20);
        (int cx, int cy, int cdx, int cdy, int clen) = BestRun(world, x, y, 1, 1, 20);
        if (clen > alen)
        {
            (ax, ay, adx, ady, alen, cx, cy, cdx, cdy, clen) = (cx, cy, cdx, cdy, clen, ax, ay, adx, ady, alen);
        }

        GD.Print($"[lights-demo] walk from {ax},{ay} by {adx},{ady} x{alen}; crossing from {cx},{cy} by {cdx},{cdy} x{clen}");
        var walk = Line(ax, ay, adx, ady, alen);
        var back = Line(ax + adx * (alen - 1), ay + ady * (alen - 1), -adx, -ady, alen);
        var across = Line(cx, cy, cdx, cdy, clen);
        _demoActors.Clear();
        // Dressed as UO characters: a skin hue, hair, shirt, pants, boots, a cloak or robe.
        _demoActors.Add(Spawn(world, 0x7F000001, 0x0190, 0x03F2, "Torch bearer", torch, walk,
            (0x203B, Layer.Hair, 0x044E), (0x1517, Layer.Shirt, 0x0485), (0x1539, Layer.Pants, 0x0455), (0x170B, Layer.Shoes, 0x0000), (0x1515, Layer.Cloak, 0x0494)));
        _demoActors.Add(Spawn(world, 0x7F000002, 0x0191, 0x0405, "Night walker", torch, back,
            (0x203C, Layer.Hair, 0x045E), (0x1517, Layer.Shirt, 0x0587), (0x1539, Layer.Pants, 0x0001), (0x170B, Layer.Shoes, 0x0000), (0x1515, Layer.Cloak, 0x0455)));
        _demoActors.Add(Spawn(world, 0x7F000003, 0x0190, 0x03EC, "Lantern carrier", lantern, across,
            (0x2048, Layer.Hair, 0x0462), (0x1F03, Layer.Robe, 0x0497), (0x170B, Layer.Shoes, 0x0000)));
    }

    /// <summary>Whether a tile can be walked: no wall or impassable static within 16 Z above its ground.</summary>
    private bool Open(World world, int x, int y)
    {
        if (x < 0 || y < 0) return false;
        sbyte ground = world.Map.GetTileZ(x, y);
        var chunk = world.Map.GetChunk(x, y, load: true);
        if (chunk == null) return false;
        for (GameObject o = chunk.GetHeadObject(x & 7, y & 7); o != null; o = o.TNext)
        {
            ushort g = o is Static st ? st.Graphic : o is Multi mu ? mu.Graphic : (ushort)0;
            if (g == 0 || o.Z < ground - 2 || o.Z > ground + 16) continue;
            ref StaticTiles data = ref _data.Files.TileData.StaticData[g];
            if (data.IsWall || data.IsImpassable) return false;
        }

        return true;
    }

    /// <summary>
    /// The longest open run of at most <paramref name="max"/> tiles along the
    /// axis (dx,dy) through (x,y), in either direction, as its start and step.
    /// </summary>
    private (int x, int y, int dx, int dy, int len) BestRun(World world, int x, int y, int dx, int dy, int max)
    {
        int back = 0, fwd = 0;
        while (back < max && Open(world, x - dx * (back + 1), y - dy * (back + 1))) back++;
        while (fwd < max && Open(world, x + dx * (fwd + 1), y + dy * (fwd + 1))) fwd++;
        int len = Math.Min(max, back + 1 + fwd);
        // Centre the run on the camera cell when it is longer than the walk.
        int start = Math.Max(-back, Math.Min(-(len / 2), fwd - len + 1));
        return (x + dx * start, y + dy * start, dx, dy, Math.Max(2, len));
    }

    private void LogPassability(World world, int x, int y, int r)
    {
        var sb = new System.Text.StringBuilder($"[lights-demo] passability around {x},{y} ('.' open, '#' blocked, '@' camera), rows are y:\n");
        for (int yy = y - r; yy <= y + r; yy++)
        {
            for (int xx = x - r; xx <= x + r; xx++) sb.Append(xx == x && yy == y ? '@' : Open(world, xx, yy) ? '.' : '#');
            sb.Append('\n');
        }

        GD.Print(sb.ToString());
    }

    private static (int x, int y)[] Line(int x, int y, int dx, int dy, int count)
    {
        var path = new (int, int)[count];
        for (int i = 0; i < count; i++) path[i] = (x + dx * i, y + dy * i);
        return path;
    }

    private ushort FirstLight(params ushort[] candidates)
    {
        ref var statics = ref _data.Files.TileData.StaticData;
        foreach (ushort g in candidates)
        {
            if (g < statics.Length && statics[g].IsLight) return g;
        }
        return candidates[0];
    }

    private DemoActor Spawn(World world, uint serial, ushort body, ushort hue, string name, ushort hand, (int x, int y)[] path,
        params (ushort graphic, Layer layer, ushort hue)[] wear)
    {
        var m = new Mobile(world, serial);
        world.Mobiles[serial] = m;
        (int sx, int sy) = path[0];
        m.X = (ushort)sx;
        m.Y = (ushort)sy;
        m.Z = world.Map.GetTileZ(sx, sy);
        m.Graphic = body;
        m.Name = name;
        m.FixHue(hue);
        m.Hits = m.HitsMax = 100;
        m.Direction = DirectionHelper.CalculateDirection(sx, sy, path[1].x, path[1].y);
        m.AddToTile();

        var item = new Item(world) { Serial = serial + 0x100, Graphic = hand, Layer = Layer.TwoHanded, Container = serial };
        m.PushToBack(item);
        uint next = serial + 0x101;
        foreach ((ushort graphic, Layer layer, ushort itemHue) in wear)
        {
            var worn = new Item(world) { Serial = next++, Graphic = graphic, Layer = layer, Container = serial };
            worn.FixHue(itemHue);
            m.PushToBack(worn);
        }

        return new DemoActor { Mobile = m, Path = path, Next = 1 };
    }

    private void StepLightsDemo()
    {
        if (_frames < 60)
        {
            return;
        }

        // An unfocused editor sleeps 100 ms a frame; the clip needs 30 a second.
        OS.LowProcessorUsageModeSleepUsec = 1000;

        // Fixed timestep: the game clock the walkers step and animate by
        // advances exactly one frame per captured frame, however long the
        // editor took to draw it, so machine load cannot show in the clip.
        GUO.Time.Ticks = unchecked(_demoClockStart + (uint)((long)_demoTaken * 1000 / DemoFps));
        _world.Host.CaptureTicks = GUO.Time.Ticks;
        World world = _world.Host.World;
        foreach (DemoActor a in _demoActors)
        {
            Mobile m = a.Mobile;
            // A remote mobile with a backlog uses ClassicUO's mounted-speed
            // catch-up path. Feed one ordinary walk at a time instead, replacing
            // a completed step in this same tick, without an idle frame between.
            if (m.Steps.Count == 0) QueueDemoStep(a, world);
            m.Update();
            if (m.Steps.Count == 0) QueueDemoStep(a, world);
        }

        // The camera rides the first walker: same tile, same step offset.
        PlayerMobile player = world.Player;
        Mobile lead = _demoActors[0].Mobile;
        if (!_demoFixed && player != null && lead != null)
        {
            if (player.X != lead.X || player.Y != lead.Y || player.Z != lead.Z)
            {
                _world.GoTo(_demoFacet, lead.X, lead.Y);
                player.Z = lead.Z;
            }

            player.Offset = lead.Offset;
        }

        // Every editor frame is a clip frame; times.txt carries each frame's
        // clock time (always 1000/DemoFps apart) and its real time. The PNGs
        // are written off the main thread: encoding them on it starves the
        // editor.
        ulong now = Time.GetTicksMsec();
        ulong sinceLast = _demoLastMs == 0 ? 0 : now - _demoLastMs;
        _demoLastMs = now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_demoTaken == 0)
        {
            _demoStartMs = now;
            string dir = Path.Combine(_out, "world_pan");
            Directory.CreateDirectory(dir);
            _demoTimes = new StreamWriter(Path.Combine(dir, "times.txt"));
            _demoMotion = new StreamWriter(Path.Combine(dir, "motion.csv"));
            _demoMotion.WriteLine("prepared_frame,clock_ms,actor,tile_x,tile_y,offset_x,offset_y,projected_x,projected_y,steps,animation");
        }

        foreach (DemoActor a in _demoActors)
        {
            Mobile m = a.Mobile;
            // Camera-independent projected foot anchor; the viewport captured
            // below can still contain the preceding render's prepared state.
            int px = (m.X - m.Y) * 22 + (int)m.Offset.X;
            int py = (m.X + m.Y) * 22 - m.Z * 4 + (int)m.Offset.Y - (int)m.Offset.Z;
            _demoMotion.WriteLine(FormattableString.Invariant($"{_demoTaken},{GUO.Time.Ticks},{m.Serial},{m.X},{m.Y},{m.Offset.X},{m.Offset.Y},{px},{py},{m.Steps.Count},{m.AnimIndex}"));
        }

        Image frame = _world.Capture();
        long captureMs = sw.ElapsedMilliseconds;
        if (frame != null && !frame.IsEmpty())
        {
            string path = Path.Combine(_out, "world_pan", $"frame_{_demoTaken:0000}.png");
            _demoWrites.RemoveAll(t =>
            {
                if (!t.IsCompleted) return false;
                t.GetAwaiter().GetResult();
                return true;
            });
            while (_demoWrites.Count >= 12)
            {
                _demoWrites[0].Wait();
                _demoWrites.RemoveAt(0);
            }

            _demoWrites.Add(System.Threading.Tasks.Task.Run(() =>
            {
                using (frame)
                {
                    Error error = frame.SavePng(path);
                    if (error != Error.Ok) throw new IOException($"lights demo frame write failed: {error}");
                }
            }));
        }
        else
        {
            throw new IOException($"lights demo frame {_demoTaken} was empty");
        }

        if (sinceLast > 80)
        {
            GD.Print($"[lights-demo] slow frame {_demoTaken}: {sinceLast} ms since the last; capture {captureMs} ms, write queue {sw.ElapsedMilliseconds - captureMs} ms, gc gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }

        _demoTimes.WriteLine(FormattableString.Invariant($"{_demoTaken} {_demoTaken * 1000.0 / DemoFps:F6} {now - _demoStartMs}"));
        if (_demoTaken % 30 == 0)
        {
            GD.Print($"[lights-demo] frame {_demoTaken} at {now - _demoStartMs} ms: A={lead?.X},{lead?.Y} steps={lead?.Steps.Count} offset={lead?.Offset.X:0},{lead?.Offset.Y:0} anim={lead?.AnimIndex}");
        }

        _demoTaken++;
        if (_demoTaken >= _demoFrames)
        {
            foreach (var t in _demoWrites) t.Wait();
            _demoWrites.Clear();
            _demoTimes.Dispose();
            _demoTimes = null;
            _demoMotion.Dispose();
            _demoMotion = null;
            SaveWorldShot();
            _world.Host.CaptureTicks = null;
            Finish();
        }
    }

    private void QueueDemoStep(DemoActor a, World world)
    {
        if (a.Next >= a.Path.Length || a.Next < 0)
        {
            a.Dir = -a.Dir;
            // The endpoint was already reached: reverse toward its neighbour.
            a.Next = a.Dir > 0 ? 1 : a.Path.Length - 2;
        }

        (int nx, int ny) = a.Path[a.Next];
        Mobile m = a.Mobile;
        Direction dir = DirectionHelper.CalculateDirection(m.X, m.Y, nx, ny);
        if (!m.EnqueueStep(nx, ny, world.Map.GetTileZ(nx, ny), dir, false))
            throw new InvalidOperationException("lights demo could not queue its next walk");
        a.Next += a.Dir;
    }
}
#endif
