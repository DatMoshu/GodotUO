// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039, drawing path retired 2026-11-14): the headless contract check for the
// light core that lighting speed mode shares.
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer.Lights;

/// <summary>
/// Checks the shared light core without game data: the retired profile field
/// still loading, the ambient ramp, the blocker rule, falloff, wall art
/// classification and wall-lamp placement, the light profile file and curves,
/// and the spell-effect light table. Prints "[Realtime lights] PASS" and
/// exits 0, or the failed check and exits 1.
/// </summary>
public partial class RealtimeLightsProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        try
        {
            // The retired option: an old saved profile still loads, the field keeps its value, nothing reads it.
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Require(!profile.RealtimeLights, "Existing profiles must default off");
            profile.RealtimeLights = true;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(restored.RealtimeLights, "A retired Realtime lights value still round-trips");

            // Ambient ramp: white by day, the blue-grey floor at the darkest level, warmer in between.
            Color day = RealtimeLightField.AmbientFor(1f), night = RealtimeLightField.AmbientFor(0.0625f), dusk = RealtimeLightField.AmbientFor(0.5f);
            Require(day.R > 0.99f && day.G > 0.99f && day.B > 0.99f, "Full day must be white");
            Require(night.B > night.R && night.R < 0.0625f, "Night must be a dim blue floor");
            Require(dusk.R > dusk.B && dusk.R < day.R, "Dusk must sit between, warm");
            foreach (float level in new[] { 0.0625f, 0.1875f, 0.5f, 1f })
            {
                Color ambient = RealtimeLightField.AmbientFor(level);
                float luminance = ambient.R * 0.2126f + ambient.G * 0.7152f + ambient.B * 0.0722f;
                Require(Math.Abs(luminance - level) < 0.0001f, "Ambient must preserve classic night brightness");
            }

            // Tiledata rule.
            Require(RealtimeLightField.Blocks(true, false, false, false, 0), "A wall blocks");
            Require(RealtimeLightField.Blocks(false, true, false, false, 20), "A tall impassable static blocks");
            Require(!RealtimeLightField.Blocks(false, true, false, false, 2), "A low impassable static does not block");
            Require(!RealtimeLightField.Blocks(true, true, true, false, 20), "A door does not block");
            Require(!RealtimeLightField.Blocks(true, true, false, true, 20), "A window does not block");


            // Falloff end points.
            Require(Math.Abs(RealtimeLightField.Falloff(0f, 5f) - 1f) < 0.001f && RealtimeLightField.Falloff(5f, 5f) < 0.001f, "Falloff is 1 at the source and 0 at the radius");

            CheckWalls();
            CheckProfiles();
            CheckEffects();

            GD.Print("[Realtime lights] PASS");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Realtime lights] FAIL: {e.Message}");
            return false;
        }
    }

    /// <summary>Wall art classification and wall-lamp placement.</summary>
    private static void CheckWalls()
    {
        // Art classification: a "\" wall, a "/" wall, a "V" corner, and a blob that is no wall.
        Require(Wall(c => c - 10).Count == 1 && Wall(c => c - 10).First.AlongX, "A \\ silhouette is a wall along X");
        Require(Wall(c => 40 - c).Count == 1 && !Wall(c => 40 - c).First.AlongX, "A / silhouette is a wall along Y");
        WallFaces v = Wall(c => c < 22 ? c + 10 : 54 - c);
        Require(v.Count == 2 && v.First.AlongX && !v.Second.AlongX && v.First.To == v.Second.From, "A V silhouette is a corner: two faces split at a column");
        Require(Wall(c => (c * 7919) % 23 + 10).Count == 0, "A ragged silhouette is no wall plane");
        // Line placement: a "\" whose base runs through the tile's middle (box Y = X) is the line y + 0.5.
        WallFaces middle = Wall(c => c + 55);
        Require(middle.Count == 1 && Math.Abs(middle.First.Offset - 0.5f) < 0.05f, "A \\ base through the tile's middle is on its middle line");

        // A lamp on a wall's tile shines from the shown face, not from inside it.
        var field = new RealtimeLightField();
        field.Begin(0, 0, 16, 16);
        field.AddEdge(5, 5, true, 0.5f, 20);
        Require(Math.Abs(field.LightOrigin(5, 5).Y - (5.5f + RealtimeLightField.WallLampStandoff)) < 0.001f
            && Math.Abs(field.LightOrigin(4, 5).Y - 5.5f) < 0.001f, "A wall lamp stands off its wall on the shown side");
    }

    private static WallFaces Wall(Func<int, int> bottom) => WallFaces.Classify(Silhouette(44, 100, bottom), 44, 100);

    /// <summary>A 44-wide art whose column c is opaque from row 0 down to row bottom(c).</summary>
    private static uint[] Silhouette(int width, int height, Func<int, int> bottom)
    {
        var px = new uint[width * height];
        for (int c = 0; c < width; c++)
        {
            int b = Math.Clamp(bottom(c), 0, height - 1);
            for (int r = 0; r <= b; r++) px[r * width + c] = 0xFF808080;
        }
        return px;
    }

    /// <summary>The light profile file and the A..E curves.</summary>
    private static void CheckProfiles()
    {
        // Every curve: 1 at the source, 0 at the radius, never rising.
        foreach (LightCurve c in new[] { LightCurve.A, LightCurve.B, LightCurve.C, LightCurve.D, LightCurve.E })
        {
            Require(Math.Abs(RealtimeLightProfiles.Evaluate(c, 0f) - 1f) < 0.001f, $"Curve {c} is 1 at the source");
            Require(RealtimeLightProfiles.Evaluate(c, 1f) < 0.001f, $"Curve {c} is 0 at the radius");
            float last = 1f;
            for (int i = 1; i <= 20; i++)
            {
                float v = RealtimeLightProfiles.Evaluate(c, i / 20f);
                Require(v <= last + 0.0001f, $"Curve {c} never rises");
                last = v;
            }
        }

        Require(Math.Abs(RealtimeLightField.Falloff(2f, 5f, LightCurve.B) - RealtimeLightField.Falloff(2f, 5f)) < 0.0001f, "Curve B is the shipped falloff");
        Require(RealtimeLightProfiles.Evaluate(LightCurve.C, 0.5f) < RealtimeLightProfiles.Evaluate(LightCurve.D, 0.5f), "C has a hotter core than D");
        Require(Math.Abs(RealtimeLightProfiles.Evaluate(LightCurve.E, 0.5f) - 1f) < 0.001f, "E holds its plateau");

        // The built-in default overrides nothing.
        var builtIn = RealtimeLightProfiles.BuiltIn();
        LightProfile none = builtIn.Resolve(0x0A0F, 1);
        Require(none.Radius == 0 && none.RadiusScale == 0f && none.Color == null && none.Energy == 0f && none.Curve == null, "Built-in profile overrides nothing");

        // A file: default, light id, item, custom curve; item over light over default.
        var p = RealtimeLightProfiles.Parse(@"{
            // comments are allowed
            ""version"": 1,
            ""curves"": { ""ember"": [1, 0.4, 0.1, 0] },
            ""default"": { ""curve"": ""D"" },
            ""lights"": { ""2"": { ""radius"": 4, ""energy"": 1.5 } },
            ""items"": {
                ""0x0A0F"": { ""color"": ""#FF8000"", ""curve"": ""C"", ""note"": ""candle"" },
                ""2576"": { ""curve"": ""ember"", ""radiusScale"": 0.5 },
            }
        }");
        Require(p.ItemCount == 2 && p.LightCount == 1, "Profile file entries are read");
        LightProfile candle = p.Resolve(0x0A0F, 2);
        Require(candle.Curve == LightCurve.C && candle.Radius == 4 && Math.Abs(candle.Energy - 1.5f) < 0.001f, "Item entry lies over the light id entry");
        Require(candle.Color.HasValue && candle.Color.Value.R > 0.99f && Math.Abs(candle.Color.Value.G - 0.5f) < 0.01f, "Colour is #RRGGBB");
        Require(p.Resolve(0x1234, 9).Curve == LightCurve.D, "Unlisted lights take the file default");
        LightProfile ember = p.Resolve(2576, 9);
        Require(ember.Curve == LightCurve.Custom && Math.Abs(ember.RadiusScale - 0.5f) < 0.001f, "A named curve is a custom curve");
        float[] samples = p.CustomCurve(ember.CustomCurve);
        Require(samples != null && samples.Length == RealtimeLightProfiles.CurveSamples && samples[^1] == 0f, "Custom curves are resampled and end at 0");
        Require(Math.Abs(RealtimeLightProfiles.Evaluate(LightCurve.Custom, 1f / 3f, samples) - 0.4f) < 0.02f, "Custom curve passes its samples");

        // A broken or missing file is the built-in default, with the reason.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo_light_profiles_probe");
        System.IO.Directory.CreateDirectory(dir);
        string bad = System.IO.Path.Combine(dir, "bad.json");
        System.IO.File.WriteAllText(bad, "{ \"items\": { \"0x10\": { \"curve\": \"Z\" } } }");
        var fallback = RealtimeLightProfiles.Load(bad);
        Require(fallback.Error != null && fallback.ItemCount == 0 && fallback.Source == "built-in", "A bad file falls back to the built-in default");
        var missing = RealtimeLightProfiles.Load(System.IO.Path.Combine(dir, "missing.json"));
        Require(missing.ItemCount == 0 && missing.Error != null, "A missing file falls back to the built-in default");
        Require(RealtimeLightProfiles.Load(bad).Resolve(0x10, 0).Curve == null, "Fallback keeps every offer as it was");

    }

    /// <summary>The spell-effect light hook.</summary>
    private static void CheckEffects()
    {
        RealtimeLightEffects.Clear();
        uint t = 1_000_000;
        int h = RealtimeLightEffects.Add(t, 8, 8, 0, 4, new Color(0.2f, 0.4f, 1f), 1000);
        Require(h > 0 && RealtimeLightEffects.Count == 1, "A temporary light is added");
        var into = new RealtimeLightField.Source[8];
        Require(RealtimeLightEffects.Collect(t + 100, into, 2) == 1 && into[2].X == 8 && into[2].Curve == LightCurve.E, "It is collected after the offers");
        Require(Math.Abs(into[2].Energy - RealtimeLightField.Energy) < 0.001f, "Full strength before its fade");


        RealtimeLightEffects.Collect(t + 900, into, 0);
        Require(into[0].Energy < RealtimeLightField.Energy * 0.5f && into[0].Energy > 0f, "It fades over the end of its duration");
        Require(RealtimeLightEffects.Collect(t + 1000, into, 0) == 0 && RealtimeLightEffects.Count == 0, "It expires at its duration");

        int a = RealtimeLightEffects.Add(t, 1, 1, 0, 3, Colors.White, 5000);
        RealtimeLightEffects.Add(t, 2, 2, 0, 3, Colors.White, 5000, fadeOut: false);
        Require(RealtimeLightEffects.Remove(a) && !RealtimeLightEffects.Remove(a) && RealtimeLightEffects.Count == 1, "A handle puts its light out once");
        RealtimeLightEffects.Collect(t + 4999, into, 0);
        Require(Math.Abs(into[0].Energy - RealtimeLightField.Energy) < 0.001f, "No fade when asked for none");

        for (int i = 0; i < RealtimeLightEffects.MaxLights + 10; i++) RealtimeLightEffects.Add(t, i, 0, 0, 2, Colors.White, 5000);
        Require(RealtimeLightEffects.Count == RealtimeLightEffects.MaxLights, "The table is capped; the oldest go");
        Require(RealtimeLightEffects.Add(t, 0, 0, 0, 2, Colors.White, 0) == 0, "A zero duration adds nothing");
        RealtimeLightEffects.Clear();
        Require(RealtimeLightEffects.Count == 0, "Clear puts every light out");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}