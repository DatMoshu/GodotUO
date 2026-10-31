using System.Text.Json;
using GUO.Host;

static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
var options = new JsonSerializerOptions { WriteIndented = true };
string json = PerfResultJson.Serialize(new { scenes = new[] { new { p50_ms = 1.25, fps = 800.0, text = "Infinity" } } }, options);
using (var doc = JsonDocument.Parse(json)) {
 var root = doc.RootElement;
 Check(root.GetProperty("scenes")[0].GetProperty("p50_ms").GetDouble() == 1.25, "finite measurement unchanged");
 Check(root.GetProperty("scenes")[0].GetProperty("text").GetString() == "Infinity", "literal Infinity text preserved");
 Check(root.GetProperty("invalid_metrics").GetArrayLength() == 0, "finite result has no false invalid entries");
}
json = PerfResultJson.Serialize(new { scenes = new[] { new { positive = double.PositiveInfinity, negative = double.NegativeInfinity, nan = double.NaN, gpu = float.PositiveInfinity } } }, options);
using (var doc = JsonDocument.Parse(json)) {
 var root = doc.RootElement;
 Check(root.GetProperty("scenes")[0].GetProperty("positive").ValueKind == JsonValueKind.Null, "invalid number is null not zero/string");
 Check(root.GetProperty("invalid_metrics").GetArrayLength() == 4, "every nonfinite metric retained as issue");
 Check(root.GetProperty("invalid_metrics")[0].GetProperty("path").GetString() == "$[\"scenes\"][0][\"positive\"]", "nested metric path identifies source");
 Check(!root.GetProperty("numeric_metrics_valid").GetBoolean(), "nonfinite report cannot pass numeric qualification");
}
Check(PerfResultJson.SampleIssue(0, 0, double.NaN) == "no_samples", "no samples explicitly invalid");
Check(PerfResultJson.SampleIssue(1, 0, 1) == "invalid_elapsed_ms", "zero elapsed invalid");
Check(PerfResultJson.SampleIssue(1, 10, 0) == "invalid_mean_frame_ms", "zero mean invalid");
Check(PerfResultJson.SampleIssue(2, 10, 5) == null, "finite positive sample accepted");
Check(!PerfResultJson.NumbersFinite(new { optional = new { gpu = float.NaN } }), "nested optional nonfinite metric invalidates measurement");
Console.WriteLine("finite-result regression PASS");
