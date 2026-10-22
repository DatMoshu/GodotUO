// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039 time-of-day addendum): the shard clock and the colour of the hour.

using System;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The hour of the shard's day and the ambient colour it gives. Pure, so the
    /// probe runs it headless.
    /// </summary>
    /// <remarks>
    /// UO sends no clock: the light level (0x4F) is the only time signal, and it
    /// reads the same at dawn and dusk and all day long. The RunUO family
    /// (RunUO, ServUO, ModernUO) derives both the clock gumps and the light cycle
    /// from one formula on UTC: five real seconds per UO minute since 1997-09-01,
    /// plus 320 minutes per map index and one minute per 16 tiles east. The
    /// client computes the same hour, so the colour follows the shard's day. The
    /// light level still owns brightness; the hour only gives the tint, and
    /// where the level is much darker than any sky (a dungeon) the level's own
    /// ramp wins.
    /// </remarks>
    internal static class RealtimeTimeOfDay
    {
        /// <summary>Real seconds per UO minute (RunUO family <c>Clock.SecondsPerUOMinute</c>).</summary>
        public const double SecondsPerUOMinute = 5.0;

        private static readonly DateTime WorldStart = new DateTime(1997, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Daylight scale of a light at full day: torches read weaker under the sun.</summary>
        public const float DayLightScale = 0.45f;

        /// <summary>
        /// The shard's hour, 0 to 24 with a fraction, at map <paramref name="mapIndex"/>
        /// and tile column <paramref name="x"/> (RunUO family <c>Clock.GetTime</c>,
        /// kept continuous so the colour does not step each UO minute).
        /// </summary>
        public static float ShardHour(DateTime utcNow, int mapIndex, int x)
        {
            double minutes = (utcNow - WorldStart).TotalSeconds / SecondsPerUOMinute;
            minutes += Math.Max(0, mapIndex) * 320 + Math.Max(0, x) / 16;
            double hour = minutes / 60.0 % 24.0;
            return (float)(hour < 0 ? hour + 24.0 : hour);
        }

        /// <summary>
        /// The RunUO family light cycle for an hour, as a shard light level
        /// (0 day, 12 night): night to 4, up to day by 6, day to 22, down to night by 24.
        /// </summary>
        public static int LevelForHour(float hour)
        {
            hour = Wrap(hour);
            const int day = 0, night = 12;
            int minutes = (int)(hour * 60f);
            if (hour < 4f) return night;
            if (hour < 6f) return night + (minutes - 240) * (day - night) / 120;
            if (hour < 22f) return day;
            return day + (minutes - 1320) * (night - day) / 120;
        }

        // The hue of the sky light through the day, by hour (luminance is set later).
        private static readonly (float hour, Color tint)[] Keys =
        {
            (0.0f, new Color(0.66f, 0.74f, 1.00f)),   // night, moonlit blue
            (3.6f, new Color(0.66f, 0.74f, 1.00f)),
            (4.6f, new Color(1.00f, 0.84f, 0.82f)),   // first light, rose
            (5.5f, new Color(1.00f, 0.88f, 0.72f)),   // dawn, warm gold
            (7.0f, new Color(1.00f, 0.96f, 0.90f)),   // morning
            (10.0f, new Color(1.00f, 1.00f, 1.00f)),  // noon white
            (16.0f, new Color(1.00f, 1.00f, 1.00f)),
            (19.0f, new Color(1.00f, 0.96f, 0.88f)),  // late afternoon
            (21.0f, new Color(1.00f, 0.86f, 0.68f)),  // golden hour, orange
            (22.4f, new Color(1.00f, 0.79f, 0.80f)),  // dusk, orange-pink
            (23.3f, new Color(0.82f, 0.78f, 0.98f)),  // twilight violet
            (24.0f, new Color(0.66f, 0.74f, 1.00f)),
        };

        /// <summary>The sky's tint at an hour (unit max channel), eased between the keys.</summary>
        public static Color Tint(float hour)
        {
            hour = Wrap(hour);
            for (int i = 1; i < Keys.Length; i++)
            {
                if (hour > Keys[i].hour) continue;
                var (h0, c0) = Keys[i - 1];
                var (h1, c1) = Keys[i];
                float t = h1 > h0 ? (hour - h0) / (h1 - h0) : 0f;
                t = t * t * (3f - 2f * t);
                return c0.Lerp(c1, t);
            }

            return Keys[0].tint;
        }

        /// <summary>How much the sun is up at an hour: 0 at night, 1 through the day, eased at dawn and dusk.</summary>
        public static float Daylight(float hour)
        {
            hour = Wrap(hour);
            float up = Smooth(4.5f, 7f, hour), down = 1f - Smooth(20.5f, 23f, hour);
            return Math.Min(up, down);
        }

        /// <summary>
        /// The ambient for a classic grey <paramref name="isometricLevel"/> at an hour.
        /// The hour's tint at the level's luminance, blended back toward the
        /// level-only ramp (<see cref="RealtimeLightField.AmbientFor"/>) as the
        /// level falls below any night sky, and by <paramref name="strength"/> (0..1).
        /// No channel exceeds 1: the light target multiplies the world.
        /// </summary>
        public static Color Ambient(float isometricLevel, float hour, float strength)
        {
            float t = Math.Clamp(isometricLevel, 0f, 1f);
            Color baseline = RealtimeLightField.AmbientFor(t);
            Color tint = Tint(hour);
            float luminance = tint.R * 0.2126f + tint.G * 0.7152f + tint.B * 0.0722f;
            float scale = t / Math.Max(0.0001f, luminance);
            float max = Math.Max(tint.R, Math.Max(tint.G, tint.B));
            if (max * scale > 1f) scale = 1f / max;
            var sky = new Color(tint.R * scale, tint.G * scale, tint.B * scale);
            // A sky only above a night's dimness; deeper dark is a dungeon's.
            float w = Math.Clamp(strength, 0f, 1f) * Smooth(0.30f, 0.55f, t);
            return baseline.Lerp(sky, w);
        }

        /// <summary>
        /// The scale on every light source: weaker by day, full at night, and
        /// full wherever the level says it is dark whatever the clock (a dungeon).
        /// </summary>
        public static float LightScale(float isometricLevel, float hour, float strength)
        {
            float sun = Daylight(hour) * Smooth(0.55f, 0.95f, Math.Clamp(isometricLevel, 0f, 1f));
            return 1f - (1f - DayLightScale) * sun * Math.Clamp(strength, 0f, 1f);
        }

        /// <summary>The sun is up from this hour (the RunUO family light cycle brightens from 4:00) ...</summary>
        public const float SunRise = 4.5f;

        /// <summary>... to this one (night is full at 24:00): highest at 14:00, the middle of the bright day.</summary>
        public const float SunSet = 23.5f;

        /// <summary>A full sky shadow at noon takes this share of the ambient (before strength).</summary>
        public const float SunShadow = 0.42f;

        /// <summary>A moon shadow takes this share: faint, and it keeps the night's blue.</summary>
        public const float MoonShadow = 0.16f;

        /// <summary>
        /// The sky light's shadow at an hour: the sun rises in the east (+X),
        /// stands in the south (+Y) and sets in the west, low and long at dawn
        /// and dusk, high and short in the middle of the day; between sunset
        /// and sunrise a faint moon crosses the same way. Shadows fall away
        /// from the light. Strength fades with the level as the sky colour
        /// does (none in a dungeon), times <paramref name="strength"/> (0..1).
        /// </summary>
        public static RealtimeLightField.Sun SkyShadow(float hour, float isometricLevel, float strength)
        {
            hour = Wrap(hour);
            float sky = Smooth(0.30f, 0.55f, Math.Clamp(isometricLevel, 0f, 1f)) * Math.Clamp(strength, 0f, 1f);
            // The sun's disc is up a little longer than full daylight: dawn and dusk get the long shadows.
            float day = Smooth(4.0f, 5.5f, hour) * (1f - Smooth(22.5f, 24f, hour));
            float t, maxElevation, share;
            if (day >= 0.5f)
            {
                t = Math.Clamp((hour - SunRise) / (SunSet - SunRise), 0f, 1f);
                maxElevation = 62f;
                share = SunShadow * day;
            }
            else
            {
                // The night runs from sunset to sunrise across midnight.
                float night = SunRise + 24f - SunSet;
                t = Math.Clamp(Wrap(hour - SunSet) / night, 0f, 1f);
                maxElevation = 40f;
                share = MoonShadow * (1f - day);
            }

            // Near the swap both strengths are small, so the direction change does not show.
            share *= Smooth(0.0f, 0.15f, (float)Math.Sin(Math.PI * t));
            float elevation = (6f + (maxElevation - 6f) * (float)Math.Sin(Math.PI * t)) * (MathF.PI / 180f);
            float toX = MathF.Cos(MathF.PI * t), toY = MathF.Sin(MathF.PI * t);
            return new RealtimeLightField.Sun(-toX, -toY, MathF.Tan(elevation), share * sky);
        }

        private static float Wrap(float hour)
        {
            hour %= 24f;
            return hour < 0f ? hour + 24f : hour;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
