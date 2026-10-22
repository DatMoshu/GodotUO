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
            // The parent is freed by hand: Dispose on a Node wrapper leaves the
            // engine object (and the target's viewports under it) alive at exit.
            var targetParent = new Node();
            using (var target = new RealtimeLightTarget())
            {
                Node parent = targetParent;
                target.EnsureSize(parent, 4, 4);
                byte[] bytes = new byte[16];
                target.SetField(bytes, 2, 2, 0, 0, 1, 1, Colors.White);
                target.SetField(bytes, 2, 2, 3, 4, 1, 1, Colors.Black);
                Require(target.FieldUploads == 1, "Unchanged light bytes must skip upload even when origin/ambient changes");
                bytes[0] = 1;
                target.SetField(bytes, 2, 2, 3, 4, 1, 1, Colors.Black);
                Require(target.FieldUploads == 2, "Changed bytes in a reused buffer must upload");
                target.SetField(bytes, 1, 4, 3, 4, 1, 1, Colors.Black);
                Require(target.FieldUploads == 3, "Changed dimensions must recreate even with identical bytes");
                target.Teardown();
                target.EnsureSize(parent, 4, 4);
                target.SetField(bytes, 1, 4, 3, 4, 1, 1, Colors.Black);
                Require(target.FieldUploads == 4, "Recreated targets must upload their first frame");
            }
            targetParent.Free();
            // Bounded teardown: resizing and recreating a target frees its old
            // canvas item each time, and disposing frees the last one.
            int liveBefore = RealtimeLightTarget.LiveItems;
            {
                var parent = new Node();
                var target = new RealtimeLightTarget();
                for (int i = 0; i < 8; i++) target.EnsureSize(parent, 4 + i, 4);
                Require(RealtimeLightTarget.LiveItems == liveBefore + 1, "A resized light target holds one canvas item");
                target.Teardown();
                Require(RealtimeLightTarget.LiveItems == liveBefore, "Teardown frees the canvas item");
                target.EnsureSize(parent, 6, 6);
                target.Dispose();
                target.Dispose();
                Require(RealtimeLightTarget.LiveItems == liveBefore, "Dispose frees the canvas item once");
                parent.Free();
            }
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

            // Time of day (UO+, ADR-0039 addendum): the RunUO family clock, its light
            // cycle, the colour of the hour, and torches weaker by day.
            Require(profile.RealtimeLightsHour == -1, "The time-of-day hour defaults to the shard clock");
            Require(!UoPlusOptions.Requested(profile, UoPlusFeature.TimeOfDay), "Time-of-day light defaults off");
            var epoch = new DateTime(1997, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            Require(Math.Abs(RealtimeTimeOfDay.ShardHour(epoch.AddSeconds(5 * 60 * 13), 0, 0) - 13f) < 0.001f, "Five seconds per UO minute from 1997-09-01");
            Require(Math.Abs(RealtimeTimeOfDay.ShardHour(epoch, 1, 160) - (320 + 10) / 60f) < 0.001f, "Map index and column shift the hour");
            Require(RealtimeTimeOfDay.LevelForHour(2f) == 12 && RealtimeTimeOfDay.LevelForHour(5f) == 6
                && RealtimeTimeOfDay.LevelForHour(12f) == 0 && RealtimeTimeOfDay.LevelForHour(23f) == 6, "Light cycle as the RunUO family's");
            Color dawnTint = RealtimeTimeOfDay.Tint(5.5f), noonTint = RealtimeTimeOfDay.Tint(12f),
                duskTint = RealtimeTimeOfDay.Tint(22.4f), nightTint = RealtimeTimeOfDay.Tint(1f);
            Require(dawnTint.R > dawnTint.G && dawnTint.G > dawnTint.B, "Dawn is warm");
            Require(noonTint.R > 0.99f && noonTint.G > 0.99f && noonTint.B > 0.99f, "Noon is white");
            Require(duskTint.R > duskTint.G && duskTint.B >= duskTint.G - 0.01f, "Dusk is orange-pink");
            Require(nightTint.B > nightTint.R && nightTint.B > nightTint.G, "Night is blue");
            Color noonAmbient = RealtimeTimeOfDay.Ambient(1f, 12f, 1f);
            Require(noonAmbient.R > 0.99f && noonAmbient.G > 0.99f && noonAmbient.B > 0.99f, "Noon at full day is the classic frame");
            Color nightAmbient = RealtimeTimeOfDay.Ambient(0.625f, 1f, 1f);
            float nightLum = nightAmbient.R * 0.2126f + nightAmbient.G * 0.7152f + nightAmbient.B * 0.0722f;
            Require(nightAmbient.B > nightAmbient.R && Math.Abs(nightLum - 0.625f) < 0.002f, "Night keeps the level's brightness, tinted blue");
            Color cave = RealtimeTimeOfDay.Ambient(0.1875f, 12f, 1f), caveRamp = RealtimeLightField.AmbientFor(0.1875f);
            Require(Math.Abs(cave.R - caveRamp.R) < 0.001f && Math.Abs(cave.B - caveRamp.B) < 0.001f, "A dungeon's dark level ignores the noon sky");
            Color zero = RealtimeTimeOfDay.Ambient(0.625f, 22.4f, 0f), ramp = RealtimeLightField.AmbientFor(0.625f);
            Require(Math.Abs(zero.R - ramp.R) < 0.001f && Math.Abs(zero.B - ramp.B) < 0.001f, "Strength 0 is the level-only ramp");
            Require(RealtimeTimeOfDay.LightScale(1f, 12f, 1f) < 0.5f && RealtimeTimeOfDay.LightScale(0.625f, 1f, 1f) > 0.99f
                && RealtimeTimeOfDay.LightScale(0.1875f, 12f, 1f) > 0.99f, "Torches weaker by day, full at night and underground");

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
            Color at = field.Cell(16, 16), near = field.Cell(18, 16), far = field.Cell(16, 23), behind = field.Cell(22, 16), beside = field.Cell(16, 21);
            Require(at.R >= near.R && near.R > night.R + 0.05f, "Light falls off with distance");
            Require(Math.Abs(far.R - night.R) < 0.02f, "Nothing past the radius");
            Require(behind.R < beside.R - 0.1f && Math.Abs(behind.R - night.R) < 0.03f, "A wall blocks the light behind it");
            Require(field.Cell(20, 16).R > night.R + 0.05f, "The wall's own tile takes light on the lit face");

            // Second frame, nothing moved: a cache hit.
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) field.AddBlocker(20, y, 20);
            field.Compute(0.0625f, new[] { torch });
            Require(field.LastComputed == 0, "Unchanged frame hits the cache");
            float full = field.Cell(16, 21).R - night.R;
            field.Begin(0, 0, 32, 32);
            for (int y = 0; y < 32; y++) field.AddBlocker(20, y, 20);
            field.Compute(0.0625f, RealtimeLightField.AmbientFor(0.0625f), 0.5f, new[] { torch });
            Require(Math.Abs(field.Cell(16, 21).R - night.R - full * 0.5f) < 0.01f && full > 0.05f, "The light scale scales every source");

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

            // By day a warm torch keeps its hue but never dims the daylight around it.
            field.Begin(0, 0, 32, 32);
            field.Compute(1f, new[] { new RealtimeLightField.Source(16, 16, 0, 6, RealtimeLightField.WarmLight) });
            Color sunTorch = field.Cell(16, 16), sunFine = field.FineAt(16.5f, 16.5f);
            Require(sunTorch.R > 0.99f && sunTorch.G > 0.99f && sunTorch.B > 0.99f
                && sunFine.R > 0.99f && sunFine.G > 0.99f && sunFine.B > 0.99f, "A torch by day is no darker than daylight");

            // Sun and moon shadows (UO+, ADR-0039 addendum).
            Require(!UoPlusOptions.Requested(profile, UoPlusFeature.SunShadows), "Sun shadows default off");
            var noonSun = RealtimeTimeOfDay.SkyShadow(12.5f, 1f, 1f);
            var dawnSun = RealtimeTimeOfDay.SkyShadow(5.5f, 1f, 1f);
            var duskSun = RealtimeTimeOfDay.SkyShadow(22.5f, 1f, 1f);
            var moon = RealtimeTimeOfDay.SkyShadow(1.5f, 0.625f, 1f);
            Require(noonSun.Active && noonSun.TanElevation > 1.5f, "Noon: a high sun, short shadows");
            Require(dawnSun.Active && dawnSun.TanElevation < 0.4f && dawnSun.DirX < -0.9f, "Dawn: a low sun in the east, long shadows to the west");
            Require(duskSun.Active && duskSun.TanElevation < 0.4f && duskSun.DirX > 0.9f, "Dusk: a low sun in the west, long shadows to the east");
            Require(moon.Active && moon.Strength <= RealtimeTimeOfDay.MoonShadow + 0.001f && moon.Strength < noonSun.Strength, "Night: a faint moon shadow");
            Require(!RealtimeTimeOfDay.SkyShadow(12.5f, 0.1875f, 1f).Active, "No sky shadow in a dungeon's dark");
            Require(!RealtimeTimeOfDay.SkyShadow(12.5f, 1f, 0f).Active, "Strength 0 is no shadow");

            var sunField = new RealtimeLightField();
            var lowSun = new RealtimeLightField.Sun(-1f, 0f, 0.5f, 0.5f); // west-pointing, 20 Z wall -> 4 tiles
            void SunGrid()
            {
                sunField.Begin(0, 0, 32, 32);
                for (int y = 0; y < 32; y++) sunField.AddBlocker(20, y, 20);
                sunField.SetRoof(17, 4);
            }
            SunGrid();
            sunField.SetSun(in lowSun);
            sunField.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(sunField.LastShadowComputed == 1, "The shadow mask is computed");
            Require(sunField.Cell(18, 16).R < 0.75f && sunField.Cell(22, 16).R > 0.99f && sunField.Cell(13, 16).R > 0.99f,
                "A wall's shadow falls away from the sun, as long as its height allows");
            Require(Math.Abs(sunField.Cell(17, 4).R - RealtimeLightField.RoofDaylight) < 0.01f, "Under a roof the sky shadow adds nothing");
            SunGrid();
            sunField.SetSun(in lowSun);
            sunField.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(sunField.LastShadowComputed == 0, "An unchanged sun and grid hit the mask cache");
            SunGrid();
            sunField.SetSun(new RealtimeLightField.Sun(1f, 0f, 0.5f, 0.5f));
            sunField.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(sunField.LastShadowComputed == 1 && sunField.Cell(22, 16).R < 0.75f && sunField.Cell(18, 16).R > 0.99f, "A moved sun recomputes the mask");
            SunGrid();
            sunField.SetSun(in lowSun);
            sunField.AddMobileCaster(8.5f, 8.5f, 0);
            sunField.Compute(1f, new[] { new RealtimeLightField.Source(18, 24, 0, 3, Colors.White) });
            Require(sunField.FineAt(7.6f, 8.5f).R < 0.85f && sunField.FineAt(8.5f, 10.5f).R > 0.99f, "A mobile casts its own shadow");
            Require(sunField.Cell(18, 24).R > sunField.Cell(18, 16).R + 0.2f, "A lamp lights a sky shadow");
            sunField.Begin(0, 0, 32, 32);
            sunField.SetSun(default);
            sunField.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
            Require(sunField.Cell(18, 16).R > 0.99f, "No sun set is no shadow");

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

            CheckWalls(night);
            CheckProfiles();
            CheckEffects(night);
            CheckLightTypes(night);
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

    /// <summary>Wall lines, their two sides, shadows behind blockers, wall lamps and the per-pixel samples.</summary>
    private static void CheckWalls(Color night)
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

        // A wall along X on y = 10.5 across the grid; a light 3 south of it.
        var field = new RealtimeLightField();
        field.Begin(0, 0, 32, 32);
        for (int x = 0; x < 32; x++) field.AddEdge(x, 10, true, 0.5f, 20);
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 13, 0, 6, Colors.White) });
        Require(field.FineAt(16.5f, 10.85f).R > night.R + 0.1f, "The face toward the light is lit");
        Require(Math.Abs(field.FineAt(16.5f, 10.1f).R - night.R) < 0.03f, "The far side of the wall is dark");
        Require(Math.Abs(field.FineAt(16.5f, 8.5f).R - night.R) < 0.02f, "Nothing reaches past the wall");
        // The same wall lit from the north: its south face (the shown one) is dark.
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 7, 0, 6, Colors.White) });
        Require(Math.Abs(field.FineAt(16.5f, 10.85f).R - night.R) < 0.03f && field.FineAt(16.5f, 10.1f).R > night.R + 0.1f, "A wall's faces take the light of their own side");
        // Per-pixel falloff along the face: brighter in front of the light than off to the side.
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 13, 0, 6, new Color(0.3f, 0.3f, 0.3f)) });
        Require(field.FineAt(16.5f, 10.85f).R > field.FineAt(19.5f, 10.85f).R + 0.02f
            && field.FineAt(19.5f, 10.85f).R > field.FineAt(21.5f, 10.85f).R, "Light falls off along the face as it does on the ground");
        Require(field.FineAt(16.25f, 12f).R != field.FineAt(16.75f, 12f).R || field.FineScale > 1, "Samples finer than a tile");

        // A post (a whole-tile blocker) throws a shadow wedge behind it.
        field.Begin(0, 0, 32, 32);
        field.AddBlocker(16, 15, 20);
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 13, 0, 6, Colors.White) });
        Require(Math.Abs(field.FineAt(16.5f, 17.5f).R - night.R) < 0.03f, "Behind a post is its shadow");
        Require(field.FineAt(18.5f, 15.5f).R > night.R + 0.1f, "Beside the post is lit");

        // A lamp on a wall's tile shines from the shown face, not from inside it.
        field.Begin(0, 0, 16, 16);
        field.AddEdge(5, 5, true, 0.5f, 20);
        Require(Math.Abs(field.LightOrigin(5, 5).Y - (5.5f + RealtimeLightField.WallLampStandoff)) < 0.001f
            && Math.Abs(field.LightOrigin(4, 5).Y - 5.5f) < 0.001f, "A wall lamp stands off its wall on the shown side");

        // The texture carries the samples: sqrt(v / 8) encoded, decoded c * c * 8.
        field.Begin(0, 0, 4, 4);
        field.Compute(1f, ReadOnlySpan<RealtimeLightField.Source>.Empty);
        var rgba = new byte[field.FineWidth * field.FineHeight * 4];
        field.FillTexture(rgba);
        float decoded = rgba[0] / 255f * (rgba[0] / 255f) * RealtimeLightField.TextureRange;
        Require(Math.Abs(decoded - 1f) < 0.03f && rgba[3] == 255, "The field texture decodes to the field");
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
    /// <summary>Spot, flash, presets and flicker (ADR-0039 light-types addendum).</summary>
    private static void CheckLightTypes(Color night)
    {
        // A spot pointing east, 60 degrees wide: ahead lit, behind and beside dark, its own tile lit.
        var field = new RealtimeLightField();
        var east = new RealtimeLightField.Cone(0f, 60f);
        field.Begin(0, 0, 32, 32);
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 16, 0, 6, Colors.White, beam: east) });
        Require(field.Cell(19, 16).R > night.R + 0.2f, "A spot lights ahead of it");
        Require(Math.Abs(field.Cell(13, 16).R - night.R) < 0.02f && Math.Abs(field.Cell(16, 19).R - night.R) < 0.02f, "Not behind it nor beside it");
        Require(field.Cell(16, 16).R > night.R + 0.2f, "A spot's own lamp glows all round");
        // Swing the beam south: the same reach (no recompute), the light moves.
        field.Begin(0, 0, 32, 32);
        field.Compute(0.0625f, new[] { new RealtimeLightField.Source(16, 16, 0, 6, Colors.White, beam: new RealtimeLightField.Cone(90f, 60f)) });
        Require(field.LastComputed == 0, "A sweeping beam keeps the cached reach");
        Require(field.Cell(16, 19).R > night.R + 0.2f && Math.Abs(field.Cell(19, 16).R - night.R) < 0.02f, "The beam follows its direction");
        // The accumulation's bounds and radius cut: a light the field's edge clips
        // gives the same cells as in a larger field, for each curve and a beam, and
        // nothing beyond its radius is lit.
        foreach ((LightCurve curve, RealtimeLightField.Cone cone) in new[]
        {
            (LightCurve.B, default(RealtimeLightField.Cone)), (LightCurve.C, default), (LightCurve.E, default),
            (LightCurve.D, new RealtimeLightField.Cone(45f, 70f)),
        })
        {
            var edge = new RealtimeLightField.Source(1, 2, 0, 4, Colors.White, curve: curve, energy: 2.2f, beam: cone);
            var small = new RealtimeLightField();
            small.Begin(0, 0, 24, 24);
            small.Compute(0.0625f, new[] { edge });
            var large = new RealtimeLightField();
            large.Begin(-12, -12, 48, 48);
            large.Compute(0.0625f, new[] { edge });
            bool same = true;
            for (int y = 0; y < 8 && same; y++)
            {
                for (int x = 0; x < 8 && same; x++)
                {
                    Color a = small.Cell(x, y), b = large.Cell(x, y);
                    same = Math.Abs(a.R - b.R) < 1e-5f && Math.Abs(a.G - b.G) < 1e-5f && Math.Abs(a.B - b.B) < 1e-5f;
                }
            }
            Require(same, $"A clipped light matches the unclipped one ({curve}{(cone.IsSpot ? ", spot" : "")})");
            Require(Math.Abs(large.Cell(1 + 6, 2).R - night.R) < 1e-5f && Math.Abs(large.Cell(1, 2 - 6).R - night.R) < 1e-5f,
                $"Nothing beyond the radius is lit ({curve})");
        }

        var sweep = new LightSpot(0f, 40f, 45f, 4f);
        var c0 = sweep.ConeAt(0.0); var c1 = sweep.ConeAt(1.0);
        Require(Math.Abs(c0.DirX - 1f) < 0.001f && c1.DirY > 0.69f, "A sweep swings the beam by its angle over its period");

        // A flash: full at once, an exponential fall, gone at its end.
        RealtimeLightEffects.Clear();
        const uint t = 50_000;
        var into = new RealtimeLightField.Source[8];
        Require(RealtimeLightEffects.AddFlash(t, 4, 4, 0, 5, Colors.White, 400, energy: 4f) > 0, "A flash is added");
        Require(RealtimeLightEffects.Collect(t, into, 0) == 1 && Math.Abs(into[0].Energy - 4f) < 0.01f, "A flash starts at its peak");
        RealtimeLightEffects.Collect(t + 200, into, 0);
        Require(into[0].Energy < 4f * 0.1f && into[0].Energy > 0f, "A flash falls fast");
        Require(RealtimeLightEffects.Collect(t + 400, into, 0) == 0, "A flash is gone at its end");

        // Presets: the curated eight, a file entry over a preset, a spot in the file, flicker.
        foreach (string name in new[] { "candle", "torch", "brazier", "lantern", "oil-lamp", "forge", "magic-crystal", "will-o-wisp" })
        {
            Require(RealtimeLightProfiles.BuiltInPresets.TryGetValue(name, out LightProfile pr) && pr.Radius > 0 && pr.Color.HasValue, $"Preset {name} is built in");
        }
        var profiles = RealtimeLightProfiles.Parse(@"{
            ""version"": 1,
            ""presets"": { ""blue-torch"": { ""preset"": ""torch"", ""color"": ""#4060FF"" } },
            ""items"": {
                ""0x0A0F"": { ""preset"": ""candle"", ""radius"": 3 },
                ""0x0A10"": { ""preset"": ""blue-torch"" },
                ""0x0A11"": { ""preset"": ""lantern"", ""spot"": { ""direction"": 45, ""width"": 50, ""sweep"": 30, ""period"": 6 } }
            }
        }");
        LightProfile candle = profiles.Resolve(0x0A0F, 0);
        Require(candle.Radius == 3 && candle.Curve == LightCurve.C && candle.Flicker > 0.1f, "An entry lays its fields over its preset");
        Require(profiles.Resolve(0x0A10, 0).Color.Value.B > 0.9f && profiles.Resolve(0x0A10, 0).Energy > 2f, "A file preset builds on a built-in one");
        LightProfile beam = profiles.Resolve(0x0A11, 0);
        Require(beam.Spot.HasValue && Math.Abs(beam.Spot.Value.Width - 50f) < 0.01f && Math.Abs(beam.Spot.Value.Sweep - 30f) < 0.01f, "A spot is read from the file");
        Require(profiles.Resolve(0x1234, 0).Curve == null && profiles.Resolve(0x1234, 0).Flicker == null && !profiles.Resolve(0x1234, 0).Spot.HasValue, "Untouched items keep the offer");
        bool rejected = false;
        try { RealtimeLightProfiles.Parse(@"{ ""items"": { ""1"": { ""preset"": ""no-such"" } } }"); }
        catch (FormatException) { rejected = true; }
        Require(rejected, "An unknown preset rejects the file");
        float lo = 1f, hi = 0f;
        for (int i = 0; i < 200; i++)
        {
            float k = candle.FlickerAt(i * 0.05, 7);
            lo = Math.Min(lo, k); hi = Math.Max(hi, k);
        }
        Require(lo >= 1f - candle.Flicker.Value - 0.001f && hi <= 1.001f && hi - lo > 0.05f, "A flame flickers within its depth");
        Require(Math.Abs(candle.FlickerAt(1.0, 7) - candle.FlickerAt(1.0, 8)) > 0.001f, "Neighbouring flames do not flicker together");
        Require(new LightProfile(0, 0f, null, 0f, null, -1).FlickerAt(1.0, 7) == 1f, "No flicker is steady");

        // The effect hook takes a preset by name.
        Require(RealtimeLightEffects.AddPreset(t, "forge", 2, 2, 0, 1000) > 0, "A preset light is added by name");
        Require(RealtimeLightEffects.AddPreset(t, "no-such", 2, 2, 0, 1000) == 0, "An unknown preset adds nothing");
        Require(RealtimeLightEffects.Collect(t + 10, into, 0) == 1 && into[0].Radius == 5 && into[0].Color.R > into[0].Color.B, "The forge preset's radius and tint");
        RealtimeLightEffects.Clear();
    }

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
