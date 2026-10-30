// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>World-target wet-floor overlay; instantiated only by a valid speed-mode frame.</summary>
public partial class SpeedModeWetPass : ColorRect
{
    private ShaderMaterial _wetMaterial;

    internal void Bind(Texture2D surface, Texture2D lights, Vector2I size, Vector2I surfaceOrigin,
        float wetness, float rainIntensity, double seconds, float artScale = 1f, bool reflections = false)
    {
        if (surface == null || lights == null || size.X <= 0 || size.Y <= 0
            || surface.GetWidth() != size.X || surface.GetHeight() != size.Y
            || lights.GetWidth() != size.X || lights.GetHeight() != size.Y
            || !float.IsFinite(wetness) || (wetness <= 0 && !reflections))
        {
            HideForFallback();
            return;
        }
        if (_wetMaterial == null)
        {
            _wetMaterial = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://src/Render/SpeedMode/Weather/speed_wet.gdshader")
            };
            Material = _wetMaterial;
            MouseFilter = MouseFilterEnum.Ignore;
            TextureFilter = TextureFilterEnum.Nearest;
        }
        Size = size;
        _wetMaterial.SetShaderParameter("surface_buffer", surface);
        _wetMaterial.SetShaderParameter("light_target", lights);
        _wetMaterial.SetShaderParameter("target_size", new Vector2(size.X, size.Y));
        _wetMaterial.SetShaderParameter("surface_origin", new Vector2(surfaceOrigin.X, surfaceOrigin.Y));
        _wetMaterial.SetShaderParameter("wetness", Math.Clamp(wetness, 0, 1));
        _wetMaterial.SetShaderParameter("rain_intensity", float.IsFinite(rainIntensity) ? Math.Clamp(rainIntensity, 0, 1) : 0);
        _wetMaterial.SetShaderParameter("seconds", double.IsFinite(seconds) ? (float)(seconds % 3600) : 0);
        // Approved plane-consistency reflection; no object base-Z dependency.
        _wetMaterial.SetShaderParameter("reflections_enabled", reflections);
        _wetMaterial.SetShaderParameter("reflection_art_scale", float.IsFinite(artScale) ? Math.Clamp(artScale, 0.125f, 8f) : 1f);
        Visible = true;
    }

    internal void HideForFallback()
    {
        Visible = false;
        _wetMaterial?.SetShaderParameter("surface_buffer", default(Variant));
        _wetMaterial?.SetShaderParameter("light_target", default(Variant));
    }

    public override void _ExitTree()
    {
        Material = null;
        _wetMaterial?.Dispose();
        _wetMaterial = null;
    }
}
