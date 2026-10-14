// GUO-owned (HD light Phase 3): the headless sun proof.
//
// --hd-sun-probe: no shard, no world. Evaluates the color-temperature curve
// across the day and checks its shape (dawn warms, noon harsh, dusk blue),
// loads the hd_sun shader and checks every uniform the stack pushes, and
// builds the height buffer once to prove it allocates and stamps. Prints
// one line per check; exit 0 only when all pass.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Renderer.Lights;
using GUO.Renderer.PostFx;

namespace GUO.Host;

internal static class HdSunProbe
{
    public static bool Passed { get; private set; }

    public static bool Run(Node host)
    {
        bool ok = true;
        ok &= CheckCurve();
        ok &= CheckShader();
        ok &= CheckPreset();
        ok &= CheckBuffer(host);
        ok &= CheckGi();
        ok &= CheckWeather();
        ok &= CheckNightVision();
        Passed = ok;
        GD.Print($"[GUO] hd-sun probe: {(ok ? "ok" : "FAIL")}");
        return ok;
    }

    private static bool CheckCurve()
    {
        bool ok = true;
        float[] hours = { 0f, 4.5f, 5.5f, 6.5f, 8f, 12f, 16f, 17.75f, 19f, 20f };

        HdSun.SunSample dawn = HdSun.Evaluate(6.5f);
        HdSun.SunSample noon = HdSun.Evaluate(12f);
        HdSun.SunSample dusk = HdSun.Evaluate(19f);
        HdSun.SunSample night = HdSun.Evaluate(0f);

        foreach (float h in hours)
        {
            HdSun.SunSample s = HdSun.Evaluate(h);
            GD.Print($"[GUO] hd-sun probe: {h:00.00}h " +
                $"rgb({s.Color.R:0.00},{s.Color.G:0.00},{s.Color.B:0.00}) x{s.Energy:0.00} " +
                $"elev {s.Elevation:0.00} slope {s.Slope:0.00} " +
                $"dir({s.ShadowDir.X:+0.00;-0.00},{s.ShadowDir.Y:+0.00;-0.00}) " +
                $"{(s.IsNight ? "moon" : "sun")}");
        }

        ok &= Expect("noon brighter than dawn", noon.Energy > dawn.Energy, $"{noon.Energy:0.00} > {dawn.Energy:0.00}");
        ok &= Expect("dawn brighter than night", dawn.Energy > night.Energy, $"{dawn.Energy:0.00} > {night.Energy:0.00}");
        ok &= Expect("noon near-white", noon.Color.R > 0.95f && noon.Color.G > 0.95f && noon.Color.B > 0.95f,
            $"({noon.Color.R:0.00},{noon.Color.G:0.00},{noon.Color.B:0.00})");
        ok &= Expect("dawn warm (red dominates)", dawn.Color.R > dawn.Color.G && dawn.Color.G > dawn.Color.B,
            $"({dawn.Color.R:0.00},{dawn.Color.G:0.00},{dawn.Color.B:0.00})");
        ok &= Expect("dusk blue-shifted", dusk.Color.B > dusk.Color.R * 0.8f && dusk.IsNight == false,
            $"({dusk.Color.R:0.00},{dusk.Color.G:0.00},{dusk.Color.B:0.00})");
        ok &= Expect("night is moon", night.IsNight && noon.IsNight == false, "");
        ok &= Expect("shadows swing east->west", dawn.ShadowDir.X < -0.5f && HdSun.Evaluate(17.75f).ShadowDir.X > 0.5f,
            $"dawn {dawn.ShadowDir.X:0.00}, golden {HdSun.Evaluate(17.75f).ShadowDir.X:0.00}");
        ok &= Expect("noon ray steeper than dawn", noon.Slope > dawn.Slope, $"{noon.Slope:0.00} > {dawn.Slope:0.00}");
        ok &= Expect("24h wraps", HdSun.Evaluate(24f).Energy == night.Energy, "");

        return ok;
    }

