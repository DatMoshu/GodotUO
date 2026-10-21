// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using Godot;
using GUO.Renderer.Water;
using GUO.Store;

/// <summary>The real water shader on synthetic water, usable without client data or a shard.</summary>
[Tool]
internal partial class WaterPreview : SubViewportContainer
{
    private SubViewport _viewport;
    private Shader _shader;
    private ShaderMaterial _material;
    private ShaderMaterial _referenceMaterial;
    private ImageTexture _mask;
    private ColorRect _rect;
    private ColorRect _referenceRect;
    private readonly WaterRipples _ripples = new();
    private readonly WaterRipples _referenceRipples = new();
    internal StoreWater ReferenceStyle { get; private set; }
    private float _step, _seconds, _boat;
    internal bool Paused;
    internal bool Boat;
    internal bool Compare;
    internal bool Classic;
    internal bool LiveWorld;
    internal float Seconds => _seconds;
    internal int Scene { get; private set; }
    internal Image Capture() => _viewport?.GetTexture().GetImage();

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        Stretch = true;
        CustomMinimumSize = new Vector2(240, 150);
        _viewport = new SubViewport { Size = new Vector2I(512, 256),
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_viewport);
        // Keep the shader directive inside an ordinary C# string: when TOOLS
        // is undefined the C# preprocessor still scans skipped source lines.
        _shader = new Shader { Code = "shader_type canvas_item;\nrender_mode unshaded;\n#include \"res://src/Render/shaders/uo_water.gdshaderinc\"\n" + """
            uniform bool compare = false;
            uniform bool classic = false;
            uniform float brightness = 1.0;
            uniform bool reference_side = false;
            void fragment() {
                // Original synthetic texels; never samples client artwork.
                vec2 pixel = FRAGCOORD.xy * water_target_to_world.xy;
                if (reference_side && pixel.x >= 256.0) discard;
                float check = mod(floor(pixel.x / 2.0) + floor(pixel.y / 2.0), 2.0);
                vec3 base = mix(vec3(0.07, 0.13, 0.21), vec3(0.09, 0.16, 0.25), check) * brightness;
                vec2 f;
                vec2 world = pixel + vec2(-256.0, -148.0);
                vec2 cell = water_cell(world, f);
                bool dry = water_mask_at(cell).r < 0.5;
                vec3 native = dry ? vec3(0.35, 0.30, 0.20) * brightness : base;
                vec3 styled = dry ? native : water_shade(base, FRAGCOORD.xy);
                COLOR = vec4(classic || (compare && pixel.x < 256.0) ? native : styled, 1.0);
                if (compare && !classic && abs(pixel.x - 256.0) < 1.0) COLOR.rgb = vec3(0.65);
            }
            """ };
        _material = new ShaderMaterial { Shader = _shader };
        _rect = new ColorRect { Size = new Vector2(512, 256), Material = _material, MouseFilter = MouseFilterEnum.Ignore };
        _viewport.AddChild(_rect);
        _referenceMaterial = new ShaderMaterial { Shader = _shader };
        _referenceMaterial.SetShaderParameter("reference_side", true);
        _referenceRect = new ColorRect { Size = _rect.Size, Material = _referenceMaterial,
            MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _viewport.AddChild(_referenceRect);
        SetBoth("water_mask_origin", new Vector2(-64, -64));
        SetBoth("water_target_to_world", new Vector4(1, 1, -256, -148));
        SetBoth("water_ripples_origin", new Vector2(-64, -64));
        SetScene(0);
        SetStyle(WaterAuthoring.Presets[0]);
        ResetWaves();
        SetProcess(true);
    }

    private void SetBoth(string name, Variant value)
    {
        // Callers pass temporary Variants. Texture-bearing temporaries otherwise retain
        // native references until managed finalization, after a short-lived audition closes.
        using (value)
        {
            _material?.SetShaderParameter(name, value);
            _referenceMaterial?.SetShaderParameter(name, value);
        }
    }

    private void EachField(Action<WaterRipples> action)
    {
        action(_ripples);
        if (ReferenceStyle != null) action(_referenceRipples);
    }

    internal void SetReference(StoreWater style)
    {
        ReferenceStyle = style == null ? null : style with { Tint = (float[])style.Tint.Clone() };
        if (ReferenceStyle != null)
        {
            new PackWaterStyle("editor-reference", ReferenceStyle).Bind((name, value) => _referenceMaterial.SetShaderParameter(name, value));
            _referenceRipples.Strength = ReferenceStyle.RippleStrength;
            _referenceRipples.Damping = ReferenceStyle.RippleDamping;
        }
        else
        {
            _referenceRect.Visible = false;
            _referenceMaterial.SetShaderParameter("water_ripples", default(Variant));
            _referenceRipples.DisposeForEditor();
        }
        ResetWaves();
        Advance(0);
    }

