// SPDX-License-Identifier: BSD-2-Clause
// GUO benchmark diagnostics only. Strict JSON; invalid timing is never valid zero.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GUO.Host;

internal static class PerfResultJson
{
    internal static string SampleIssue(int count, double elapsed, double mean) =>
        count <= 0 ? "no_samples" : !double.IsFinite(elapsed) || elapsed <= 0 ? "invalid_elapsed_ms"
        : !double.IsFinite(mean) || mean <= 0 ? "invalid_mean_frame_ms" : null;

    // Reporting is outside the measured window. Two serializations distinguish
    // actual nonfinite numbers from legitimate text such as the string Infinity.
    internal static string Serialize(object value, JsonSerializerOptions options)
    {
        var permissive = new JsonSerializerOptions(options) {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var strict = new JsonSerializerOptions(options);
        strict.Converters.Add(new FiniteDouble());
        strict.Converters.Add(new FiniteFloat());
        JsonNode named = JsonSerializer.SerializeToNode(value, permissive);
        JsonNode safe = JsonSerializer.SerializeToNode(value, strict);
        var issues = new List<Dictionary<string,string>>();
        Collect(named, safe, "$", issues);
        var report = (JsonObject)safe;
        report["invalid_metrics"] = JsonSerializer.SerializeToNode(issues);
        report["numeric_metrics_valid"] = issues.Count == 0;
        return report.ToJsonString(options);
    }

    internal static void WriteAtomic(string path, string json)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool NumbersFinite(object value)
    {
        using var report = JsonDocument.Parse(Serialize(value, new JsonSerializerOptions()));
        return report.RootElement.GetProperty("numeric_metrics_valid").GetBoolean();
    }

    private static void Collect(JsonNode original, JsonNode safe, string path,
        List<Dictionary<string,string>> issues)
    {
        if (safe == null && original is JsonValue v && v.TryGetValue<string>(out string label)
            && label is "NaN" or "Infinity" or "-Infinity") {
            issues.Add(new() { ["path"] = path, ["reason"] = "nonfinite:" + label });
            return;
        }
        if (original is JsonObject obj && safe is JsonObject other)
            foreach (var pair in obj)
                Collect(pair.Value, other[pair.Key], path + "[" + JsonSerializer.Serialize(pair.Key) + "]", issues);
        else if (original is JsonArray array && safe is JsonArray otherArray)
            for (int i = 0; i < array.Count; i++) Collect(array[i], otherArray[i], path + "[" + i + "]", issues);
    }

    private sealed class FiniteDouble : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) {
            if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue();
        }
    }
    private sealed class FiniteFloat : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetSingle();
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) {
            if (float.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue();
        }
    }
}