    private static bool CheckShader()
    {
        bool ok = true;
        Shader shader = PostFxLibrary.Shader("hd_sun");
        ok &= Expect("hd_sun shader loads", shader != null, "");

        if (shader == null)
        {
            return false;
        }

        // "source" is deliberately absent: a hint_screen_texture sampler is
        // fed from the backbuffer, not listed in GetShaderUniformList (same
        // for every existing hd_* pass). Everything the stack pushes is here.
        string[] needed =
        {
            "height_tex", "sun_shadow_dir", "sun_color", "sun_energy",
            "sun_floor", "sun_slope", "sun_px_per_z", "sun_steps",
            "sun_strength", "sun_soften", "sun_shadow_px",
        };

        var names = new HashSet<string>();
        try
        {
            foreach (Godot.Collections.Dictionary d in shader.GetShaderUniformList())
            {
                names.Add((string)d["name"]);
            }
        }
        catch (Exception e)
        {
            return Expect("shader uniforms readable", false, e.Message);
        }

        foreach (string n in needed)
        {
            ok &= Expect($"uniform {n}", names.Contains(n), "");
        }

        return ok;
    }

    /// <summary>The HD Light look loads with hd_sun first and every pass's shader resolves.</summary>
    private static bool CheckPreset()
    {
        bool ok = true;
        PostFxPreset preset = PostFxLibrary.Find("HD Light");
        ok &= Expect("HD Light preset loads", preset != null, "");

        if (preset == null)
        {
            return false;
        }

        ok &= Expect("hd_sun runs first", preset.Passes.Count > 0 && preset.Passes[0].Shader == "hd_sun",
            preset.Passes.Count > 0 ? preset.Passes[0].Shader : "no passes");

        foreach (PostFxPass pass in preset.Passes)
        {
            ok &= Expect($"pass shader {pass.Shader}", PostFxLibrary.Shader(pass.Shader) != null, "");
        }

        return ok;
    }

    private static bool CheckBuffer(Node host)
    {
        bool ok = true;

        var buffer = new HdHeightBuffer();
        buffer.EnsureSize(host, 1280, 800, mobile: false);
        ok &= Expect("height buffer builds (640x400)", buffer.IsBuilt, "");

        if (buffer.IsBuilt)
        {
            buffer.BeginFill();
            // One ground tile, one wall (Z 0 + 20), one splat rect.
            buffer.Stamp(new Rect2(0, 0, 11, 11), 0);
            buffer.Stamp(new Rect2(11, 0, 11, 31), 20);
            buffer.Stamp(new Rect2(40, 40, 60, 30), 12);
            ok &= Expect("3 stamps laid", buffer.LastStamps == 3, $"{buffer.LastStamps}");
            ok &= Expect("buffer texture live",
                buffer.Texture != null && GodotObject.IsInstanceValid(buffer.Texture)
                && buffer.Texture.GetWidth() == 640 && buffer.Texture.GetHeight() == 400,
                $"{buffer.Texture?.GetWidth()}x{buffer.Texture?.GetHeight()}");
        }

        buffer.Dispose();
        ok &= Expect("height buffer tears down", !buffer.IsBuilt, "");
        return ok;
    }

