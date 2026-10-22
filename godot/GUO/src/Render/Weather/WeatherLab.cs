// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.CompilerServices;
using Godot;
using GUO.Game;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Renderer;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Renderer.Weather;

// Bounded visual state; flags and densities come from the shared UO+ profile.
internal sealed class WeatherSettings
{
    internal bool WaterDrops, Puddles, WetPlayer;
    internal bool Cobblestone = true;
    internal int DropBudget = 12;
    internal int WaterDensity = 100, PuddleDensity = 100, WetDensity = 100;
    internal readonly System.Collections.Generic.HashSet<ushort> PuddleTiles = new();
    internal bool Enabled => WaterDrops || Puddles || WetPlayer;
}

internal sealed class WeatherState
{
    internal readonly WeatherSettings Settings = new();
    internal readonly WeatherSurface[] Surfaces = new WeatherSurface[512];
    internal readonly Random Random = new(2917);
    internal long LastTicks;
    internal int Map = -1, BodyDrops, Impacts;
    internal float Wetness, RainCredit;
    internal bool Raining;
#if GUO_LABS
    internal float? RainOverride;
#endif
    internal readonly BodyFrame[] Frames = new BodyFrame[32];
    internal int FrameCount, AlphaChecks;

    internal void Step(float delta, float intensity)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        bool rain = intensity > 0;
        Raining = rain;
        delta = Math.Clamp(delta, 0, 0.25f);
        Wetness = Settings.WetPlayer ? Approach(Wetness, rain ? 1 : 0, delta * (rain ? intensity / 4 : 1f / 18)) : 0;
        BodyDrops = (Math.Clamp(Settings.DropBudget, 0, 32) * Math.Clamp(Settings.WetDensity, 0, 100) + 99) / 100;
        AlphaChecks = 1024;
        Impacts = 0;
        RainCredit = rain ? Math.Min(32, RainCredit + delta * 40 * intensity) : 0;
    }

    internal static float Approach(float value, float target, float amount) =>
        value < target ? Math.Min(target, value + amount) : Math.Max(target, value - amount);
}

internal readonly record struct BodyFrame(ushort Id, byte Group, byte Direction, bool Uop,
    int Frame, bool Mirror, int X, int Y, int Width, int Height);

internal static class WeatherLab
{
    private sealed class Signal
    {
        internal WeatherType? Type;
        internal int Count;
        internal uint Expires;
    }
    private static readonly ConditionalWeakTable<GUO.Game.Weather, Signal> Signals = new();
    private static readonly ConditionalWeakTable<World, WeatherState> States = new();
    internal static WeatherState For(World world) => States.GetValue(world, _ => new WeatherState());
    internal static Vector3 Hue(float alpha) => new(0, ShaderHueTranslator.SHADER_NONE, alpha);
    internal static Texture2D DropTexture => SolidColorTextureCache.GetTexture(new GUO.Compat.Color(162, 202, 218, 255));

    // Observe every 0x65, including same-type intensity changes that classic ignores.
    // The classic renderer and its message/timer semantics stay untouched.
    internal static void Observe(GUO.Game.Weather weather, WeatherType type, int count, uint now)
    {
        var signal = Signals.GetValue(weather, _ => new Signal());
        signal.Type = type;
        signal.Count = count;
        signal.Expires = now + Constants.WEATHER_TIMER;
    }

    internal static void Clear(World world)
    {
        Signals.Remove(world.Weather);
        States.Remove(world);
    }

    internal static float RainIntensity(World world)
    {
#if GUO_LABS
        if (world != null && States.TryGetValue(world, out var state) && state.RainOverride.HasValue)
            return Math.Clamp(state.RainOverride.Value, 0, 1);
#endif
        return world?.Weather != null && Signals.TryGetValue(world.Weather, out var signal)
            && unchecked((int)(signal.Expires - Time.Ticks)) >= 0
            ? RainIntensity(signal.Type, signal.Count) : 0;
    }

    internal static float RainIntensity(WeatherType? type, int count) =>
        type is WeatherType.WT_RAIN or WeatherType.WT_STORM_APPROACH or WeatherType.WT_STORM_BREWING
            ? Math.Clamp(count / 70f, 0, 1) : 0;

    internal static bool IsRain(World world) => RainIntensity(world) > 0;

#if TOOLS
    // Read-only automation evidence. No state is created by inspection.
    internal static object Inspect(World world)
    {
        if (world?.Player == null) return null;
        int puddles = 0, rings = 0;
        float maxFill = 0;
        States.TryGetValue(world, out var state);
        if (state != null)
            foreach (var surface in state.Surfaces)
                if (surface != null && surface.QueuedAt == state.LastTicks)
                {
                    if (surface.Fill > 0) puddles++;
                    if (surface.RingAge < 1) rings++;
                    maxFill = Math.Max(maxFill, surface.Fill);
                }
        return new { rainIntensity = RainIntensity(world), wetness = state?.Wetness ?? 0,
            puddles, rings, maxFill, enabled = state?.Settings.Enabled ?? false };
    }
#endif

    internal static void Open(World world)
    {
        Game.UI.Gumps.UoPlusOptionsGump.Open(world);
    }

    internal static void SyncProfile(WeatherSettings settings, Profile profile)
    {
        settings.WaterDrops = UoPlusOptions.Enabled(profile, UoPlusFeature.RainRings);
        settings.Puddles = UoPlusOptions.Enabled(profile, UoPlusFeature.Puddles);
        settings.WetPlayer = UoPlusOptions.Enabled(profile, UoPlusFeature.WetPlayer);
        settings.WaterDensity = UoPlusOptions.Density(profile, UoPlusFeature.RainRings);
        settings.PuddleDensity = UoPlusOptions.Density(profile, UoPlusFeature.Puddles);
        settings.WetDensity = UoPlusOptions.Density(profile, UoPlusFeature.WetPlayer);
    }

