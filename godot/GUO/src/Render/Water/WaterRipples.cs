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

    private readonly Dictionary<uint, (float X, float Y)> _boats = new Dictionary<uint, (float, float)>();
    private readonly Dictionary<uint, (int X, int Y)> _items = new Dictionary<uint, (int, int)>();
    private readonly Dictionary<GameEffect, (int X, int Y)> _effects = new Dictionary<GameEffect, (int, int)>();
    private readonly HashSet<GameEffect> _seenEffects = new HashSet<GameEffect>();
    private readonly List<GameEffect> _gone = new List<GameEffect>();
    private readonly HashSet<uint> _seen = new HashSet<uint>();
    private readonly List<uint> _drop = new List<uint>();
    private int _lastPlayerX = int.MinValue, _lastPlayerY;
    private bool _jumped;
#if GUO_LABS
    // PORT DEVIATION (GUO): owner-requested default-off UO+ rain experiment.
    private readonly GUO.Labs.WaterRain.RainRippleEmitter _rain = new();
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
    public double LastUpdateMs { get; private set; }

    /// <summary>Forget everything: a new map, a style switched off and on.</summary>
    public void Reset()
    {
#if GUO_LABS
        _rain.Reset();
#endif
        Array.Clear(_now);
        Array.Clear(_before);
        _boats.Clear();
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

#if GUO_LABS
        GUO.Labs.WaterRain.WaterRainLab.Update(world, mask, this, _rain, deltaSeconds, _jumped);
#endif
        Disturb(world, mask, Math.Max(deltaSeconds, 1f / 240f));

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
        float cx = (cellX - OriginX + 0.5f) * PerCell;
        float cy = (cellY - OriginY + 0.5f) * PerCell;
        float r = Math.Max(0.75f, radius * PerCell);
        int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r);
        int y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
        if (x1 < 1 || y1 < 1 || x0 >= Size - 1 || y0 >= Size - 1)
        {
            return;
        }

        for (int y = Math.Max(1, y0); y <= Math.Min(Size - 2, y1); y++)
        {
            for (int x = Math.Max(1, x0); x <= Math.Min(Size - 2, x1); x++)
            {
                float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) / r;
                if (d < 1f)
                {
                    float fall = 0.5f + 0.5f * MathF.Cos(d * MathF.PI);
                    int i = y * Size + x;
                    _now[i] = Math.Clamp(_now[i] - amount * Strength * fall, -1f, 1f);
                }
            }
        }

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
    }

    private void Upload()
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            _pixels[i] = (byte)Math.Clamp((int)((_now[i] * 0.5f + 0.5f) * 255f + 0.5f), 0, 255);
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
        return (_now[ty * Size + tx] + _now[ty * Size + tx + 1] + _now[(ty + 1) * Size + tx] + _now[(ty + 1) * Size + tx + 1]) * 0.25f;
    }

    // ---- what disturbs the water --------------------------------------

    // Continuous disturbances (a swimmer, a hull under way) are rates per
    // second, scaled by the frame's length, so they push the same at any
    // frame rate; one-off splashes are not.
    private float _dt;

    private void Disturb(World world, WaterMask mask, float deltaSeconds)
    {
        _dt = deltaSeconds;
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

            (float x, float y) = CellOf(m);
            bool moving = m.Steps.Count > 0 || m.Offset.X != 0 || m.Offset.Y != 0;
            // A moving swimmer pushes harder; one standing still laps gently.
            float lap = 0.5f + 0.5f * MathF.Sin((float)(Time.Ticks % 100_000) / 400f + m.Serial);
            Stamp(x, y, 0.7f, (moving ? 10f : 1.5f * lap) * _dt);
        }
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
            if (!_boats.TryGetValue(item.Serial, out var was))
            {
                _boats[item.Serial] = (x, y);
                continue;
            }

            float dx = x - was.X, dy = y - was.Y;
            _boats[item.Serial] = (x, y);
            if (dx * dx + dy * dy < 1e-4f)
            {
                continue;
            }

            // The hull's outline, pushed a little every frame it moves: the
            // waves it leaves behind become the wake, the ones ahead the bow wave.
            Rectangle r = item.MultiInfo.Value;
            // Cells a second; a boat under way makes two to four.
            float speed = Math.Min(1f, MathF.Sqrt(dx * dx + dy * dy) / _dt / 3f);
            float push = 3f * speed * _dt;
            for (int ox = r.X; ox <= r.Width; ox++)
            {
                Stamp(x + ox, y + r.Y - 0.5f, 0.5f, push);
                Stamp(x + ox, y + r.Height + 0.5f, 0.5f, push);
            }
            for (int oy = r.Y; oy <= r.Height; oy++)
            {
                Stamp(x + r.X - 0.5f, y + oy, 0.5f, push);
                Stamp(x + r.Width + 0.5f, y + oy, 0.5f, push);
            }
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
    }

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
