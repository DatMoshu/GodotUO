// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;

/// <summary>Temporary native actors in the editor world; no packets, project objects or files.</summary>
internal sealed class WaterWorldTest
{
    private WorldHost _host;
    private World _world;
    private Item _boat;
    private Mobile _wader;
    private int _map, _x, _y, _offset, _direction;
    private sbyte _z, _cameraZ;
    private (int Map, int X, int Y, sbyte Z) _home;
    private float _elapsed;
    private uint _lastTick;
    internal bool Active => _boat != null || _wader != null;
    internal string Kind => _boat != null ? "boat" : _wader != null ? "wader" : "none";
    internal uint Serial => _boat?.Serial ?? _wader?.Serial ?? 0;
    internal int Moves { get; private set; }
    internal int TravelledCells { get; private set; }
    internal int ActorY => _boat?.Y ?? _wader?.Y ?? 0;

    internal string Start(WorldHost host, bool boat)
    {
        Stop();
        if (host?.IsBooted != true || host.World?.Player == null)
            return "Open the World view before starting a native water test.";
        World world = host.World;
        if (!ReferenceEquals(world, Client.Game?.UO?.World))
            return "The World view is busy. Return to World and try again.";
        int minX = 0, maxX = 0, minY = 0, maxY = 0;
        if (boat)
        {
            var parts = Client.Game.UO.FileManager.Multis.GetMultis(0);
            if (parts.Count == 0) return "Small boat artwork is unavailable in this client install. Check the editor's client data setting.";
            minX = Math.Min(0, parts.Min(p => (int)p.X)); maxX = Math.Max(0, parts.Max(p => (int)p.X));
            minY = Math.Min(0, parts.Min(p => (int)p.Y)); maxY = Math.Max(0, parts.Max(p => (int)p.Y));
            if (maxX - minX > 32 || maxY - minY > 32)
                return "This client install's boat footprint is too large for the bounded preview. Use a standard small boat multi.";
        }
        if (!FindRoute(world, host.X, host.Y, minX, maxX, minY - 3, maxY + 3, out int x, out int y, out sbyte z))
            return "No clear water route nearby. Move the World view over open water and try again.";
        uint serial = boat ? 0x4000_F100u : 0x3FFF_F100u;
        while (world.Get(serial) != null && (serial & 0xFF) != 0xFF) serial++;
        if (world.Get(serial) != null) return "The temporary actor slots are occupied. Close and reopen the World view, then try again.";
        _host = host; _world = world; _map = world.MapIndex;
        _home = (_map, host.X, host.Y, (sbyte)host.Z);
        _x = x; _y = y; _z = z; _offset = -3; _direction = 1;
        _elapsed = 0; _lastTick = GUO.Time.Ticks; Moves = TravelledCells = 0;
        try
        {
            if (boat)
                _boat = host.PlaceServerMulti(serial, 0, (ushort)x, (ushort)(y + _offset), z);
            else
            {
                _wader = world.GetOrCreateMobile(serial);
                _wader.Graphic = 0x0190;
                _wader.FixHue(0x03EA);
                _wader.Name = "Water test actor";
                // Embedded World has no game audio service. Silence only this
                // temporary actor through its existing footstep timer.
                _wader.LastStepSoundTime = long.MaxValue;
                _wader.Direction = Direction.South;
                _wader.CheckGraphicChange();
                _wader.SetInWorldTile((ushort)x, (ushort)(y + _offset), z);
            }
            host.GoTo(_map, x, y);
            // The invisible camera stand-in must not add its own standing-water stamps.
            _cameraZ = (sbyte)Math.Clamp(z + 6, sbyte.MinValue, sbyte.MaxValue);
            world.Player.Z = _cameraZ;
        }
        catch { Stop(); throw; }
        return $"Native {(boat ? "boat" : "wader")} test at {x}, {y}. Scripted path; no gameplay collision or server test. Stop test removes the actor.";
    }