    internal static bool TryTiles(string text, out ushort[] ids)
    {
        var result = new System.Collections.Generic.HashSet<ushort>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool hex = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!ushort.TryParse(hex ? part[2..] : part, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ushort id) || id > 0x3fff) { ids = Array.Empty<ushort>(); return false; }
            result.Add(id);
        }
        ids = new ushort[result.Count];
        result.CopyTo(ids);
        return true;
    }

    internal static void BeginBody(World world)
    {
        if (States.TryGetValue(world, out var state)) state.FrameCount = 0;
    }

    internal static void CollectBody(Mobile owner, ushort id, byte group, byte direction,
        bool uop, int frame, bool mirror, int x, int y, int width, int height, float depth)
    {
        if (!States.TryGetValue(owner.World, out var state) || !state.Settings.WetPlayer || state.Wetness <= 0 ||
            owner.IsHidden || owner.IsDead || state.BodyDrops == 0 || width <= 0 || height <= 0 || state.FrameCount == state.Frames.Length) return;
        state.Frames[state.FrameCount++] = new(id, group, direction, uop, frame, mirror, x, y, width, height);
    }

    // Check the union of body/equipment/mount alpha, without changing picking state.
    internal static bool BodyPixel(WeatherState state, int x, int y)
    {
        for (int i = 0; i < state.FrameCount; i++)
        {
            var f = state.Frames[i];
            int px = x - f.X, py = y - f.Y;
            if (px < 0 || py < 0 || px >= f.Width || py >= f.Height) continue;
            if (state.AlphaChecks == 0) return false;
            state.AlphaChecks--;
            if (Client.Game.UO.Animations.PixelCheck(f.Id, f.Group, f.Direction, f.Uop, f.Frame,
                f.Mirror ? f.Width - 1 - px : px, py)) return true;
        }
        return false;
    }

    internal static void EndBody(Mobile owner, UltimaBatcher2D batcher, float depth)
    {
        if (!States.TryGetValue(owner.World, out var state) || state.FrameCount == 0) return;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int i = 0; i < state.FrameCount; i++)
        {
            var f = state.Frames[i];
            left = Math.Min(left, f.X); top = Math.Min(top, f.Y);
            right = Math.Max(right, f.X + f.Width); bottom = Math.Max(bottom, f.Y + f.Height);
        }
        int attempts = state.BodyDrops * 8;
        while (attempts-- > 0 && state.BodyDrops > 0)
        {
            int px = state.Random.Next(left, right), py = state.Random.Next(top, bottom);
            if (!BodyPixel(state, px, py)) continue;
            bool edge = !BodyPixel(state, px - 1, py) || !BodyPixel(state, px + 1, py) || !BodyPixel(state, px, py - 1);
            if (!edge || state.AlphaChecks == 0) continue;
            batcher.Draw(DropTexture, new Rectangle(px, py, 1, 2), null, Hue(state.Wetness * 0.75f), 0,
                Vector2.Zero, SpriteEffects.None, depth);
            state.BodyDrops--;
        }
    }
}

internal sealed class WeatherSurface : GameObject
{
    internal long QueuedAt = -1;
    internal float Fill, RingAge = 1, DropX, DropY;
    internal bool Water;
    internal WeatherSurface(World world) : base(world) { Graphic = 1; AlphaHue = 255; }

    internal void Step(float delta, float intensity, bool puddles)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        Fill = !Water && puddles ? WeatherState.Approach(Fill, intensity > 0 ? 1 : 0,
            delta * (intensity > 0 ? intensity / 30 : 1f / 45)) : 0;
        RingAge = Math.Min(1, RingAge + delta / 0.65f);
    }

    public override bool Draw(UltimaBatcher2D batcher, int x, int y, float depth)
    {
        // Land top-left is (-22,-22) from its centre; patches stay inside the diamond.
        var centre = new Vector2(x + 22, y + 22);
        var texture = WeatherLab.DropTexture;
        if (Fill > 0)
        {
            int radius = 2 + (int)(Fill * 10);
            for (int dy = -radius / 2; dy <= radius / 2; dy++)
            {
                int half = radius - Math.Abs(dy) * 2;
                if (half > 0) batcher.Draw(texture, new Rectangle((int)centre.X - half, (int)centre.Y + dy, half * 2, 1),
                    null, WeatherLab.Hue(Fill * 0.22f), 0, Vector2.Zero, SpriteEffects.None, depth);
            }
        }
        if (RingAge < 1)
        {
            centre += new Vector2(DropX, DropY);
            float radius = 1 + RingAge * 7;
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.Tau / 12;
                var p = centre + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius * 0.5f);
                batcher.Draw(texture, new Rectangle((int)p.X, (int)p.Y, 1, 1), null, WeatherLab.Hue((1 - RingAge) * 0.65f),
                    0, Vector2.Zero, SpriteEffects.None, depth);
            }
            if (RingAge < 0.2f)
                batcher.Draw(texture, new Rectangle((int)centre.X, (int)centre.Y - 3, 1, 3), null,
                    WeatherLab.Hue(0.7f), 0, Vector2.Zero, SpriteEffects.None, depth);
        }
        return true;
    }
    public override bool CheckMouseSelection() => false;
}
