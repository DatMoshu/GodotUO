// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x hue-safety (the gray-base invariant).
//
// Native hueable art is exact gray (r == g == b): uo_hue_core.gdshaderinc
// hues a sprite by treating the source texel's red channel as a brightness
// index into the hues.mul palette row, and PARTIAL_HUED only hues texels
// with r == g == b exactly (colored trim stays). The x4v3 model
// hallucinates a few/255 of chroma into flat gray, so an upscaled
// hueable base stops being gray: PARTIAL_HUED skips almost every texel
// (gray-white with speckles where a texel happens to survive) and HUED
// reads a shifted red index. The draw path is already hue-in-shader over
// the SR base (as HD): the resolvers pass the same hueVec they would for
// native, and the disk cache stays keyed by graphic only -- one unhued
// hue-0 base per graphic, never a per-hue bake. So the fix is data-side:
//
//   * the worker restores exact gray over native-gray blocks after upscale
//     (RestoreGrayBlocks), so new PNGs satisfy the shader's exact check;
//   * the resolvers reject a PNG that carries chroma where native is gray
//     (IsSrGraySafe): delete it, requeue the hue-0 base copy, draw native
//     this frame. Old/bad cache heals through the lazy worker.
//
// No shader change: the native path (and SR-off) stays byte-identical.

using System;

namespace GUO.Renderer.SuperRes
{
    internal static class SrGray
    {
        /// <summary>Max surviving channel spread (/255) for an SR texel that
        /// maps to opaque native gray. New worker output is exactly 0;
        /// bilinear fallback is exactly 0; model-chroma cache reads 2+.</summary>
        public const int SpreadTolerance = 4;

        /// <summary>True for an opaque exactly-gray native texel (0xAABBGGRR).</summary>
        public static bool IsOpaqueGray(uint p)
        {
            if ((p >> 24) == 0)
            {
                return false;
            }

            byte r = (byte)p;
            return r == (byte)(p >> 8) && r == (byte)(p >> 16);
        }

        /// <summary>Forces exact gray over the SR blocks whose native texel
        /// is opaque gray (desaturate to the channel average). Colored trim
        /// (native not gray) and transparent texels are untouched. Runs on
        /// the worker after upscale, before the alpha interleave.</summary>
        public static void RestoreGrayBlocks(byte[] srRgb, uint[] native, int natW, int natH, int srW, int srH)
        {
            if (srRgb == null || native == null)
            {
                return;
            }

            if (natW <= 0 || natH <= 0 || srW <= 0 || srH <= 0)
            {
                return;
            }

            if (native.Length < natW * natH || srRgb.Length < srW * srH * 3)
            {
                return;
            }

            for (int y = 0; y < srH; y++)
            {
                int sy = (y * natH) / srH;
                int nrow = sy * natW;
                int srow = y * srW;
                for (int x = 0; x < srW; x++)
                {
                    uint p = native[nrow + ((x * natW) / srW)];
                    if (!IsOpaqueGray(p))
                    {
                        continue;
                    }

                    int si = (srow + x) * 3;
                    int gray = (srRgb[si] + srRgb[si + 1] + srRgb[si + 2]) / 3;
                    byte g = (byte)gray;
                    srRgb[si] = g;
                    srRgb[si + 1] = g;
                    srRgb[si + 2] = g;
                }
            }
        }

        /// <summary>True when every sampled opaque SR texel that maps to
        /// opaque native gray is itself (near-)gray. Transparent texels and
        /// natively-colored trim are skipped. Pure function over bytes: the
        /// resolvers call it before GenerateMipmaps (which would append mip
        /// levels to GetData), the probe calls it on the file bytes.</summary>
        public static bool IsSrGraySafe(byte[] srRgba, int srW, int srH, uint[] native, int natW, int natH, int spreadTol = SpreadTolerance)
        {
            if (srRgba == null || native == null)
            {
                return false;
            }

            if (natW <= 0 || natH <= 0 || srW <= 0 || srH <= 0)
            {
                return false;
            }

            if (native.Length < natW * natH || srRgba.Length < srW * srH * 4)
            {
                return false;
            }

            // Large SR images sample every 2nd texel; the check stays ~ms
            // and chroma damage is never a single texel.
            int step = (srW * srH > 1024 * 1024) ? 2 : 1;
            for (int y = 0; y < srH; y += step)
            {
                int sy = (y * natH) / srH;
                int nrow = sy * natW;
                int srow = y * srW;
                for (int x = 0; x < srW; x += step)
                {
                    uint p = native[nrow + ((x * natW) / srW)];
                    if (!IsOpaqueGray(p))
                    {
                        continue;
                    }

                    int si = (srow + x) * 4;
                    if (srRgba[si + 3] == 0)
                    {
                        continue;
                    }

                    int r = srRgba[si];
                    int g = srRgba[si + 1];
                    int b = srRgba[si + 2];
                    int mx = Math.Max(r, Math.Max(g, b));
                    int mn = Math.Min(r, Math.Min(g, b));
                    if (mx - mn > spreadTol)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
