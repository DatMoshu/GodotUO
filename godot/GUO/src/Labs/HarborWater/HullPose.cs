// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Renderer.Water;

namespace GUO.Labs.HarborWater;

internal static class HullPose
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("GUO_HARBOR_POSE") == "1";
    private static Dictionary<GameObject, int> previous = new();
    private static Dictionary<GameObject, int> current = new();

    internal static void Finish()
    {
        foreach (var obj in previous.Keys)
            if (!current.ContainsKey(obj)) obj.SetHarborPixels(0);
        (previous, current) = (current, previous);
        current.Clear();
    }

    internal static void Add(World world, Item hull, WaterRipples ripples, float x, float y)
    {
        // Classic small, medium and large hulls (four facings per type).
        if (hull.Graphic > 0x17 || !world.HouseManager.TryGetHouse(hull.Serial, out var house)) return;
        float time = (Time.Ticks % 3_600_000L) / 1000f;
        float sum = 0f;
        var r = hull.MultiInfo.Value;
        for (int i = 0; i < 4; i++)
        {
            float u = x + ((i & 1) == 0 ? r.X : r.Width);
            float v = y + ((i & 2) == 0 ? r.Y : r.Height);
            float h = 1.7f * MathF.Sin(u * 0.26f - v * 0.17f + time * 1.26f)
                + 0.9f * MathF.Sin(u * 0.13f + v * 0.32f - time * 0.87f);
            sum += h + 1.1f * ripples.HeightAt((int)MathF.Round(u), (int)MathF.Round(v));
        }
        int pixels = -(int)MathF.Round(sum * 0.25f);
        Mark(hull);
        // The root tile may be the invisible first multi entry. It still
        // carries passengers and has no drawn component to visit below.
        MarkDeckCell(hull.X, hull.Y);
        foreach (var part in house.Components)
        {
            Mark(part);
            // ModernUO treats any occupied multi cell as part of the hull.
            // Classic deck components do not all carry the Surface flag.
            MarkDeckCell(part.X, part.Y);
        }

        void MarkDeckCell(int tileX, int tileY)
        {
            if (world.Player.X == tileX && world.Player.Y == tileY && OnDeck(world.Player))
                Mark(world.Player);
            for (var obj = world.Map.GetTile(tileX, tileY, false); obj != null; obj = obj.TNext)
                if ((obj is Mobile || obj is Item { OnGround: true }) && OnDeck(obj))
                    Mark(obj);
        }

        bool OnDeck(GameObject obj) => obj.Z >= hull.Z && obj.Z <= hull.Z + 6;

        void Mark(GameObject obj)
        {
            current[obj] = pixels;
            obj.SetHarborPixels(pixels);
        }
    }
}
