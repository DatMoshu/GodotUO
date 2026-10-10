// SPDX-License-Identifier: BSD-2-Clause

using System;
using Godot;
using GUO.Assets;

namespace GUO.Renderer
{
    /// <summary>
    /// A gaussian splat's mipmap chain: lod0 is the full set, each later level a
    /// decimated one (tools/comfy/splatlod.py). Level choice is by projected
    /// pixel radius, so a distant multi draws thousands of gaussians instead of
    /// tens of thousands. CPU only: no Godot rendering types, mirroring the
    /// ADR-0001 sorter rule that selection never touches the device.
    /// </summary>
    public sealed class SplatLodChain
    {
        /// <summary>Pixel radii at which lodN still pays: below the last one the splat culls.</summary>
        public readonly float[] LevelRadii = { 256f, 128f, 64f, 32f };

        public SplatSet[] Levels = Array.Empty<SplatSet>();

        /// <summary>Culling bounds, always from lod0: decimation may drop extremes.</summary>
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;

        public int Count => Levels.Length;

        public static SplatLodChain Load(string[] lodPaths)
        {
            var chain = new SplatLodChain { Levels = new SplatSet[lodPaths.Length] };
            for (int i = 0; i < lodPaths.Length; i++)
            {
                chain.Levels[i] = SplatPlyParser.Parse(lodPaths[i]);
                if (i == 0)
                {
                    chain.BoundsMin = chain.Levels[0].BoundsMin;
                    chain.BoundsMax = chain.Levels[0].BoundsMax;
                }
            }

            return chain;
        }

        /// <summary>
        /// Level for a projected radius in pixels, or -1 when the splat is too
        /// small to draw. A chain shorter than the radii table (in-client
        /// generations ship lod0 only) draws its smallest instead of
        /// culling: with no smaller tier, vanishing would be the only
        /// behaviour at every zoom. Never recomputes depth: the caller passes
        /// the radius it already projected for sorting (one depth function,
        /// ADR-0001).
        /// </summary>
        public int Select(float pixelRadius)
        {
            for (int i = 0; i < Levels.Length && i < LevelRadii.Length; i++)
            {
                if (pixelRadius >= LevelRadii[i])
                {
                    return i;
                }
            }

            if (Levels.Length > LevelRadii.Length)
            {
                return Levels.Length - 1;
            }

            return Levels.Length < LevelRadii.Length ? Levels.Length - 1 : -1;
        }

        /// <summary>Projected radius of a world-size object under the 2D camera's zoom.</summary>
        public static float PixelRadius(float worldSize, float zoom) => worldSize * zoom;
    }
}
