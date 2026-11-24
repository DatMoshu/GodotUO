// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GUO.Store;

/// <summary>
/// The translucent-water and wake numbers of a guo/water@2 authoring document
/// (docs/data_formats.md W-01, the depth and wake groups): how far see-through
/// water stays clear, how the seabed floor darkens, and a boat's trail. Editor
/// only. The Store still installs guo/water@1 alone, so a draft saved here is an
/// authoring file, never a pack; the defaults are the numbers the client draws with.
/// </summary>
internal sealed record WaterShallows(float FadeStart, float FadeEnd, float FloorReach, float SandBars, float WakeFoam, float WakeLength)
{
    public const string Schema = "guo/water@2";
    public static readonly WaterShallows Defaults = new(9.5f, 13f, 17f, 0.5f, 1f, 1f);

    /// <summary>Inclusive ranges, the same as water_v2.schema.json.</summary>
    internal static readonly (string Group, string Field, float Low, float High)[] Ranges =
    {
        ("depth", "fade_start", 0f, 28f), ("depth", "fade_end", 1f, 29f), ("depth", "floor_reach", 2f, 40f),
        ("depth", "sand_bars", 0f, 1f), ("wake", "foam", 0f, 2f), ("wake", "length", 0.25f, 3f),
    };

    private float[] Values => new[] { FadeStart, FadeEnd, FloorReach, SandBars, WakeFoam, WakeLength };

    /// <summary>Throws InvalidDataException with the field's name when a number is out of range.</summary>
    public WaterShallows Checked()
    {
        float[] v = Values;
        for (int i = 0; i < Ranges.Length; i++)
            if (!(v[i] >= Ranges[i].Low && v[i] <= Ranges[i].High) || float.IsNaN(v[i]))
                throw new InvalidDataException($"Water {Ranges[i].Group}.{Ranges[i].Field} out of range ({Ranges[i].Low} to {Ranges[i].High}).");
        if (FadeEnd <= FadeStart) throw new InvalidDataException("Water depth.fade_end must exceed depth.fade_start.");
        return this;
    }

    /// <summary>
    /// One complete guo/water@2 document: the v1 style's numbers in their v2 places (the
    /// correspondence in W-01), v2's defaults where v1 has none, and these six.
    /// </summary>
    public static byte[] Serialize(StoreWater style, WaterShallows s)
    {
        s.Checked();
        var doc = new Dictionary<string, object>
        {
            ["schema"] = Schema, ["name"] = style.Name, ["model"] = style.Model,
            ["surface"] = new { base_color = new[] { 1f, 1f, 1f }, opacity = 1f },
            ["depth"] = new { tint = style.Tint, strength = 0f, falloff = 8f, fade_start = s.FadeStart, fade_end = s.FadeEnd, floor_reach = s.FloorReach, sand_bars = s.SandBars },
            ["shoreline"] = new { width = style.FoamWidth, strength = style.Foam },
            ["normal"] = new { amplitude = style.Wave, speed = style.Speed, scale = style.Scale },
            ["flow"] = new { direction = new[] { 1f, 0f }, strength = 0f },
            ["wake"] = new { boat = 1f, player = 1f, @object = 1f, damping = style.RippleDamping, foam = s.WakeFoam, length = s.WakeLength },
            ["lighting"] = new { reflection = 0f, sun_glint = style.Glint },
            ["clock"] = new { source = "elapsed", rate = 1f, offset = 0f, fixed_time = 0f },
            ["pixel"] = new { levels = style.Levels, grain = style.Grain },
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads the six numbers back from a guo/water@2 document; anything else in it is left to its own reader.</summary>
    public static WaterShallows Parse(byte[] bytes)
    {
        if (bytes.Length > StoreWater.MaxBytes) throw new InvalidDataException("Water draft exceeds 16 KB.");
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema)
                throw new InvalidDataException("Not a guo/water@2 document.");
            float[] v = Ranges.Select(r =>
            {
                if (!root.TryGetProperty(r.Group, out var group) || group.ValueKind != JsonValueKind.Object
                    || !group.TryGetProperty(r.Field, out var value) || value.ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException($"Water {r.Group}.{r.Field} is missing.");
                return (float)value.GetDouble();
            }).ToArray();
            return new WaterShallows(v[0], v[1], v[2], v[3], v[4], v[5]).Checked();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Water draft is not valid JSON: " + e.Message);
        }
    }

    public static void Save(StoreWater style, WaterShallows s, string path)
    {
        WaterAuthoring.CheckDestination(path);
        byte[] bytes = Serialize(style, s);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }

    public static WaterShallows Read(string path) => Parse(File.ReadAllBytes(path));
}
#endif
