// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

// Lead-owned camera code explicitly opts in; default is the unchanged world view.
internal readonly record struct SpeedModeCloudView(bool Enabled, float Zoom, float Height);

/// <summary>Procedural cloud plane for the experimental above-cloud camera.</summary>
public partial class SpeedModeCloudPass : ColorRect
{
    private ShaderMaterial _cloudMaterial;
    internal void Bind(Vector2I size, Vector3 camera, SpeedModeCloudView view, float wind, double seconds)
    {
        if (!view.Enabled || !float.IsFinite(view.Zoom) || view.Zoom <= 4
            || !float.IsFinite(view.Height) || view.Height <= 0 || size.X <= 0 || size.Y <= 0)
        {
            HideForFallback();
            return;
        }
        _cloudMaterial ??= new ShaderMaterial {
            Shader = GD.Load<Shader>("res://src/Render/SpeedMode/Weather/speed_cloud.gdshader") };
        Material = _cloudMaterial;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        Size = size;
        _cloudMaterial.SetShaderParameter("target_size", new Vector2(size.X, size.Y));
        _cloudMaterial.SetShaderParameter("camera_tile", new Vector2(camera.X, camera.Y));
        _cloudMaterial.SetShaderParameter("camera_zoom", view.Zoom);
        _cloudMaterial.SetShaderParameter("camera_height", view.Height);
        _cloudMaterial.SetShaderParameter("wind", float.IsFinite(wind) ? Math.Clamp(wind, -2, 2) : 0);
        _cloudMaterial.SetShaderParameter("seconds", double.IsFinite(seconds) ? (float)(seconds % 3600) : 0);
        Visible = true;
    }
    internal void ProtectPin(Vector2 pixel,bool overview)
    {
        _cloudMaterial?.SetShaderParameter("pin_pixel",pixel);
        _cloudMaterial?.SetShaderParameter("opacity_cap",overview ? 0.32f : 0.82f);
    }
    internal void HideForFallback() => Visible = false;
    public override void _ExitTree()
    {
        Material = null;
        _cloudMaterial?.Dispose();
        _cloudMaterial = null;
    }
}