    private static bool FindRoute(World world, int originX, int originY, int minX, int maxX, int minY, int maxY,
        out int x, out int y, out sbyte z)
    {
        var cells = new Dictionary<(int, int), sbyte?>();
        sbyte? Cell(int cx, int cy)
        {
            if (!cells.TryGetValue((cx, cy), out sbyte? value))
                cells[(cx, cy)] = value = WetCell(world, cx, cy);
            return value;
        }
        for (int radius = 0; radius <= 32; radius++)
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    int cx = originX + dx, cy = originY + dy;
                    sbyte? height = Cell(cx, cy);
                    if (height == null) continue;
                    bool clear = true;
                    for (int ox = minX; ox <= maxX && clear; ox++)
                        for (int oy = minY; oy <= maxY; oy++)
                            if (Cell(cx + ox, cy + oy) != height) { clear = false; break; }
                    if (!clear) continue;
                    x = cx; y = cy; z = height.Value; return true;
                }
        x = y = 0; z = 0; return false;
    }

    private static sbyte? WetCell(World world, int x, int y)
    {
        var sizes = Client.Game.UO.FileManager.Maps.MapsDefaultSize;
        if (x < 0 || y < 0 || x >= sizes[world.MapIndex, 0] || y >= sizes[world.MapIndex, 1]) return null;
        GameObject head = world.Map.GetTile(x, y);
        int height = int.MinValue;
        for (GameObject o = head; o != null; o = o.TNext)
        {
            if (o is Land land && land.TileData.IsWet || o is Static tile && tile.ItemData.IsWet)
                height = Math.Max(height, o.Z);
        }
        if (height == int.MinValue) return null;
        for (GameObject o = head; o != null; o = o.TNext)
        {
            if (o is Land land && !land.TileData.IsWet && land.Z >= height
                || o is Static tile && !tile.ItemData.IsWet && tile.Z + tile.ItemData.Height >= height
                || o is Item or Multi || o is Mobile && !ReferenceEquals(o, world.Player)) return null;
        }
        return (sbyte)height;
    }

    internal void Advance(float delta, bool paused)
    {
        if (!Active) return;
        Entity actor = (Entity)_boat ?? _wader;
        if (_host.World != _world || _world.MapIndex != _map || actor.IsDestroyed
            || !ReferenceEquals(_world.Get(actor.Serial), actor)
            || Math.Abs(_host.X - _x) > 32 || Math.Abs(_host.Y - _y) > 32)
        { Stop(); return; }
        uint now = GUO.Time.Ticks;
        if (paused)
        {
            if (_wader != null) _wader.LastStepTime += now - _lastTick;
            _lastTick = now; return;
        }
        _lastTick = now;
        int beforeY = ActorY;
        _wader?.Update();
        TravelledCells += Math.Abs(ActorY - beforeY);
        _elapsed += Math.Clamp(delta, 0, .25f);
        if (_elapsed < .4f || _wader?.Steps.Count > 0) return;
        _elapsed = 0;
        if (_offset == 3) _direction = -1;
        else if (_offset == -3) _direction = 1;
        _offset += _direction;
        if (_boat != null)
        {
            _host.PlaceServerMulti(_boat.Serial, 0, (ushort)_x, (ushort)(_y + _offset), _z);
            TravelledCells++;
        }
        else _wader.EnqueueStep(_x, _y + _offset, _z, _direction > 0 ? Direction.South : Direction.North, false);
        Moves++;
    }

    internal void Stop()
    {
        if (_world != null)
        {
            if (_boat != null && ReferenceEquals(_world.Get(_boat.Serial), _boat))
            {
                _world.HouseManager.Remove(_boat.Serial);
                _world.RemoveItem(_boat.Serial, forceRemove: true);
            }
            if (_wader != null && ReferenceEquals(_world.Get(_wader.Serial), _wader))
                _world.RemoveMobile(_wader.Serial, forceRemove: true);
            if (_host.World == _world && _world.MapIndex == _map && _host.X == _x && _host.Y == _y && _host.Z == _cameraZ)
            {
                _host.GoTo(_home.Map, _home.X, _home.Y);
                _world.Player.Z = _home.Z;
            }
        }
        _boat = null; _wader = null; _world = null; _host = null;
    }
}
#endif
