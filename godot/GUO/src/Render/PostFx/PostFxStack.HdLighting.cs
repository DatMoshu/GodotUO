// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned; lighting bindings adapted from JarJar e4a3cd35 (ADR-0027).
using System;
using System.Collections.Generic;
using Godot;
using GUO.Renderer.Lights;
using GUO.Store;

namespace GUO.Renderer.PostFx;

internal sealed partial class PostFxStack
{
    private StoreLighting _lighting;
    internal Rid? ProbeViewport => _viewport?.GetViewportRid();
    private readonly Dictionary<string, Shader> _hdShaders = new(StringComparer.Ordinal);
    private Texture2D _flatHeight, _clearLight;
    internal Texture2D HdHeightTex { get; set; }

    internal void RemoveHdLighting(StoreLighting lighting)
    {
        if (lighting != null && ReferenceEquals(_lighting, lighting)) SetHdLighting(null);
    }

    internal void SetHdLighting(StoreLighting lighting)
    {
        if (ReferenceEquals(_lighting, lighting)) return;
        // Materials are discarded before shaders are released. Ordinary looks
        // and state.json remain owned by the existing PostFx path.
        Teardown();
        foreach (var shader in _hdShaders.Values) shader.Dispose();
        _hdShaders.Clear();
        _lighting = lighting;
        if (lighting != null)
            foreach (var (name, code) in lighting.Shaders) _hdShaders.Add(name, new Shader { Code = code });
        else
        {
            _flatHeight?.Dispose(); _flatHeight = null;
            _clearLight?.Dispose(); _clearLight = null;
            HdHeightTex = null;
        }
        _dirty = true;
    }

    private Shader ResolveShader(string name) => _lighting != null
        ? _hdShaders.GetValueOrDefault(name) : PostFxLibrary.Shader(name);

    private static Texture2D Flat(ref Texture2D texture, Color color)
    {
        if (texture != null) return texture;
        using var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.Fill(color);
        return texture = ImageTexture.CreateFromImage(image);
    }

    private void ApplyHdBindings(Texture2D light)
    {
        var sun = HdSun.Evaluate(HdSun.Hour);
        bool gi = ActivePreset.Passes.Exists(p => p.Enabled && p.Shader == "hd_gi"
            && (!p.Params.TryGetValue("gi_strength", out var v) || v.GetValue<float>() > 0.001f));
        foreach (var b in _built)
        {
            void Bind(string name, Variant value)
            {
                if (HasUniform(b.material.Shader, name)) b.material.SetShaderParameter(name, value);
            }
            Bind("light_tex", light ?? Flat(ref _clearLight, Colors.Transparent));
            Bind("height_tex", HdHeightTex ?? Flat(ref _flatHeight, new Color(HdHeightBuffer.Encode(0), HdHeightBuffer.Encode(0), HdHeightBuffer.Encode(0))));
            Bind("sun_shadow_dir", sun.ShadowDir);
            Bind("sun_color", new Vector3(sun.Color.R, sun.Color.G, sun.Color.B));
            Bind("sun_energy", sun.Energy * HdWeather.SunDimFactor());
            Bind("sun_floor", new Vector3(sun.Floor.R, sun.Floor.G, sun.Floor.B));
            Bind("sun_slope", sun.Slope);
            Bind("sun_px_per_z", HdSun.PxPerZ * Scale);
            Bind("sun_steps", HdSun.Steps);
            Bind("sun_strength", HdSun.Strength);
            Bind("gi_steps", HdGi.Steps);
            Bind("gi_wet", HdWeather.Wetness);
            Bind("hd_wet", HdWeather.Wetness);
            Bind("hd_flash", HdWeather.Flash);
            Bind("hd_skip_light", gi ? 1f : 0f);
        }
    }
}
