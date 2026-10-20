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
            Require(profile.RealtimeShadows && !profile.RealtimeClassicShadows, "Shadows default on (under the off option), classic shadows replaced");
            profile.RealtimeShadows = false;
            profile.RealtimeClassicShadows = true;
            restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(!restored.RealtimeShadows && restored.RealtimeClassicShadows, "Shadow settings must persist");

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

            // Roof dim by day, none at night; a building's outer edge (open sky
            // on its east, south or south-east) is not dimmed: its outer face shows.
            field.Begin(0, 0, 8, 8);
            for (int y = 1; y <= 3; y++) for (int x = 1; x <= 3; x++) field.SetRoof(x, y, 20);
            field.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Cell(2, 2).R - RealtimeLightField.RoofDaylight) < 0.01f && field.Cell(5, 5).R > 0.99f, "Roofed tiles dim by day");
            Require(field.IsDim(1, 1) && !field.IsDim(3, 2) && !field.IsDim(2, 3) && field.Cell(3, 3).R > 0.99f, "The outer edge of a roof is not dimmed");
            Require(field.CellAt(2, 2, 0).R < 0.6f && field.CellAt(2, 2, 20).R > 0.99f, "On or above the cover is open sky");
            field.Compute(0.0625f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Cell(2, 2).R - field.Cell(5, 5).R) < 0.01f, "Roofed tiles are not dimmer at night");

            // A torch at noon neither brightens nor tints the ground; by night it lights it.
            field.Begin(0, 0, 8, 8);
            field.Compute(1f, new[] { new RealtimeLightField.Source(4, 4, 0, 3, new Color(1f, 0.5f, 0f)) });
            Color noon = field.Cell(4, 4);
            Require(noon.R > 0.99f && noon.G > 0.99f && noon.B > 0.99f, "No light shows over full daylight");
            Require(RealtimeLightField.Headroom(RealtimeLightField.AmbientFor(0.1875f)) > 0.99f, "A light shows in full at night");

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

            // Corners average their four cells' ambient; a light is evaluated at the corner itself.
            field.Begin(0, 0, 4, 4);
            field.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(Math.Abs(field.Corner(2, 2).R - 1f) < 0.001f, "Corner of a flat field is the field");
            field.Begin(0, 0, 16, 16);
            var dimLamp = new RealtimeLightField.Source(8, 8, 0, 4, new Color(0.2f, 0.2f, 0.2f));
            field.Compute(0.0625f, new[] { dimLamp });
            float expect = night.R + 0.2f * RealtimeLightField.Energy * RealtimeLightField.Falloff(MathF.Sqrt(0.5f), 4f);
            Require(Math.Abs(field.Corner(8, 8).R - expect) < 0.01f, "A corner takes the light at its own distance");
            Require(Math.Abs(field.SourceAt(0, 10, 8).R - (field.Cell(10, 8).R - night.R)) < 0.01f, "One source's light at a point is its share of the field");
            Color left = RealtimeLightField.Without(new Color(1.2f, 0.9f, 0.6f), new Color(1f, 0.5f, 0f));
            Require(Math.Abs(left.R - 0.2f) < 0.001f && Math.Abs(left.B - 0.6f) < 0.001f, "A shadow leaves the rest of the light");

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
            Require(field.LastComputed == 1 && field.Cell(10, 8).R > before + 0.02f, "Sub-tile motion moves the pool and fetches the next tile's visibility once");
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 3, dim, 0.6f, 0f) });
            Require(field.LastComputed == 0, "Further sub-tile motion reuses both tiles' visibility");

            // A walking light crossfades its visibility: halfway past a wall
            // end, the cell the wall hides from one tile is half lit.
            field.Begin(0, 0, 16, 16);
            for (int y = 0; y <= 5; y++) field.AddBlocker(9, y, 20);
            var lamp = new Color(0.2f, 0.2f, 0.2f);
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 5, 0, 4, lamp) });
            float hidden = field.Cell(10, 5).R;
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 6, 0, 4, lamp) });
            float shown = field.Cell(10, 5).R;
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 6, 0, 4, lamp, 0f, -0.5f) });
            float half = field.Cell(10, 5).R;
            Require(shown > hidden + 0.01f, "A wall end hides a cell from one tile and not the next");
            Require(half > hidden + 0.003f && half < shown + 0.05f, "Halfway, the visibility is between the two tiles'");
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(8, 8, 0, 3, dim, 0.999f, 0f) });
            float seam = field.Cell(10, 8).R;
            field.Compute(0.0625f, new[] { new RealtimeLightField.Source(9, 8, 0, 3, dim) });
            Require(Math.Abs(field.Cell(10, 8).R - seam) < 0.001f, "Pool is continuous through a tile completion");
            Require(Math.Abs(field.Cell(13, 8).R - night.R) < 0.001f, "Local pool reaches ambient beyond its radius");

            // Falloff end points.
            Require(Math.Abs(RealtimeLightField.Falloff(0f, 5f) - 1f) < 0.001f && RealtimeLightField.Falloff(5f, 5f) < 0.001f, "Falloff is 1 at the source and 0 at the radius");

            GD.Print("[Realtime lights] PASS");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Realtime lights] FAIL: {e.Message}");
            return false;
        }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
