// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Renderer.Water;

namespace GUO.Renderer.WaterRain;

internal sealed class RainSurfaceQuery
{
    private readonly World _world;
    internal WaterMask Mask;
    internal readonly Func<int, int, bool> ExposedWater;

    internal RainSurfaceQuery(World world)
    {
        _world = world;
        ExposedWater = Check;
    }

    private bool Check(int x, int y)
    {
        if (_world.Map == null || Mask == null || !Mask.WaterAt(x, y, out int maskZ)) return false;
        // The shader mask intentionally treats unknown cells as wet. Never use
        // that alone as evidence of a surface: inspect loaded tile objects too.
        var surface = new RainSurface();
        int objects = 0;
        for (GameObject o = _world.Map.GetTile(x, y, false); o != null; o = o.TNext)
        {
            // Fail closed on pathological decoration stacks. With eight impact
            // attempts, at most 1024 objects are inspected in one frame.
            if (++objects > 128) return false;
            if (o.IsDestroyed) continue;
            bool wet = o is Land land && land.TileData.IsWet || o is Static s && s.ItemData.IsWet;
            bool cover = o is Land || o is Multi
                || o is Static dry && (dry.ItemData.IsRoof || dry.ItemData.IsSurface);
            surface.Add(wet, cover, o.Z);
        }
        return surface.Exposed && surface.WaterZ == maskZ;
    }
}
