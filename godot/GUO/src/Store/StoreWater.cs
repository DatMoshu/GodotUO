// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GUO.Store;

/// <summary>
/// The guo/water@1 style file a <c>water</c> component names (ADR-0037
/// decision 6): a built-in shading model and the numbers it reads, no code.
/// tools/asset_store/water_style.py applies the same rules and the corpus holds both
/// to them. No Godot types, so the headless StoreSmoke compiles it too.
/// </summary>
internal sealed record StoreWater(
    string Name, string Model, float[] Tint, float Speed, float Scale, float Wave, float Foam,
    float FoamWidth, float Glint, int Levels, int Grain, float RippleStrength, float RippleDamping)
{
    public const string Schema = "guo/water@1";
    public const int MaxBytes = 16384;
    public static readonly string[] Models = { "uo", "pixel", "cel", "realistic", "classicplus" };

    private static readonly HashSet<string> Fields = new(
        "schema name model tint ripples speed scale wave foam foam_width glint levels grain".Split(' '), StringComparer.Ordinal);

    /// <summary>Parses and checks one style file; a bad one throws InvalidDataException.</summary>
    public static StoreWater Parse(byte[] bytes)
    {
        StorePack.Require(bytes.Length <= MaxBytes, "Water style too large");
        StorePack.Require(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "Water style has a byte order mark");
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Water style is not UTF-8");
        }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            StorePack.UniqueJson(doc.RootElement);
            var root = doc.RootElement;
            StorePack.Require(root.ValueKind == JsonValueKind.Object, "Water style must be an object");
            var names = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            StorePack.Require(names.SetEquals(Fields), "Water style fields");
            var schema = root.GetProperty("schema");
            StorePack.Require(schema.ValueKind == JsonValueKind.String && schema.GetString() == Schema, "Unsupported water schema");
            var nameElement = root.GetProperty("name");
            StorePack.Require(nameElement.ValueKind == JsonValueKind.String, "Invalid water name");
            string name = nameElement.GetString();
            StorePack.Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 48 && !name.Any(c => c < 32 || c == 127), "Invalid water name");
            var model = root.GetProperty("model");
            StorePack.Require(model.ValueKind == JsonValueKind.String && Models.Contains(model.GetString()), "Unknown water model");
            var tint = root.GetProperty("tint");
            StorePack.Require(tint.ValueKind == JsonValueKind.Array && tint.GetArrayLength() == 3, "Water tint is three numbers");
            float[] rgb = tint.EnumerateArray().Select(v => Number(v, 0, 1, "tint")).ToArray();
            var ripples = root.GetProperty("ripples");
            StorePack.Require(ripples.ValueKind == JsonValueKind.Object
                && ripples.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "strength", "damping" }), "Water ripples fields");
            return new StoreWater(name, model.GetString(), rgb,
                Number(root.GetProperty("speed"), 0, 4, "speed"),
                Number(root.GetProperty("scale"), 0.005, 0.25, "scale"),
                Number(root.GetProperty("wave"), 0, 1, "wave"),
                Number(root.GetProperty("foam"), 0, 1, "foam"),
                Number(root.GetProperty("foam_width"), 0, 0.5, "foam_width"),
                Number(root.GetProperty("glint"), 0, 1, "glint"),
                Integer(root.GetProperty("levels"), 0, 16, "levels"),
                Integer(root.GetProperty("grain"), 1, 8, "grain"),
                Number(ripples.GetProperty("strength"), 0, 2, "ripples strength"),
                Number(ripples.GetProperty("damping"), 0.9, 0.999, "ripples damping"));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Water style is not valid JSON: " + e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("Water style has a value of the wrong type: " + e.Message);
        }
    }

    private static float Number(JsonElement value, double low, double high, string what)
    {
        StorePack.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && d >= low && d <= high,
            $"Water {what} out of range");
        return (float)value.GetDouble();
    }

    private static int Integer(JsonElement value, int low, int high, string what)
    {
        int i = 0;
        StorePack.Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out i) && i >= low && i <= high,
            $"Water {what} out of range");
        return i;
    }
}
