// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): water ripples (ADR-0037 decision 5, WATER-03). Not upstream's.
using System;
using System.Collections.Generic;
using GUO.Game;
using GUO.Game.GameObjects;
using Godot;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Renderer.Water;

/// <summary>
/// The ripple field: a small wave simulation over the water around the
/// player, 2 x 2 texels a cell, which the water shading reads as a height.
/// Things on the water disturb it: a mobile standing or swimming in water, a
/// boat that moves (its hull outline, which leaves a bow wave and a wake),
/// an effect that lands on water (a splash, a thrown or fishing effect) and
/// an item that comes to rest on a water cell. Purely cosmetic: nothing here
/// feeds movement, targeting or the server.
/// </summary>
/// <remarks>
/// A plain two-buffer wave equation stepped at a fixed 20 Hz, so it looks
/// the same at any frame rate. When nothing has disturbed it for a while it
/// stops stepping and stops uploading; a calm sea costs one check a frame.
/// The window follows the player: when the player has moved far enough it
/// shifts by whole cells, keeping the waves that are still inside.
/// </remarks>
internal sealed class WaterRipples
{
    public const int Cells = 128;
    public const int PerCell = 2;
    public const int Size = Cells * PerCell;
    private const int Recentre = 16;
    private const float StepSeconds = 1f / 20f;
    // How far a wave front moves in a step, squared (texels): 0.1 is about
    // a third of a texel, three cells a second at 20 Hz -- slow enough to
    // see a ring open out. Must stay under 0.5 for the scheme to be stable.
    private const float Speed2 = 0.1f;
    private const int MaxStepsPerFrame = 4;
    private const int ScanRadius = 24;
    // A hull slower than this (cells a second) is at rest: no wake.
    private const float MinWakeSpeed = 0.45f;
    // Closer than this, a new item was not walked into view (the client
    // sees about 18 cells out): it was put there.
    private const int InsideView = 14;

    private float[] _now = new float[Size * Size];
    private float[] _before = new float[Size * Size];
    private float[] _scratch = new float[Size * Size];
    private readonly byte[] _pixels = new byte[Size * Size];
    private Image _image;
    private ImageTexture _texture;

    private float _accumulated;
    private double _calmFor = double.MaxValue;
    private bool _uploaded;

    // Per boat: last position, smoothed speed (cells a second) and heading (unit, cells).
    internal sealed class BoatState { public float X, Y, Speed, HX, HY, Emit; }
    // The wake proper: short-lived marks left astern and at the bow, drawn each step into a
    // layer that is added to the waves, so it stays attached to the hull's track and is gone
    // when the marks expire (not a wave that runs off by itself).
    private struct WakeMark { public float X, Y, VX, VY, Age, Life, Amp, Radius; }
    private readonly List<WakeMark> _marks = new List<WakeMark>();
    private readonly float[] _wake = new float[Size * Size];
    private const int MaxMarks = 600;
    private const float EmitEvery = 0.05f;
    private readonly Dictionary<uint, BoatState> _boats = new Dictionary<uint, BoatState>();
    // When each swimmer last made a ring (ms), so a walking creature leaves a trail of small ones.
    private readonly Dictionary<uint, long> _swimmers = new Dictionary<uint, long>();
    private readonly List<uint> _swimDrop = new List<uint>();
    private readonly HashSet<uint> _swimSeen = new HashSet<uint>();
    // Texels that hold water (1) or not (0): the field is cleared on dry ones so a
    // wave never runs up a beach or under a pier into water on the far side.
    private readonly byte[] _wet = new byte[Size * Size];
    private long _wetAt = long.MinValue;
    private int _wetOriginX = int.MinValue, _wetOriginY;
    private readonly Dictionary<uint, (int X, int Y)> _items = new Dictionary<uint, (int, int)>();
    private readonly Dictionary<GameEffect, (int X, int Y)> _effects = new Dictionary<GameEffect, (int, int)>();
    private readonly HashSet<GameEffect> _seenEffects = new HashSet<GameEffect>();
    private readonly List<GameEffect> _gone = new List<GameEffect>();
    private readonly HashSet<uint> _seen = new HashSet<uint>();
    private readonly List<uint> _drop = new List<uint>();
    private int _lastPlayerX = int.MinValue, _lastPlayerY;
    private bool _jumped;
    // Shared bounded input; only lighting water or an enabled Lab consumes it.
    private readonly RainRippleEmitter _rain = new();
#if GUO_LABS
    private readonly GUO.Labs.WaterRain.RainRippleEmitter _labRain = new();
#endif

