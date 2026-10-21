// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): the headless contract check for Realtime lights.
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer.Lights;

/// <summary>
/// Checks the Realtime lights contract without game data: the option's
/// default and persistence, the ambient ramp, a wall blocking and a door not,
/// falloff, the reach cache and its invalidation, and the roof dim. Prints
/// "[Realtime lights] PASS" and exits 0, or the failed check and exits 1.
/// </summary>
public partial class RealtimeLightsProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        try
        {
            var profile = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Require(!profile.RealtimeLights, "Existing profiles must default off");
            profile.RealtimeLights = true;
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(restored.RealtimeLights, "Realtime lights must persist");

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

            // A 32x32 grid at night with one light and a wall column at x = 20.
            var field = new RealtimeLightField();
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) field.AddBlocker(20, y, 20);
            var torch = new RealtimeLightField.Source(16, 16, 0, 6, Colors.White);
            field.Compute(0.0625f, new[] { torch });
            Require(field.LastComputed == 1 && field.CacheCount == 1, "One reach computed and cached");
            Color at = field.Cell(16, 16), near = field.Cell(18, 16), far = field.Cell(16, 23), behind = field.Cell(22, 16), beside = field.Cell(19, 16);
            Require(at.R >= near.R && near.R > night.R + 0.05f, "Light falls off with distance");
            Require(Math.Abs(far.R - night.R) < 0.02f, "Nothing past the radius");
            Require(behind.R < beside.R - 0.1f && Math.Abs(behind.R - night.R) < 0.03f, "A wall blocks the light behind it");
            Require(field.Cell(20, 16).R > night.R + 0.05f, "The wall's own tile takes light on the lit face");

            // Second frame, nothing moved: a cache hit.
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) field.AddBlocker(20, y, 20);
            field.Compute(0.0625f, new[] { torch });
            Require(field.LastComputed == 0, "Unchanged frame hits the cache");

            // A door opens in the wall (the blocker goes): recomputed, light passes.
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) if (y != 16) field.AddBlocker(20, y, 20);
            field.Compute(0.0625f, new[] { torch });
            Require(field.LastComputed == 1, "Changed blockers recompute the reach");
            Require(field.Cell(21, 16).R > night.R + 0.05f, "Light passes through the gap");

            // A moved light is a new reach; a light below the wall's clearance is blocked, one above is not.
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) field.AddBlocker(20, y, 20);
            var high = new RealtimeLightField.Source(16, 16, 18, 6, Colors.White);
            field.Compute(0.0625f, new[] { high });
            Require(field.Cell(21, 16).R > night.R + 0.05f, "A light above the wall top reaches over it");

            // Roof dim by day, none at night.
            field.Begin(0, 0, 8, 8);
            field.SetRoof(2, 2);
            field.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Cell(2, 2).R - RealtimeLightField.RoofDaylight) < 0.01f && field.Cell(4, 4).R > 0.99f, "Roofed tiles dim by day");
            field.Compute(0.0625f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Cell(2, 2).R - field.Cell(4, 4).R) < 0.01f, "Roofed tiles are not dimmer at night");

            // Cover: an upper floor at 20 covers what stands below it on that cell,
            // not a lamp standing on it (z == cover) nor the cell beside it; a
            // roof-flagged cover is told apart from a floor; a lamp on the upper
            // floor lights the floor (not "above the light"), while a floor over a
            // ground lamp is.
            field.Begin(0, 0, 8, 8);
            field.SetGround(3, 3, 0);
            field.SetRoof(3, 3, 20, roofFlag: false);
            field.SetRoof(5, 5, 24);
            Require(field.CoverZ(3, 3) == 20 && field.CoverZ(4, 3) == RealtimeLightField.NoBlock, "Cover is per cell at the floor's base Z");
            Require(!field.CoverIsRoofFlag(3, 3) && field.CoverIsRoofFlag(5, 5), "Floor cover and roof cover are told apart");
            Require(field.IsRoofed(3, 3), "An upper floor shades the cell under it like a roof");
            var upperLamp = new RealtimeLightField.Source(3, 3, 20, 3, Colors.White);
            field.Compute(0.0625f, new[] { upperLamp });
            Require(!field.IsAboveLight(3, 3, 20), "A floor with a lamp standing on it is lit by it");
            Require(!(20 < field.CoverZ(3, 3)), "A lamp standing on the floor is not under it");
            field.Begin(0, 0, 8, 8);
            field.SetGround(3, 3, 0);
            field.SetRoof(3, 3, 20, roofFlag: false);
            var groundLamp = new RealtimeLightField.Source(3, 3, 0, 3, Colors.White);
            field.Compute(0.0625f, new[] { groundLamp });
            Require(field.IsAboveLight(3, 3, 20), "A floor over a ground lamp is above the light");
            Require(0 < field.CoverZ(3, 3), "A ground lamp under the floor is covered");

            // Corners average their four cells.
            field.Begin(0, 0, 4, 4);
            field.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Corner(2, 2).R - 1f) < 0.001f, "Corner of a flat field is the field");

            // A dim light saturates nothing, so the falloff itself is measurable.
            field.Begin(0, 0, 16, 16);
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 6, new Color(0.3f, 0.3f, 0.3f)) });
            Require(field.Cell(8, 8).R > field.Cell(10, 8).R && field.Cell(10, 8).R > field.Cell(12, 8).R, "A dim light falls off cell by cell");

            // Brighter than white keeps its hue: an orange light at the source is orange, not yellow.
            field.Begin(0, 0, 8, 8);
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(4, 4, 0, 4, new Color(1f, 0.5f, 0f)) });
            Color hot = field.Cell(4, 4);
            Require(hot.R > 0.99f && hot.G < 0.75f && hot.G > hot.B, "A clipped cell is scaled by its brightest channel, hue kept");

            // A walking source moves its pool between tile completions while
            // reusing visibility; crossing the tile seam does not snap it.
            field.Begin(0, 0, 16, 16);
            var dim = new Color(0.3f, 0.2f, 0.1f);
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 3, dim) });
            float before = field.Cell(10, 8).R;
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 3, dim, 0.5f, 0f) });
            Require(field.LastComputed == 0 && field.Cell(10, 8).R > before + 0.02f, "Sub-tile motion moves the pool without rebuilding visibility");
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 3, dim, 0.999f, 0f) });
            float seam = field.Cell(10, 8).R;
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(9, 8, 0, 3, dim) });
            Require(Math.Abs(field.Cell(10, 8).R - seam) < 0.001f, "Pool is continuous through a tile completion");
            Require(Math.Abs(field.Cell(13, 8).R - night.R) < 0.001f, "Local pool reaches ambient beyond its radius");

            // Falloff end points.
            Require(Math.Abs(RealtimeLightField.Falloff(0f, 5f) - 1f) < 0.001f && RealtimeLightField.Falloff(5f, 5f) < 0.001f, "Falloff is 1 at the source and 0 at the radius");

            CheckProfiles();
            CheckEffects(night);
            CheckDebug();

            GD.Print("[Realtime lights] PASS");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Realtime lights] FAIL: {e.Message}");
            return false;
        }
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

        // A profiled source shapes the field: E holds full strength further out than B.
        var field = new RealtimeLightField();
        var dim = new Color(0.3f, 0.3f, 0.3f);
        field.Begin(0, 0, 16, 16);
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 6, dim) });
        float b = field.Cell(11, 8).R;
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 6, dim, curve: LightCurve.E) });
        Require(field.LastComputed == 0 && field.Cell(11, 8).R > b + 0.02f, "A curve changes the pool, not the cached visibility");
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 6, dim, energy: 1.1f) });
        Require(field.Cell(11, 8).R < b - 0.01f, "Energy scales the pool");
    }

    /// <summary>The spell-effect light hook.</summary>
    private static void CheckEffects(Color night)
    {
        RealtimeLightEffects.Clear();
        uint t = 1_000_000;
        int h = RealtimeLightEffects.Add(t, 8, 8, 0, 4, new Color(0.2f, 0.4f, 1f), 1000);
        Require(h > 0 && RealtimeLightEffects.Count == 1, "A temporary light is added");
        var into = new RealtimeLightField.Source[8];
        Require(RealtimeLightEffects.Collect(t + 100, into, 2) == 1 && into[2].X == 8 && into[2].Curve == LightCurve.E, "It is collected after the offers");
        Require(Math.Abs(into[2].Energy - RealtimeLightField.Energy) < 0.001f, "Full strength before its fade");

        var field = new RealtimeLightField();
        field.Begin(0, 0, 16, 16);
        field.Compute(0.0625f, new ReadOnlySpan<RealtimeLightField.Source>(into, 2, 1));
        Color lit = field.Cell(9, 8);
        Require(lit.B > lit.R && lit.B > night.B + 0.1f, "A spell light tints the field in its colour");

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

    /// <summary>The budget record, the dump and the pixel-to-tile readout.</summary>
    private static void CheckDebug()
    {
        RealtimeLightsDebug.Configure(false, null);
        Require(RealtimeLightsDebug.Record(new RealtimeLightsDebug.Budget(3, 1, 1, 0.1, 0.2, 0.3)) == null, "Budget log is off without its flag");
        Require(RealtimeLightsDebug.Last.Lights == 3 && Math.Abs(RealtimeLightsDebug.Last.TotalMs - 0.6) < 1e-9, "The last budget is kept regardless");
        Require(!RealtimeLightsDebug.DumpDue(), "Dump is off without its flag");
        RealtimeLightsDebug.Configure(true, null);
        string line = RealtimeLightsDebug.Record(new RealtimeLightsDebug.Budget(5, 2, 1, 0.1, 0.2, 0.3));
        Require(line != null && line.Contains("lights=5") && line.Contains("temp=2") && line.Contains("total=0.600ms"), "Budget line carries count and cost");
        RealtimeLightsDebug.Configure(false, null);

        // Pixel to tile: every tile's diamond centre maps back to the tile, at any Z.
        foreach (var (x, y, z) in new[] { (0, 0, 0), (1500, 1600, 0), (1500, 1600, 20), (3, 70, -5) })
        {
            float u = (x - y) * 22f, v = (x + y) * 22f - z * 4f;
            Require(RealtimeLightField.ScreenToTile(u, v, z) == (x, y), $"Pixel at tile {x},{y},{z} reads back");
            Require(RealtimeLightField.ScreenToTile(u + 20f, v, z) == (x, y) && RealtimeLightField.ScreenToTile(u, v + 10f, z) == (x, y), "Inside the diamond reads the tile");
            Require(RealtimeLightField.ScreenToTile(u + 22f, v + 23f, z) == (x + 1, y), "Past the lower-right edge is the next x");
        }

        // The ASCII dump: a lit room, a wall, the marked tile, a margin.
        var field = new RealtimeLightField();
        field.Begin(0, 0, 12, 6);
        for (int y = 0; y < 6; y++) field.AddBlocker(6, y, 20);
        field.SetRoof(2, 4, 20);
        var torch = new RealtimeLightField.Source(3, 2, 0, 3, Colors.White);
        field.Compute(0.0625f, new[] { torch });
        string dump = field.DumpAscii(new[] { torch }, (x, y) => x < 10, 1, 1);
        string[] rows = dump.Split('\n');
        Require(rows[0].StartsWith("# grid origin=0,0 size=12x6"), "Dump header names the grid");
        Require(rows.Length >= 9 && rows[2].Length == 12, "One row of 12 per tile row");
        Require(rows[2 + 2][3] == '*' && rows[2 + 1][1] == '@', "Source and mark are placed");
        Require(rows[2 + 0][6] == '#' && rows[2 + 4][2] == '^', "Walls and cover are drawn");
        Require(rows[2 + 0][10] == '.' && rows[2 + 0][11] == '.', "Cells outside the view are dots");
        Require(rows[2 + 2][4] > rows[2 + 2][8] || rows[2 + 2][8] == '0', "Lit cells read brighter than the far side of the wall");
        Require(dump.Contains("# light 3,2,0 r=3 curve=B"), "Dump lists its sources");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
