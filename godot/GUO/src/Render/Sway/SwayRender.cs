// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game.GameObjects;

namespace GUO.Renderer.Sway;

internal readonly record struct SwayInstance(byte Kind, byte Phase, float Wind)
{
    internal bool Active => Kind != 0 && Wind > 0;
}

internal static class SwayRender
{
    // A missing/broken catalogue disables sway. Never guess from names or flags.
    private static readonly byte[] Kinds = Load();

    internal static byte Classify(int graphic) => (uint)graphic < Kinds.Length ? Kinds[graphic] : (byte)0;
    internal static byte Phase(int x, int y) => (byte)(unchecked((uint)x * 73856093u ^ (uint)y * 19349663u) % 32);
    internal static float Strength(Profile profile, float rain) => UoPlusOptions.Enabled(profile, UoPlusFeature.Sway)
        ? UoPlusOptions.Density(profile, UoPlusFeature.Sway) / 100f * (1 + 1.5f * Math.Clamp(rain, 0, 1)) : 0;

    internal static SwayInstance For(Static obj, ushort graphic)
    {
        var profile = ProfileManager.CurrentProfile;
        if (obj == null || graphic != obj.Graphic || !UoPlusOptions.Requested(profile, UoPlusFeature.Sway))
            return default;
        byte kind = Classify(graphic);
        ref var tile = ref obj.ItemData;
        if (kind == 0 || tile.IsWall || tile.IsRoof || tile.IsWet) return default;
        return new(kind, Phase(obj.X, obj.Y), Strength(profile, WeatherSignal.RainIntensity(obj.World)));
    }

    internal static byte[] Parse(string json)
    {
        var kinds = new byte[65536];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("version").GetInt32() != 1) throw new FormatException("sway version");
        foreach (var range in doc.RootElement.GetProperty("ranges").EnumerateArray())
        {
            int first = Convert.ToInt32(range.GetProperty("first").GetString(), 16);
            int last = Convert.ToInt32(range.GetProperty("last").GetString(), 16);
            byte kind = range.GetProperty("kind").GetString() switch
            { "tree" => 1, "bush" => 2, "plant" => 3, "grass" => 4, "hanging" => 5, _ => throw new FormatException("sway kind") };
            if (first < 0 || last > ushort.MaxValue || last < first) throw new FormatException("sway range");
            for (int i = first; i <= last; i++)
            {
                if (kinds[i] != 0) throw new FormatException("overlapping sway ranges");
                kinds[i] = kind;
            }
        }
        return kinds;
    }

    private static byte[] Load()
    {
        try
        {
            using var stream = typeof(SwayRender).Assembly.GetManifestResourceStream("sway_ranges.json");
            if (stream == null) throw new FormatException("catalogue unavailable");
            using var reader = new System.IO.StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            GD.PushWarning("[sway] disabled: " + e.Message);
            return new byte[65536];
        }
    }

    // Lower trunk remains rigid; hanging plants reverse the anchor.
    internal static float Weight(float height, byte kind) => kind == 5 ? height * height :
        MathF.Pow(Math.Clamp((1 - height - 0.3f) / 0.7f, 0, 1), 2);
    internal static float Pixels(byte kind) => kind switch { 1 => 2f, 2 => 1.2f, 3 => 1f, 4 => 0.65f, 5 => 1.2f, _ => 0 };
}