    /// <summary>The first cell of the window; the shader's ripple_origin.</summary>
    public int OriginX { get; private set; } = int.MinValue;
    public int OriginY { get; private set; } = int.MinValue;

    public Texture2D Texture => _texture;

    /// <summary>How strongly a disturbance pushes the surface (the style's ripple settings).</summary>
    public float Strength = 1f;

    /// <summary>How much of a wave is left after one step: 1 rings for ever, lower calms faster.</summary>
    public float Damping = 0.985f;

    /// <summary>For the probe and the perf table: steps run and stamps made so far.</summary>
    public int Steps { get; private set; }
    public int Stamps { get; private set; }
    internal int RainStamps { get; private set; }
    public double LastUpdateMs { get; private set; }
    /// <summary>For the perf table: the last open-water map rebuild (ms) and swimmer rings made so far.</summary>
    public double LastWetMs { get; private set; }
    public int SwimStamps { get; private set; }

    /// <summary>Forget everything: a new map, a style switched off and on.</summary>
    public void Reset()
    {
        _rain.Reset();
#if GUO_LABS
        _labRain.Reset();
#endif
        Array.Clear(_now);
        Array.Clear(_before);
        Array.Clear(_wake);
        _marks.Clear();
        _boats.Clear();
        _swimmers.Clear();
        _wetAt = long.MinValue;
        _items.Clear();
        _effects.Clear();
        _seenEffects.Clear();
        OriginX = OriginY = int.MinValue;
        _calmFor = double.MaxValue;
        _uploaded = false;
    }

    /// <summary>
    /// One frame: follow the player, find what disturbs the water, step the
    /// waves. True when the texture or the origin changed and the shader
    /// needs them again.
    /// </summary>
    public bool Update(World world, WaterMask mask, float deltaSeconds)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool moved = Follow(world.Player.X, world.Player.Y);
        // A teleport or the first frame: what is in view now was already
        // there, so nothing in it splashes.
        _jumped = _lastPlayerX == int.MinValue
            || Math.Abs(world.Player.X - _lastPlayerX) > 2 || Math.Abs(world.Player.Y - _lastPlayerY) > 2;
        _lastPlayerX = world.Player.X;
        _lastPlayerY = world.Player.Y;

        if (WaterRender.EnvironmentEnabled(GUO.Configuration.ProfileManager.CurrentProfile))
            RainSurfaceQuery.UpdateLighting(world, mask, this, _rain, deltaSeconds, _jumped);
#if GUO_LABS
        else GUO.Labs.WaterRain.WaterRainLab.Update(world, mask, this, _labRain, deltaSeconds, _jumped);
#endif
        if (!WaterRender.EnvironmentEnabled(GUO.Configuration.ProfileManager.CurrentProfile)) _rain.Reset();
        RainStamps += _rain.LastStamps;
        Disturb(world, mask, Math.Max(deltaSeconds, 1f / 240f));

        if (_marks.Count > 0) _calmFor = 0;
        bool stepped = false;
        if (_calmFor < 6.0)
        {
            _accumulated += Math.Min(deltaSeconds, StepSeconds * MaxStepsPerFrame);
            while (_accumulated >= StepSeconds)
            {
                _accumulated -= StepSeconds;
                Step();
                stepped = true;
            }
            _calmFor += deltaSeconds;
        }
        else if (_uploaded)
        {
            // Calm long enough: one last flat upload, then nothing per frame.
            Array.Clear(_now);
            Array.Clear(_before);
            Array.Clear(_wake);
            stepped = true;
        }