    private static bool Expect(string what, bool pass, string detail)
    {
        GD.Print($"[GUO] hd-sun probe: {(pass ? "pass" : "FAIL")}: {what}" +
            (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
        return pass;
    }

    /// <summary>
    /// GUO-owned (HD light Phase 4): the cascade proof. hd_gi loads with
    /// every uniform the stack pushes, the HD Light look orders
    /// hd_sun -&gt; hd_gi -&gt; hd_tonemap with the composite active, the tonemap
    /// honours the skip flag, and the tier governor matches the sun's.
    /// </summary>
    private static bool CheckGi()
    {
        bool ok = true;
        Shader gi = PostFxLibrary.Shader("hd_gi");
        ok &= Expect("hd_gi shader loads", gi != null, "");

        if (gi == null)
        {
            return false;
        }

        // "source" is fed from the backbuffer (absent from the uniform list,
        // like every existing hd_* pass). Everything else the stack or the
        // preset drives is here.
        string[] needed =
        {
            "light_tex", "height_tex",
            "gi_strength", "gi_range", "gi_bleed", "gi_bounce",
            "gi_soften", "gi_threshold", "gi_falloff", "gi_steps",
        };

        var names = new HashSet<string>();
        try
        {
            foreach (Godot.Collections.Dictionary d in gi.GetShaderUniformList())
            {
                names.Add((string)d["name"]);
            }
        }
        catch (Exception e)
        {
            return Expect("gi uniforms readable", false, e.Message);
        }

        foreach (string n in needed)
        {
            ok &= Expect($"uniform {n}", names.Contains(n), "");
        }

        ok &= Expect("hd_gi marked heavy (mobile halves the stack)", gi.Code.Contains("// postfx: heavy"), "");
        // Stateless means no clock uniform and no feedback in CODE: strip
        // the // comments first, since the header names the rule itself.
        bool stateless = true;
        foreach (string line in gi.Code.Split('\n'))
        {
            string code = line;
            int cut = code.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0)
            {
                code = code.Substring(0, cut);
            }

            if (code.Contains("TIME"))
            {
                stateless = false;
                break;
            }
        }

        ok &= Expect("hd_gi stateless (no TIME, no feedback)", stateless, "");

        Shader tonemap = PostFxLibrary.Shader("hd_tonemap");
        ok &= Expect("hd_tonemap shader loads", tonemap != null, "");
        if (tonemap != null)
        {
            bool skip = false;
            try
            {
                foreach (Godot.Collections.Dictionary d in tonemap.GetShaderUniformList())
                {
                    if ((string)d["name"] == "hd_skip_light")
                    {
                        skip = true;
                    }
                }
            }
            catch
            {
            }

            ok &= Expect("hd_tonemap honours hd_skip_light", skip, "");
        }

        PostFxPreset preset = PostFxLibrary.Find("HD Light");
        ok &= Expect("HD Light preset loads (gi)", preset != null, "");
        if (preset != null)
        {
            int sun = preset.Passes.FindIndex(p => p.Enabled && p.Shader == "hd_sun");
            int giIdx = preset.Passes.FindIndex(p => p.Enabled && p.Shader == "hd_gi");
            int tone = preset.Passes.FindIndex(p => p.Enabled && p.Shader == "hd_tonemap");
            ok &= Expect("hd_gi runs after hd_sun", sun >= 0 && giIdx > sun, $"sun={sun} gi={giIdx}");
            ok &= Expect("hd_gi runs before hd_tonemap", giIdx >= 0 && tone > giIdx, $"gi={giIdx} tonemap={tone}");
            float s = 0f;
            if (giIdx >= 0 && preset.Passes[giIdx].Params.TryGetValue("gi_strength", out System.Text.Json.Nodes.JsonNode n) &&
                n is System.Text.Json.Nodes.JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                s = v.GetValue<float>();
            }

            ok &= Expect("hd_gi composite active in preset", s > 0.001f, $"gi_strength={s:0.00}");
        }

        ok &= Expect("gi governor desktop 10 (3 rings)", HdGi.For(mobile: false, zoom: 1f) == 10, "");
        ok &= Expect("gi governor mobile 6 (near+mid)", HdGi.For(mobile: true, zoom: 1f) == 6, "");
        ok &= Expect("gi governor extreme zoom 6", HdGi.For(mobile: false, zoom: 2f) == 6, "");
        GD.Print("[GUO] hd-sun probe: tap budget desktop 24 taps x4 samples (~100/px), mobile 16x4 (~60/px)");
        return ok;
    }

    /// <summary>
    /// GUO-owned (HD light Phase 5): the weather proof. Targets, the thunder
    /// envelope shape, the ambient/sun math and the new uniforms are pure, so
    /// they are checked here with no world; the GameScene wires the live
    /// Weather into them per frame.
    /// </summary>
    private static bool CheckWeather()
    {
        bool ok = true;

        ok &= Expect("wet target rain 1", HdWeather.TargetWet(GUO.Game.WeatherType.WT_RAIN, 70) == 1f, "");
        ok &= Expect("wet target storm brews 0.8", HdWeather.TargetWet(GUO.Game.WeatherType.WT_STORM_BREWING, 70) == 0.8f, "");
        ok &= Expect("wet target snow below rain",
            HdWeather.TargetWet(GUO.Game.WeatherType.WT_SNOW, 70) < HdWeather.TargetWet(GUO.Game.WeatherType.WT_RAIN, 70), "");
        ok &= Expect("wet target clear 0", HdWeather.TargetWet(null, 0) == 0f, "");
        ok &= Expect("wet target count 0 dry", HdWeather.TargetWet(GUO.Game.WeatherType.WT_RAIN, 0) == 0f, "");

        float stormDim = HdWeather.TargetDim(GUO.Game.WeatherType.WT_STORM_BREWING, 70);
        float rainDim = HdWeather.TargetDim(GUO.Game.WeatherType.WT_RAIN, 70);
        float snowDim = HdWeather.TargetDim(GUO.Game.WeatherType.WT_SNOW, 70);
        ok &= Expect("dim storm above rain above snow", stormDim > rainDim && rainDim > snowDim,
            $"{stormDim:0.00} > {rainDim:0.00} > {snowDim:0.00}");
        ok &= Expect("dim clear 0", HdWeather.TargetDim(null, 0) == 0f, "");

        ok &= Expect("flash fires at the clap", HdWeather.FlashEnvelope(0f) > 0.99f, $"{HdWeather.FlashEnvelope(0f):0.00}");
        ok &= Expect("flash decays (0.05 above 1.0s)",
            HdWeather.FlashEnvelope(0.05f) > HdWeather.FlashEnvelope(1f),
            $"{HdWeather.FlashEnvelope(0.05f):0.00} > {HdWeather.FlashEnvelope(1f):0.00}");
        ok &= Expect("flash silent past the window", HdWeather.FlashEnvelope(5f) == 0f, "");
        ok &= Expect("flash silent with no thunder", HdWeather.FlashEnvelope(float.PositiveInfinity) == 0f, "");

        HdWeather.Reset();
        HdWeather.Dim = 1f;
        ok &= Expect("ambient dims under clouds", HdWeather.ApplyAmbient(1f) < 1f, $"{HdWeather.ApplyAmbient(1f):0.00}");
        HdWeather.Flash = 1f;
        ok &= Expect("flash lifts the ambient", HdWeather.ApplyAmbient(0.2f) > 0.2f, $"{HdWeather.ApplyAmbient(0.2f):0.00}");
        ok &= Expect("sun dims under clouds", HdWeather.SunDimFactor() < 1f, $"{HdWeather.SunDimFactor():0.00}");
        HdWeather.Reset();
        ok &= Expect("clear sun factor 1", HdWeather.SunDimFactor() == 1f, "");
        ok &= Expect("clear ambient passthrough", HdWeather.ApplyAmbient(0.5f) == 0.5f, "");

        HdWeather.FlashHold = true;
        HdWeather.Update(null, 0.0);
        ok &= Expect("held strike reads 0.85 (screenshot framing)", Math.Abs(HdWeather.Flash - 0.85f) < 0.001f,
            $"{HdWeather.Flash:0.00}");
        HdWeather.Reset();
        ok &= Expect("reset releases the hold", HdWeather.Flash == 0f && !HdWeather.FlashHold, "");

        // The -hd_weather dry override (forced-dry A/B stage): wet ground
        // dries toward 0 and clouds clear even with no world behind them.
        HdWeather.Wetness = 1f;
        HdWeather.Dim = 1f;
        HdWeather.OverrideKind = "dry";
        HdWeather.Update(null, 1.0);
        ok &= Expect("dry override dries wet ground", HdWeather.Wetness < 1f, $"{HdWeather.Wetness:0.00}");
        ok &= Expect("dry override clears the dim", HdWeather.Dim < 1f, $"{HdWeather.Dim:0.00}");
        HdWeather.Reset();

        // The stack pushes what the shaders declare: tonemap wet+flash,
        // bloom wet, GI wet. A missing uniform here is a silent no-op pass.
        Shader tonemap = PostFxLibrary.Shader("hd_tonemap");
        Shader bloom = PostFxLibrary.Shader("hd_bloom");
        Shader gi = PostFxLibrary.Shader("hd_gi");
        ok &= Expect("hd_tonemap declares hd_wet", HasUniform(tonemap, "hd_wet"), "");
        ok &= Expect("hd_tonemap declares hd_flash", HasUniform(tonemap, "hd_flash"), "");
        ok &= Expect("hd_bloom declares hd_wet", HasUniform(bloom, "hd_wet"), "");
        ok &= Expect("hd_gi declares gi_wet", HasUniform(gi, "gi_wet"), "");

        // Day/night balance: the HD Light look lifts exposure into the dark
        // via the light_level binding, and leaves day (level 0) untouched.
        PostFxPreset preset = PostFxLibrary.Find("HD Light");
        bool balance = false;
        if (preset != null)
        {
            foreach (PostFxBinding b in preset.Bindings)
            {
                if (string.Equals(b.Param, "exposure", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.From, "light_level", StringComparison.OrdinalIgnoreCase) &&
                    b.Offset == 0f && b.Scale > 0f)
                {
                    balance = true;
                }
            }
        }

        ok &= Expect("HD Light binds exposure to light_level (night lift, day untouched)", balance, "");
        return ok;
    }

    /// <summary>
    /// GUO-owned (HD light Phase 5): the HD night-vision proof. The phosphor
    /// samples the lit buffer (light_tex with the tonemap guard) and honours
    /// the GI skip flag, so it cooperates with hd_gi instead of fighting it;
    /// the preset orders sun -&gt; gi -&gt; phosphor.
    /// </summary>
    private static bool CheckNightVision()
    {
        bool ok = true;
        Shader nv = PostFxLibrary.Shader("hd_night_vision");
        ok &= Expect("hd_night_vision shader loads", nv != null, "");

        if (nv == null)
        {
            return false;
        }

        string[] needed = { "light_tex", "intensity", "gain", "hd_skip_light" };
        foreach (string n in needed)
        {
            ok &= Expect($"uniform {n}", HasUniform(nv, n), "");
        }

        PostFxPreset preset = PostFxLibrary.Find("HD Night Vision");
        ok &= Expect("HD Night Vision preset loads", preset != null, "");
        if (preset != null)
        {
            int sun = preset.Passes.FindIndex(p => p.Enabled && string.Equals(p.Shader, "hd_sun", StringComparison.OrdinalIgnoreCase));
            int giIdx = preset.Passes.FindIndex(p => p.Enabled && string.Equals(p.Shader, "hd_gi", StringComparison.OrdinalIgnoreCase));
            int phos = preset.Passes.FindIndex(p => p.Enabled && string.Equals(p.Shader, "hd_night_vision", StringComparison.OrdinalIgnoreCase));
            ok &= Expect("phosphor runs after hd_sun", sun >= 0 && phos > sun, $"sun={sun} phosphor={phos}");
            ok &= Expect("phosphor runs after hd_gi", giIdx >= 0 && phos > giIdx, $"gi={giIdx} phosphor={phos}");

            // Midnight readability: the lit composite sits near 0.08-0.12 with the
            // moon floor (was ~0.03 before it, which left midnight black), so the
            // phosphor gain must amplify well above the old 1.6 (which left
            // midnight black); noon then blows out, as a night tool should.
            float gain = 0f;
            if (phos >= 0 && preset.Passes[phos].Params.TryGetValue("gain", out System.Text.Json.Nodes.JsonNode n) &&
                n is System.Text.Json.Nodes.JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                gain = v.GetValue<float>();
            }

            ok &= Expect("phosphor gain lights the night", gain >= 6f, $"gain={gain:0.0}");
        }

        return ok;
    }

    private static bool HasUniform(Shader shader, string name)
    {
        if (shader == null)
        {
            return false;
        }

        try
        {
            foreach (Godot.Collections.Dictionary d in shader.GetShaderUniformList())
            {
                if ((string)d["name"] == name)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
