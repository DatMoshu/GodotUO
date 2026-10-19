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
            Require(night.B > night.R && night.R < 0.25f && night.B > 0.1f, "Night must be a dim blue floor");
            Require(dusk.R > dusk.B && dusk.R < day.R, "Dusk must sit between, warm");

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
            Require(at.R > near.R && near.R > night.R + 0.05f, "Light falls off with distance");
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