    internal void SetStyle(StoreWater style)
    {
        if (_material == null) return;
        Vector2I size = _viewport.Size;
        _rect.Size = size;
        SetBoth("water_target_to_world", new Vector4(512f / Math.Max(1, size.X), 256f / Math.Max(1, size.Y), -256, -148));
        new PackWaterStyle("editor-preview", style).Bind((name, value) => _material.SetShaderParameter(name, value));
        _ripples.Strength = style.RippleStrength;
        _ripples.Damping = style.RippleDamping;
    }

    internal void SetScene(int scene)
    {
        Scene = scene;
        if (_material == null) return;
        using var image = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                int cx = x < 64 ? x : x - 128, cy = y < 64 ? y : y - 128;
                bool dry = scene != 0 && cx + cy >= 2;
                image.SetPixel(x, y, new Color(dry ? 0 : 1, (dry && scene == 2 ? 148f : 123f) / 255f, 0, 1));
            }
        if (_mask == null) _mask = ImageTexture.CreateFromImage(image);
        else _mask.Update(image);
        SetBoth("water_mask", _mask);
        ResetWaves();
    }

    internal void SetBrightness(float value) => SetBoth("brightness", value);
    internal void Splash()
    {
        EachField(field => { field.Stamp(0, 0, .7f, .8f); field.StepForProbe(0); });
        BindWaves();
    }

    internal void ResetWaves()
    {
        EachField(field => { field.Reset(); field.CentreOnForProbe(0, 0); field.StepForProbe(0); });
        _boat = -3; _step = 0;
        BindWaves();
    }

    /// <summary>Rebuilds a synthetic test from t=0 using fixed 20 Hz inputs, then pauses.</summary>
    internal void Replay(int test, float seconds)
    {
        if (test < 0 || test > 2) throw new ArgumentOutOfRangeException(nameof(test));
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        int ticks = (int)MathF.Round(Math.Clamp(seconds, 0, 5) * 20);
        bool movingHull = Boat;
        ResetWaves(); _seconds = 0;
        Paused = false; Boat = test != 0;
        try
        {
            if (test != 1) Splash();
            for (int i = 0; i < ticks; i++) Advance(.05f);
        }
        finally
        {
            Boat = movingHull; Paused = true;
            _seconds = ticks * .05f;
            Advance(0);
        }
    }

    private void BindWaves()
    {
        using Variant waves = _ripples.Texture;
        _material?.SetShaderParameter("water_ripples", waves);
        if (ReferenceStyle != null)
        {
            using Variant referenceWaves = _referenceRipples.Texture;
            _referenceMaterial?.SetShaderParameter("water_ripples", referenceWaves);
        }
    }

    internal void Advance(float delta)
    {
        if (_material == null) return;
        Vector2I size = _viewport.Size;
        _rect.Size = size;
        _referenceRect.Size = size;
        SetBoth("water_target_to_world", new Vector4(512f / Math.Max(1, size.X), 256f / Math.Max(1, size.Y), -256, -148));
        _referenceRect.Visible = ReferenceStyle != null && Compare && !Classic;
        _material.SetShaderParameter("compare", Compare);
        _material.SetShaderParameter("classic", Classic);
        if (!Paused) _seconds += delta;
        SetBoth("water_time", _seconds);
        if (Paused) return;
        _step += Math.Min(delta, .2f);
        int steps = 0;
        while (_step >= .05f)
        {
            if (Boat)
            {
                float was = _boat;
                _boat += .08f; if (_boat > 3) _boat = -3;
                // The same directional stern wake as a native moving hull.
                EachField(field => field.StampHull(new GUO.Compat.Rectangle(-1, -1, 1, 1), was, 0, _boat, 0));
            }
            EachField(field => field.StepForProbe(1));
            _step -= .05f; steps++;
        }
        if (steps > 0) BindWaves();
    }

    public override void _Process(double delta) { if (IsVisibleInTree() || LiveWorld) Advance((float)delta); }

    internal void Shutdown()
    {
        SetProcess(false);
        // Release native material ownership before disposing short-lived audition textures.
        if (GodotObject.IsInstanceValid(_rect)) _rect.Material = null;
        if (GodotObject.IsInstanceValid(_referenceRect)) _referenceRect.Material = null;
        SetBoth("water_ripples", default(Variant));
        SetBoth("water_mask", default(Variant));
        if (_material != null) _material.Shader = null;
        if (_referenceMaterial != null) _referenceMaterial.Shader = null;
        _mask?.Dispose(); _mask = null;
        _material?.Dispose(); _material = null;
        _referenceMaterial?.Dispose(); _referenceMaterial = null;
        _shader?.Dispose(); _shader = null;
        _ripples.DisposeForEditor();
        _referenceRipples.DisposeForEditor();
    }

    public override void _ExitTree() => Shutdown();
}
#endif