        bool changed = moved;
        if (stepped || _texture == null)
        {
            Upload();
            _uploaded = _calmFor < 6.0;
            changed = true;
        }

        LastUpdateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return changed;
    }

    /// <summary>
    /// Pushes the surface down at a world position in cells (fractions
    /// allowed), over a radius in cells. Positions outside the window are ignored.
    /// </summary>
    public void Stamp(float cellX, float cellY, float radius, float amount)
    {
        if (StampInto(_now, false, cellX, cellY, radius, amount))
        {
            Stamps++;
            _calmFor = 0;
        }
    }

    // wake: the boat-wake layer, where the largest mark wins instead of adding up and
    // dry texels are skipped.
    private bool StampInto(float[] field, bool wake, float cellX, float cellY, float radius, float amount)
    {
        float cx = (cellX - OriginX + 0.5f) * PerCell;
        float cy = (cellY - OriginY + 0.5f) * PerCell;
        float r = Math.Max(0.75f, radius * PerCell);
        int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r);
        int y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
        if (x1 < 1 || y1 < 1 || x0 >= Size - 1 || y0 >= Size - 1)
        {
            return false;
        }

        bool clip = wake && _wetAt != long.MinValue;
        for (int y = Math.Max(1, y0); y <= Math.Min(Size - 2, y1); y++)
        {
            for (int x = Math.Max(1, x0); x <= Math.Min(Size - 2, x1); x++)
            {
                float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) / r;
                if (d < 1f)
                {
                    int i = y * Size + x;
                    if (clip && _wet[i] == 0) continue;
                    float fall = 0.5f + 0.5f * MathF.Cos(d * MathF.PI);
                    if (wake)
                    {
                        float v = -amount * Strength * fall;
                        if (MathF.Abs(v) > MathF.Abs(field[i])) field[i] = Math.Clamp(v, -1f, 1f);
                    }
                    else field[i] = Math.Clamp(field[i] - amount * Strength * fall, -1f, 1f);
                }
            }
        }
        return true;
    }

    // Ages the marks and draws the survivors into a cleared wake layer.
    private void RebuildWake(float advance)
    {
        Array.Clear(_wake);
        for (int i = _marks.Count - 1; i >= 0; i--)
        {
            WakeMark m = _marks[i];
            m.Age += advance;
            if (m.Age >= m.Life) { _marks.RemoveAt(i); continue; }
            m.X += m.VX * advance; m.Y += m.VY * advance;
            _marks[i] = m;
            float t = m.Age / m.Life;
            float fade = (1f - t) * Math.Min(1f, t * 8f + 0.35f);
            StampInto(_wake, true, m.X, m.Y, m.Radius, m.Amp * fade);
        }
    }

    private void AddMark(float x, float y, float vx, float vy, float life, float amp, float radius)
    {
        if (_marks.Count >= MaxMarks) return;
        var m = new WakeMark { X = x, Y = y, VX = vx, VY = vy, Life = life, Amp = amp, Radius = radius };
        _marks.Add(m);
        StampInto(_wake, true, x, y, radius, amp * 0.35f);
        Stamps++;
        _calmFor = 0;
    }

    private bool Follow(int x, int y)
    {
        if (OriginX != int.MinValue
            && Math.Abs(x - (OriginX + Cells / 2)) < Recentre
            && Math.Abs(y - (OriginY + Cells / 2)) < Recentre)
        {
            return false;
        }

        int newX = x - Cells / 2, newY = y - Cells / 2;
        if (OriginX == int.MinValue || Math.Abs(newX - OriginX) >= Cells || Math.Abs(newY - OriginY) >= Cells)
        {
            Array.Clear(_now);
            Array.Clear(_before);
        }
        else
        {
            Shift(ref _now, (newX - OriginX) * PerCell, (newY - OriginY) * PerCell);
            Shift(ref _before, (newX - OriginX) * PerCell, (newY - OriginY) * PerCell);
        }

        OriginX = newX;
        OriginY = newY;
        RebuildWake(0f);
        return true;
    }

    private void Shift(ref float[] field, int dx, int dy)
    {
        Array.Clear(_scratch);
        for (int y = 0; y < Size; y++)
        {
            int sy = y + dy;
            if (sy < 0 || sy >= Size)
            {
                continue;
            }
            for (int x = 0; x < Size; x++)
            {
                int sx = x + dx;
                if (sx >= 0 && sx < Size)
                {
                    _scratch[y * Size + x] = field[sy * Size + sx];
                }
            }
        }
        (field, _scratch) = (_scratch, field);
    }

    private void Step()
    {
#if GUO_LABS
        if (GUO.Labs.HarborWater.RustRipples.TryStep(_now, _before, Size, Speed2, Damping))
        {
            (_now, _before) = (_before, _now);
            Steps++;
            ClipDry();
            RebuildWake(StepSeconds);
            return;
        }
#endif
        float damping = Damping;
        float[] now = _now, before = _before;
        for (int y = 1; y < Size - 1; y++)
        {
            int row = y * Size;
            for (int x = 1; x < Size - 1; x++)
            {
                int i = row + x;
                float c = now[i];
                float next = 2f * c - before[i] + Speed2 * (now[i - 1] + now[i + 1] + now[i - Size] + now[i + Size] - 4f * c);
                before[i] = next * damping;
            }
        }
        _before = now;
        _now = before;
        Steps++;
        ClipDry();
        RebuildWake(StepSeconds);
    }

    private void ClipDry()
    {
        if (_wetAt == long.MinValue) return;
        float[] a = _now, b = _before;
        byte[] wet = _wet;
        for (int i = 0; i < wet.Length; i++)
        {
            if (wet[i] == 0) { a[i] = 0f; b[i] = 0f; }
        }
    }

    // Rebuilds the wet-texel map from the mask: when the window moved, and twice
    // a second while disturbances are made (new ground loads in).
    private void RefreshWet(World world, WaterMask mask)
    {
        long now = Time.Ticks;
        if (OriginX == int.MinValue) return;
        if (OriginX == _wetOriginX && OriginY == _wetOriginY && now - _wetAt < 500) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _wetOriginX = OriginX; _wetOriginY = OriginY; _wetAt = now;
        for (int cy = 0; cy < Cells; cy++)
        {
            for (int cx = 0; cx < Cells; cx++)
            {
                // Open water: wet and with no pier or plank over it, so nothing ripples on sand or under a pier.
                byte w = OpenWater(world, mask, OriginX + cx, OriginY + cy) ? (byte)1 : (byte)0;
                int t = cy * PerCell * Size + cx * PerCell;
                _wet[t] = _wet[t + 1] = _wet[t + Size] = _wet[t + Size + 1] = w;
            }
        }
        LastWetMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Upload()
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            _pixels[i] = (byte)Math.Clamp((int)(((_now[i] + _wake[i]) * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
        }

        if (_image == null)
        {
            _image = Image.CreateFromData(Size, Size, false, Image.Format.R8, _pixels);
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(Size, Size, false, Image.Format.R8, _pixels);
            _texture.Update(_image);
        }
    }

    // ---- for WaterProbe --------------------------------------------------

    internal void CentreOnForProbe(int x, int y) => Follow(x, y);

#if TOOLS
    // The editor owns short-lived synthetic previews across assembly reloads.
    internal void DisposeForEditor()
    {
        _texture?.Dispose(); _texture = null;
        _image?.Dispose(); _image = null;
    }
#endif

    internal void StepForProbe(int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            Step();
        }
        Upload();
    }

    /// <summary>The surface height at a cell's centre (its four texels averaged).</summary>
    internal float HeightAt(int cellX, int cellY)
    {
        int tx = (cellX - OriginX) * PerCell, ty = (cellY - OriginY) * PerCell;
        if (tx < 0 || ty < 0 || tx + 1 >= Size || ty + 1 >= Size)
        {
            return 0f;
        }
        int a = ty * Size + tx, b = a + Size;
        return (_now[a] + _now[a + 1] + _now[b] + _now[b + 1] + _wake[a] + _wake[a + 1] + _wake[b] + _wake[b + 1]) * 0.25f;
    }

    // ---- what disturbs the water --------------------------------------

    // Continuous disturbances (a swimmer, a hull under way) are rates per
    // second, scaled by the frame's length, so they push the same at any
    // frame rate; one-off splashes are not.
    private float _dt;

    private void Disturb(World world, WaterMask mask, float deltaSeconds)
    {
        _dt = deltaSeconds;
        RefreshWet(world, mask);
        Player(world, mask);
        Boats(world, mask);
        ItemsAndEffects(world, mask);
    }

    /// <summary>Where an object stands, in cells, with its walking offset (screen pixels) undone.</summary>
    private static (float X, float Y) CellOf(GameObject o)
    {
        // Screen x = (X - Y) * 22, y = (X + Y) * 22, so a pixel offset is
        // (ox + oy) / 44 cells along X and (oy - ox) / 44 along Y.
        return (o.X + (o.Offset.X + o.Offset.Y) / 44f, o.Y + (o.Offset.Y - o.Offset.X) / 44f);
    }

    private void Player(World world, WaterMask mask)
    {
        _swimSeen.Clear();
        foreach (Mobile m in world.Mobiles.Values)
        {
            if (m == null || m.IsDestroyed || Math.Abs(m.X - world.Player.X) > ScanRadius || Math.Abs(m.Y - world.Player.Y) > ScanRadius)
            {
                continue;
            }

            // In the water: on a wet cell and no higher than its surface (a
            // pier or a deck over the water keeps its walker dry).
            if (!mask.WaterAt(m.X, m.Y, out int z) || m.Z > z + 1)
            {
                continue;
            }

            // Only a creature that is moving disturbs the water, with a small
            // ring every so often that the sea soon smooths; one standing still
            // makes nothing (no endless lapping).
            bool moving = m.Steps.Count > 0 || m.Offset.X != 0 || m.Offset.Y != 0;
            if (!moving) continue;
            _swimSeen.Add(m.Serial);
            long now = Time.Ticks;
            if (_swimmers.TryGetValue(m.Serial, out long last) && now - last < 350) continue;
            _swimmers[m.Serial] = now;
            (float x, float y) = CellOf(m);
            if (!OpenWater(world, mask, x, y)) continue;
            Stamp(x, y, 0.5f, 0.30f);
            SwimStamps++;
        }
        _swimDrop.Clear();
        foreach (uint s in _swimmers.Keys) if (!_swimSeen.Contains(s)) _swimDrop.Add(s);
        foreach (uint s in _swimDrop) _swimmers.Remove(s);
    }

    /// <summary>
    /// Water a ripple may be made on: a wet cell with no pier, plank or other
    /// static standing at or above its surface. Only for the few places a
    /// disturbance is made, never per texel.
    /// </summary>
    private static bool OpenWater(World world, WaterMask mask, float fx, float fy)
    {
        int x = (int)MathF.Floor(fx + 0.5f), y = (int)MathF.Floor(fy + 0.5f);
        if (!mask.WaterAt(x, y, out int z)) return false;
        for (GameObject o = world.Map.GetTile(x, y); o != null; o = o.TNext)
        {
            if (o is Static s && !s.ItemData.IsWet && s.Z + 1 >= z && s.ItemData.Height > 0) return false;
        }
        return true;
    }

    private void Boats(World world, WaterMask mask)
    {
        _seen.Clear();
        foreach (Item item in world.Items.Values)
        {
            if (item == null || item.IsDestroyed || !item.IsMulti || item.MultiInfo == null || !item.OnGround
                || Math.Abs(item.X - world.Player.X) > Cells / 2 || Math.Abs(item.Y - world.Player.Y) > Cells / 2)
            {
                continue;
            }

            if (!mask.WaterAt(item.X, item.Y, out _))
            {
                continue; // a house, not a boat
            }

            _seen.Add(item.Serial);
            (float x, float y) = CellOf(item);
#if GUO_LABS
            if (GUO.Labs.HarborWater.HullPose.Enabled && WaterRender.EnvironmentEnabled(GUO.Configuration.ProfileManager.CurrentProfile))
                GUO.Labs.HarborWater.HullPose.Add(world, item, this, x, y);
#endif
            if (!_boats.TryGetValue(item.Serial, out var boat))
            {
                _boats[item.Serial] = new BoatState { X = x, Y = y, HY = -1f };
                continue;
            }

            Rectangle r = item.MultiInfo.Value;
            Wake(boat, r, x, y, (sx, sy) => OpenWater(world, mask, sx, sy));
        }

        _drop.Clear();
        foreach (uint serial in _boats.Keys)
        {
            if (!_seen.Contains(serial))
            {
                _drop.Add(serial);
            }
        }
        foreach (uint serial in _drop)
        {
            _boats.Remove(serial);
        }
#if GUO_LABS
        GUO.Labs.HarborWater.HullPose.Finish();
#endif
    }

    /// <summary>
    /// One frame of a boat's wake. Speed in cells a second is smoothed over
    /// about half a second from how far the hull moved; a hull that stays put
    /// (moored, anchored) reads zero and leaves nothing, and one that stops
    /// stops within a second or so. Heading is kept while it moves. Along it, a
    /// trough is made astern across the hull's beam and a small crest at the
    /// bow; the waves left behind are the wake. open says where water may be disturbed.
    /// </summary>
    internal void Wake(BoatState boat, Rectangle r, float x, float y, Func<float, float, bool> open)
    {
        float dx = x - boat.X, dy = y - boat.Y;
        boat.X = x; boat.Y = y;
        float travel = MathF.Sqrt(dx * dx + dy * dy);
        float inst = travel > 2f ? 0f : travel / _dt;
        if (travel >= 0.01f && travel <= 2f)
        {
            boat.HX += (dx / travel - boat.HX) * 0.5f;
            boat.HY += (dy / travel - boat.HY) * 0.5f;
            float hl = MathF.Sqrt(boat.HX * boat.HX + boat.HY * boat.HY);
            if (hl > 0.001f) { boat.HX /= hl; boat.HY /= hl; }
        }
        boat.Speed += (inst - boat.Speed) * Math.Min(1f, _dt / 0.5f);
        if (boat.Speed < MinWakeSpeed)
        {
            return;
        }

        float gain = Math.Min(1f, boat.Speed / 3f);
        boat.Emit += _dt;
        if (boat.Emit > 4 * EmitEvery) boat.Emit = EmitEvery;
        if (boat.Emit < EmitEvery) return;
        boat.Emit -= EmitEvery;

        float hx = (r.Width - r.X) * 0.5f, hy = (r.Height - r.Y) * 0.5f;
        float cx = x + (r.X + r.Width) * 0.5f, cy = y + (r.Y + r.Height) * 0.5f;
        float ux = boat.HX, uy = boat.HY, px = -uy, py = ux;
        float length = MathF.Abs(ux) * hx + MathF.Abs(uy) * hy;
        float beam = Math.Min(MathF.Abs(px) * hx + MathF.Abs(py) * hy, 2.5f);
        // The arms of a Kelvin wake open behind the stern at a shallow angle; a mark is left
        // where the hull was and drifts sideways, so the arms spread and fade with distance.
        float side = Math.Min(boat.Speed, 4f) * 0.4f;
        float sx = cx - ux * (length + 0.3f), sy = cy - uy * (length + 0.3f);
        if (open(sx, sy))
        {
            AddMark(sx, sy, 0f, 0f, 1.1f, WakeTrough * gain, 0.4f);
            for (int k = -1; k <= 1; k += 2)
            {
                AddMark(sx + px * beam * 0.7f * k, sy + py * beam * 0.7f * k, px * side * k, py * side * k, 1.5f, WakeArm * gain, 0.4f);
            }
        }
        // A small crest at the bow, spreading a little to either side.
        float bx = cx + ux * (length + 0.2f), by = cy + uy * (length + 0.2f);
        if (open(bx, by))
        {
            AddMark(bx, by, 0f, 0f, 0.35f, -WakeBow * gain, 0.4f);
            for (int k = -1; k <= 1; k += 2)
            {
                AddMark(bx + px * beam * 0.5f * k, by + py * beam * 0.5f * k, px * side * 0.6f * k, py * side * 0.6f * k, 0.5f, -WakeBow * 0.7f * gain, 0.35f);
            }
        }
    }

    // Wake mark heights (before the style's strength): trough astern, arms, bow crest.
    internal static float WakeTrough = 0.70f, WakeArm = 0.55f, WakeBow = 0.40f;

    /// <summary>For the probe: the frame length continuous disturbances scale by.</summary>
    internal void SetFrameForProbe(float seconds) => _dt = seconds;

    private void ItemsAndEffects(World world, WaterMask mask)
    {
        // Items: one that moves onto a water cell splashes, and so does one
        // that appears there well inside the view (dropped, thrown, created);
        // one that comes into view at its edge as the player walks does not.
        _seen.Clear();
        foreach (Item item in world.Items.Values)
        {
            if (item == null || item.IsDestroyed || item.IsMulti || !item.OnGround
                || Math.Abs(item.X - world.Player.X) > ScanRadius || Math.Abs(item.Y - world.Player.Y) > ScanRadius)
            {
                continue;
            }

            _seen.Add(item.Serial);
            bool known = _items.TryGetValue(item.Serial, out var was);
            _items[item.Serial] = (item.X, item.Y);
            bool arrived = known
                ? was.X != item.X || was.Y != item.Y
                : !_jumped && Math.Abs(item.X - world.Player.X) < InsideView && Math.Abs(item.Y - world.Player.Y) < InsideView;
            if (arrived && mask.WaterAt(item.X, item.Y, out int z) && item.Z <= z + 1)
            {
                Stamp(item.X, item.Y, 0.6f, 0.6f);
            }
        }
        _drop.Clear();
        foreach (uint serial in _items.Keys)
        {
            if (!_seen.Contains(serial))
            {
                _drop.Add(serial);
            }
        }
        foreach (uint serial in _drop)
        {
            _items.Remove(serial);
        }

        // Effects live on the map's tiles. A fixed one over water (a
        // splash, a fishing bob) disturbs it when it appears; a moving one
        // (a thrown or falling object) when it ends over water.
        _seenEffects.Clear();
        int px = world.Player.X, py = world.Player.Y;
        for (int cx = (px - ScanRadius) >> 3; cx <= (px + ScanRadius) >> 3; cx++)
        {
            for (int cy = (py - ScanRadius) >> 3; cy <= (py + ScanRadius) >> 3; cy++)
            {
                var chunk = world.Map.GetChunk2(cx, cy, false);
                if (chunk == null)
                {
                    continue;
                }
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObject o = chunk.GetHeadObject(x, y); o != null; o = o.TNext)
                        {
                            if (o is GameEffect e && !e.IsDestroyed)
                            {
                                _seenEffects.Add(e);
                                bool known = _effects.ContainsKey(e);
                                _effects[e] = (e.X, e.Y);
                                if (!known && !_jumped && !e.IsMoving && mask.WaterAt(e.X, e.Y, out int z) && e.Z <= z + 4)
                                {
                                    Stamp(e.X, e.Y, 0.8f, 0.5f);
                                }
                            }
                        }
                    }
                }
            }
        }

        _gone.Clear();
        foreach (var pair in _effects)
        {
            if (!_seenEffects.Contains(pair.Key))
            {
                _gone.Add(pair.Key);
                if (pair.Key.IsMoving && mask.WaterAt(pair.Value.X, pair.Value.Y, out _))
                {
                    Stamp(pair.Value.X, pair.Value.Y, 0.7f, 0.6f);
                }
            }
        }
        foreach (GameEffect e in _gone)
        {
            _effects.Remove(e);
        }
    }
}
