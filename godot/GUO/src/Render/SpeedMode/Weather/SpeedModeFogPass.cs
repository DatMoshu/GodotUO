// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>World-target mist; the caller supplies current-frame surface/camera data.</summary>
public partial class SpeedModeFogPass : ColorRect
{
    private ShaderMaterial _fogMaterial;

    internal void Bind(Texture2D surface, Vector2I size, Vector2I surfaceOrigin,
        Vector3 cameraWorld, float weatherDensity, int morningHaze, float wind,
        double seconds, bool enabled)
    {
        float density = CombinedDensity(weatherDensity, morningHaze);
        if (!enabled || surface == null || size.X <= 0 || size.Y <= 0 || density <= 0
            || surface.GetWidth() != size.X || surface.GetHeight() != size.Y
            || !float.IsFinite(cameraWorld.X) || !float.IsFinite(cameraWorld.Y)
            || !float.IsFinite(cameraWorld.Z))
        {
            HideForFallback();
            return;
        }
        if (_fogMaterial == null)
        {
            _fogMaterial = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://src/Render/SpeedMode/Weather/speed_fog.gdshader")
            };
            Material = _fogMaterial;
            MouseFilter = MouseFilterEnum.Ignore;
            TextureFilter = TextureFilterEnum.Nearest;
        }
        Size = size;
        _fogMaterial.SetShaderParameter("surface_buffer", surface);
        _fogMaterial.SetShaderParameter("surface_origin", new Vector2(surfaceOrigin.X, surfaceOrigin.Y));
        _fogMaterial.SetShaderParameter("camera_world", cameraWorld);
        _fogMaterial.SetShaderParameter("fog_density", density);
        _fogMaterial.SetShaderParameter("wind", float.IsFinite(wind) ? Math.Clamp(wind, -2, 2) : 0);
        _fogMaterial.SetShaderParameter("seconds", double.IsFinite(seconds) ? (float)(seconds % 3600) : 0);
        Visible = true;
    }

    internal static float CombinedDensity(float weatherDensity, int morningHaze)
        => Math.Clamp((float.IsFinite(weatherDensity) ? Math.Clamp(weatherDensity, 0, 1) : 0)
            + Math.Clamp(morningHaze, 0, 100) / 100f, 0, 1);

    internal void HideForFallback()
    {
        Visible = false;
        _fogMaterial?.SetShaderParameter("surface_buffer", default(Variant));
    }

    public override void _ExitTree()
    {
        Material = null;
        _fogMaterial?.Dispose();
        _fogMaterial = null;
    }
}
