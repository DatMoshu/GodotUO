// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using GUO.Game.GameObjects;
using GUO.Labs.Weather;

namespace GUO.Game.Scenes;
internal partial class GameScene
{
    private void QueueWeatherLab()
    {
        var state = WeatherLab.For(_world);
        WeatherLab.SyncProfile(state.Settings, Configuration.ProfileManager.CurrentProfile);
        if (!state.Settings.Enabled || _world.Player == null || _world.Map == null)
        {
            state.LastTicks = 0;
            Array.Clear(state.Surfaces);
            state.Wetness = state.RainCredit = 0;
            return;
        }
        if (state.Map != _world.MapIndex)
        {
            Array.Clear(state.Surfaces);
            state.Wetness = state.RainCredit = 0;
            state.Map = _world.MapIndex;
            state.LastTicks = 0;
        }
        long now = Time.Ticks;
        float dt = state.LastTicks == 0 ? 0 : Math.Clamp((now - state.LastTicks) / 1000f, 0, 0.25f);
        state.LastTicks = now;
        state.Step(dt, WeatherLab.IsRain(_world));
        bool simulatedWaterRain = GUO.Labs.WaterRain.WaterRainLab.IsActive(_world);
        // Fixed 512-slot spatial cache. Collisions evict old puddles; memory cannot grow.
        // Only known visible land is eligible; never infer water in unloaded cells.
        int candidates = 0;
        foreach (var chunk in _visibleChunks)
        {
            if (chunk == null) continue;
            for (int cx = 0; cx < 8; cx++)
            for (int cy = 0; cy < 8; cy++)
            {
                if (++candidates > 4096) return;
                Land land = null;
                GameObject waterSurface = null;
                bool covered = false;
                int objects = 0;
                GameObject head = chunk.GetHeadObject(cx, cy);
                GameObject o = head;
                for (; o != null && objects < 64; o = o.TNext, objects++)
                {
                    if (o is Land l) land = l;
                    bool wet = o is Land wl && wl.TileData.IsWet || o is Static ws && ws.ItemData.IsWet;
                    if (wet && (waterSurface == null || o.Z > waterSurface.Z)) waterSurface = o;
                }
                // Incomplete cells cannot be proven exposed. A wet static can
                // still be flat water above sloped dry ground.
                if (o != null || land == null || !land.AllowedToDraw ||
                    waterSurface == null && land.IsStretched && !land.TileData.IsWet) continue;
                objects = 0;
                for (o = head; o != null && objects < 64; o = o.TNext, objects++)
                {
                    if (o.Z > (waterSurface?.Z ?? land.Z) && (o is Static s && !s.ItemData.IsWet && (s.ItemData.IsRoof || s.ItemData.IsSurface) || o is Multi))
                    { covered = true; break; }
                }
                if (covered || o != null) continue;
                bool water = waterSurface != null;
                // Preserve density gating and avoid double-stamping styled water.
                bool puddle = !water && state.Settings.Puddles && ((land.X * 73u ^ land.Y * 193u) % 100 < state.Settings.PuddleDensity)
                    && (state.Settings.PuddleTiles.Contains(land.Graphic) ||
                    state.Settings.Cobblestone && (land.TileData.Name?.Contains("cobblestone", StringComparison.OrdinalIgnoreCase) ?? false));
                if ((!water || !state.Settings.WaterDrops) && !puddle) continue;
                GameObject source = water ? waterSurface : land;
                int slot = ((land.X * 73) ^ (land.Y * 193)) & 511;
                var surface = state.Surfaces[slot];
                // A cached object must never move after entering this frame's queue.
                if (surface?.QueuedAt == now) continue;
                if (surface == null || surface.X != source.X || surface.Y != source.Y || surface.Z != source.Z)
                {
                    surface ??= new WeatherSurface(_world);
                    surface.Fill = 0; surface.RingAge = 1;
                    surface.X = source.X; surface.Y = source.Y; surface.Z = source.Z;
                    // Use the shared depth function, directly above this land.
                    surface.PriorityZ = (short)(source.PriorityZ + 1);
                    state.Surfaces[slot] = surface;
                }
                surface.Water = water;
                surface.QueuedAt = now;
                surface.Step(dt, state.Raining, puddle);
                // Styled water already receives these drops through its wave
                // field. Clear old primitive rings when switching styles too.
                if (water && simulatedWaterRain) surface.RingAge = 1;
                if (state.RainCredit >= 1 && state.Impacts < Math.Clamp(state.Settings.DropBudget, 0, 32) &&
                    (water && !simulatedWaterRain || !water && surface.Fill > 0.15f)
                    && state.Random.Next(1200) < (water ? state.Settings.WaterDensity : state.Settings.PuddleDensity))
                {
                    surface.RingAge = 0;
                    surface.DropX = state.Random.Next(-5, 6);
                    surface.DropY = state.Random.Next(-2, 3);
                    state.RainCredit--; state.Impacts++;
                }
                surface.UpdateRealScreenPosition(_offset.X, _offset.Y);
                if (surface.Fill > 0 || surface.RingAge < 1)
                    _renderLists.Add(surface, false);
            }
        }
    }
}
#endif
