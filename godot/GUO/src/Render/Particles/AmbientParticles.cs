// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned UO+ non-parity option (ADR-0042). Single game/render thread only.
using System;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer.Particles;

internal enum AmbientParticleKind { None, Fog, Leaves, Dust }

/// <summary>Stateless, tile-anchored ambient motion. No world entities or random global state.</summary>
internal sealed class AmbientParticles : IDisposable
{
    internal const int MaximumParticles = 512;
    internal const int MaximumSamples = 4096;
    internal const int MaximumTileObjects = 64;
    private ImageTexture _fog;
    private Profile _profile;
    private double _seconds;
    private int _limit;
    internal int Samples { get; private set; }
    internal int Emitted { get; private set; }
    internal bool Full => Emitted >= _limit || Samples >= MaximumSamples;

    internal readonly record struct Particle(AmbientParticleKind Kind, float X, float Y, float Alpha);

    internal static bool Enabled(Profile p) => p != null
        && (p.AmbientFog || p.AmbientLeaves || p.AmbientDust)
        && p.AmbientParticleDensity > 0 && p.AmbientParticleCap > 0;

    internal void Begin(Profile profile, double seconds)
    {
        _profile = profile;
        _seconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        _limit = Enabled(profile) ? Math.Clamp(profile.AmbientParticleCap, 0, MaximumParticles) : 0;
        Samples = Emitted = 0;
    }

    // Native interior land IDs from MapGen's tile-tables.default.json. Unknown
    // and custom ground stays quiet. Foliage also supports trees on grass.
    internal static AmbientParticleKind Classify(ushort land, bool foliage, bool covered, bool wet)
    {
        if (covered || wet) return AmbientParticleKind.None;
        if (land is >= 0x3DEB and <= 0x3DEF) return AmbientParticleKind.Fog;
        if (foliage || land is >= 0xC4 and <= 0xC7 or >= 0xAC and <= 0xAF) return AmbientParticleKind.Leaves;
        if (land is >= 0x16 and <= 0x19) return AmbientParticleKind.Dust;
        return AmbientParticleKind.None;
    }

    internal bool TrySample(int x, int y, int map, AmbientParticleKind kind, out Particle particle)
    {
        particle = default;
        if (Full) return false;
        Samples++;
        bool enabled = kind switch
        {
            AmbientParticleKind.Fog => _profile.AmbientFog,
            AmbientParticleKind.Leaves => _profile.AmbientLeaves,
            AmbientParticleKind.Dust => _profile.AmbientDust,
            _ => false
        };
        if (!enabled) return false;
        uint hash = Hash(x, y, map);
        // At 100%, one in eight eligible tiles emits. Lower densities are a
        // stable subset, so changing density never relocates existing emitters.
        if (hash % 800 >= Math.Clamp(_profile.AmbientParticleDensity, 0, 100)) return false;
        double period = kind == AmbientParticleKind.Fog ? 18 : kind == AmbientParticleKind.Leaves ? 7 : 5;
        float phase = (float)((_seconds / period + ((hash >> 10) & 1023) / 1024.0) % 1);
        float wave = MathF.Sin(phase * MathF.Tau);
        float alpha = MathF.Sin(phase * MathF.PI); // zero at wrap: no visible teleport
        particle = new Particle(kind, (phase - 0.5f) * 28,
            kind == AmbientParticleKind.Leaves ? phase * 44 - 52 : wave * 3 - 5,
            alpha * (kind == AmbientParticleKind.Fog ? 0.18f : 0.65f));
        Emitted++;
        return true;
    }

    private static uint Hash(int x, int y, int map)
    {
        uint h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + (uint)map * 2246822519u);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    internal void Draw(UltimaBatcher2D batcher, in Particle p, int tileX, int tileY, float depth)
    {
        int x = tileX + (int)p.X, y = tileY + (int)p.Y;
        Texture2D texture;
        GUO.Compat.Rectangle rect;
        if (p.Kind == AmbientParticleKind.Fog)
        {
            _fog ??= CreateFog();
            texture = _fog;
            rect = new(x - 48, y - 12, 96, 24);
        }
        else
        {
            texture = SolidColorTextureCache.GetTexture(p.Kind == AmbientParticleKind.Leaves
                ? new GUO.Compat.Color(146, 129, 57) : new GUO.Compat.Color(193, 168, 112));
            rect = new(x, y, p.Kind == AmbientParticleKind.Leaves ? 3 : 2, 2);
        }
        batcher.Draw(texture, rect, new Vector3(0, 0, p.Alpha), depth);
    }

    private static ImageTexture CreateFog()
    {
        // Small code-generated nearest-sampled mist sprite, no client art or
        // filtering changes. Feathered alpha avoids hard rectangular fog banks.
        byte[] rgba = new byte[64 * 24 * 4];
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 64; x++)
        {
            float dx = (x - 31.5f) / 32, dy = (y - 11.5f) / 12;
            float a = Math.Max(0, 1 - dx * dx - dy * dy);
            int i = (y * 64 + x) * 4;
            // The native batcher uses premultiplied alpha (ADR-0003).
            // Supply premultiplied RGB so the feather remains transparent.
            rgba[i] = (byte)(183 * a * a);
            rgba[i + 1] = (byte)(195 * a * a);
            rgba[i + 2] = (byte)(193 * a * a);
            rgba[i + 3] = (byte)(255 * a * a);
        }
        using Image image = Image.CreateFromData(64, 24, false, Image.Format.Rgba8, rgba);
        return ImageTexture.CreateFromImage(image);
    }

    public void Dispose() { _fog?.Dispose(); _fog = null; }
}
