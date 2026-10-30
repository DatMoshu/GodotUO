// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): batched rain with per-pixel cover, ADR-0048.
using System;
using System.Runtime.CompilerServices;
using Godot;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>
/// One world-target overlay. The core owns placement before UI and calls
/// Bind only for a valid speed-mode frame; HideForFallback preserves classic rain.
/// </summary>
public partial class SpeedModeRainPass : ColorRect
{
    private sealed class Frame
    {
        internal uint Tick;
        internal WeakReference<SpeedModeRainPass> Pass;
    }
    private static readonly ConditionalWeakTable<World, Frame> Frames = new();
    private ShaderMaterial _rainMaterial;
    private ImageTexture _sheet;
    private World _world;
    internal bool Active { get; private set; }

    // The core supplies world only after validating this frame's mode and
    // surface target. Fixtures omit world and never suppress classic weather.
    internal void Bind(Texture2D surface, Vector2I size, float rainIntensity, float wind, double seconds,
        World world = null, Vector2I? surfaceOrigin = null)
    {
        if (surface == null || (world != null && !surfaceOrigin.HasValue) || size.X <= 0 || size.Y <= 0
            || surface.GetWidth() != size.X || surface.GetHeight() != size.Y)
        {
            HideForFallback();
            return;
        }
        if (_rainMaterial == null)
        {
            _sheet = CreateSheet();
            _rainMaterial = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://src/Render/SpeedMode/Weather/speed_rain.gdshader")
            };
            _rainMaterial.SetShaderParameter("drop_sheet", _sheet);
            Material = _rainMaterial;
            MouseFilter = MouseFilterEnum.Ignore;
            TextureFilter = TextureFilterEnum.Nearest;
        }
        Size = size;
        _rainMaterial.SetShaderParameter("surface_buffer", surface);
        _rainMaterial.SetShaderParameter("target_size", new Vector2(size.X, size.Y));
        var origin = surfaceOrigin ?? Vector2I.Zero;
        _rainMaterial.SetShaderParameter("surface_origin", new Vector2(origin.X, origin.Y));
        _rainMaterial.SetShaderParameter("intensity", float.IsFinite(rainIntensity) ? Math.Clamp(rainIntensity, 0, 1) : 0);
        _rainMaterial.SetShaderParameter("wind", float.IsFinite(wind) ? Math.Clamp(wind, -2, 2) : 0);
        _rainMaterial.SetShaderParameter("seconds", double.IsFinite(seconds) ? (float)(seconds % 3600) : 0);
        Active = true;
        Visible = true;
        _world = world;
        if (world != null)
        {
            var frame = Frames.GetValue(world, static _ => new Frame());
            frame.Tick = Time.Ticks;
            if (frame.Pass == null || !frame.Pass.TryGetTarget(out var pass) || !ReferenceEquals(pass, this))
                frame.Pass = new WeakReference<SpeedModeRainPass>(this);
        }
    }

    internal static bool ReplacesClassic(World world) => world != null
        && Frames.TryGetValue(world, out var frame) && frame.Tick == Time.Ticks
        && frame.Pass != null && frame.Pass.TryGetTarget(out var pass)
        && ReferenceEquals(pass._world, world) && pass.Active && pass.IsInsideTree() && pass.IsVisibleInTree();

    internal void HideForFallback()
    {
        Active = false;
        Visible = false;
        _world = null;
        _rainMaterial?.SetShaderParameter("surface_buffer", default(Variant));
    }

    internal static ImageTexture CreateSheet()
    {
        using var image = Image.CreateEmpty(96, 32, false, Image.Format.Rgba8);
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            float px = (x + 0.5f) / 32f - 0.5f;
            float py = (y + 0.5f) / 32f - 0.5f;
            float streak = Math.Abs(px) < 0.045f && Math.Abs(py) < 0.46f ? 1f - Math.Abs(py) : 0f;
            float radius = MathF.Sqrt(px * px + py * py * 4f);
            float splash = Math.Abs(radius - 0.36f) < 0.055f ? 1f : 0f;
            float drip = px * px + py * py < 0.05f ? 1f : 0f;
            image.SetPixel(x, y, new Color(streak, streak, streak, 1));
            image.SetPixel(x + 32, y, new Color(splash, splash, splash, 1));
            image.SetPixel(x + 64, y, new Color(drip, drip, drip, 1));
        }
        return ImageTexture.CreateFromImage(image);
    }

    public override void _ExitTree()
    {
        HideForFallback();
        Material = null;
        _rainMaterial?.Dispose();
        _sheet?.Dispose();
        _rainMaterial = null;
        _sheet = null;
    }
}
