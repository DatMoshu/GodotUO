// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Renderer.Water;

namespace GUO.Renderer.WaterRain;

/// <summary>Bounded rain impacts in world cells; the existing water field owns the waves.</summary>
internal sealed class RainRippleEmitter
{
    internal const int MaxPerFrame = 8;
    internal const int Radius = 18;
    internal const double ImpactsPerSecond = 40;
    private double _credit;
    private uint _random = 29171;
    internal int LastAttempts { get; private set; }
    internal int LastStamps { get; private set; }
    internal int TotalStamps { get; private set; }

    internal void Reset()
    {
        _credit = 0;
        LastAttempts = LastStamps = 0;
    }

    internal void Update(WaterRipples field, float delta, float intensity, bool enabled,
        int budget, int centreX, int centreY, Func<int, int, bool> exposedWater)
    {
        LastAttempts = LastStamps = 0;
        if (!enabled || !float.IsFinite(intensity) || intensity <= 0 || budget <= 0
            || !float.IsFinite(field.Strength) || field.Strength <= 0 || field.OriginX == int.MinValue)
        {
            Reset();
            return;
        }
        if (!float.IsFinite(delta) || delta <= 0) return;
        int limit = Math.Clamp(budget, 0, MaxPerFrame);
        _credit = Math.Min(limit, _credit + Math.Min(delta, 0.25f)
            * Math.Min(intensity, 1f) * ImpactsPerSecond);
        int attempts = Math.Min(limit, (int)(_credit + 1e-6));
        _credit = Math.Max(0, _credit - attempts);
        // Consume attempts on dry/covered/unloaded cells too: walking onto the
        // sea must not release a backlog of rain collected over land.
        for (int i = 0; i < attempts; i++)
        {
            LastAttempts++;
            int x = centreX + Next(Radius * 2 + 1) - Radius;
            int y = centreY + Next(Radius * 2 + 1) - Radius;
            if (!exposedWater(x, y)) continue;
            float jitterX = Next(801) / 1000f - 0.4f;
            float jitterY = Next(801) / 1000f - 0.4f;
            field.Stamp(x + jitterX, y + jitterY, 0.4f, 0.35f);
            LastStamps++;
            TotalStamps++;
        }
    }

    private int Next(int count)
    {
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return (int)(_random % (uint)count);
    }
}

/// <summary>Height policy shared by loaded-tile validation and synthetic probes.</summary>
internal struct RainSurface
{
    private int? _waterZ, _coverZ;
    internal int? WaterZ => _waterZ;
    internal void Add(bool wet, bool cover, int z)
    {
        if (wet) _waterZ = Math.Max(_waterZ ?? int.MinValue, z);
        else if (cover) _coverZ = Math.Max(_coverZ ?? int.MinValue, z);
    }
    internal bool Exposed => _waterZ.HasValue && (!_coverZ.HasValue || _coverZ <= _waterZ);
}
