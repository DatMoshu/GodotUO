// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Configuration;

namespace GUO.Renderer;

internal static class HdSampling
{
    public static SamplerState Select(double scale, double zoom, HdFilterMode mode) => mode switch {
        HdFilterMode.Point => SamplerState.PointClamp,
        HdFilterMode.PointMipmaps => SamplerState.PointMipmapsClamp,
        HdFilterMode.Smooth => SamplerState.SmoothMipmapsClamp,
        _ => Math.Abs(scale - zoom) < 0.0001 ? SamplerState.PointClamp : SamplerState.PointMipmapsClamp
    };
}
