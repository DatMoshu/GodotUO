#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The smoke stage for the Map Generator tab (ADR-0030). It runs tools/mapgen the way the tab does
/// and checks five things. The schema lists the passes. Two 256x256 runs with one seed hash the
/// same. Switching River Carve off changes the hash. Exporting a run writes MUL files that read back
/// with no mismatch, plus a world project. The tab builds one group per pass from the schema.
/// Every folder it writes is under the smoke's own output folder.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _mapgenReport = new();
    private readonly Stopwatch _mapgenClock = new();
    private Task _mapgenTask;
    private JsonElement? _mapgenSchema;

    private void MapGenCheck(string name, bool ok, string detail = "")
    {
        _mapgenReport[name] = ok;
        if (!ok)
        {
            _mapgenReport["ok"] = false;
            _failures.Add($"MapGen: {name} {detail}".Trim());
            GD.Print($"[GUO editor] smoke MapGen FAIL: {name} {detail}");
        }
    }

    /// <summary>One tick of the stage; true when it is finished.</summary>
    private bool StepMapGen()
    {
        if (System.Environment.GetEnvironmentVariable("GUO_MAPGEN_SKIP") != null)
        {
            return true;
        }

        if (_mapgenTask == null)
        {
            _mapgenReport["ok"] = true;
            _mapgenTask = RunMapGenAsync();
            _mapgenClock.Restart();
        }

        if (_mapgenTask.IsCompleted)
        {
            if (_mapgenTask.IsFaulted)
            {
                MapGenCheck("threw", false, $"{_mapgenTask.Exception?.GetBaseException().GetType().Name}: {_mapgenTask.Exception?.GetBaseException().Message}");
            }
            else if (_mapgenSchema is JsonElement schema && GuoEditorPlugin.MapGenMain is MapGenView view)
            {
                // The tab, on the main thread: one group per runnable pass, one control per setting.
                view.LoadSchema(schema);
                int runnable = schema.GetProperty("passes").EnumerateArray().Count(p => !p.GetProperty("file_side_effects").GetBoolean());
                _mapgenReport["tab_groups"] = view.PassGroupCount;
                _mapgenReport["tab_controls"] = view.TunableControlCount;
                MapGenCheck("tab_groups_match", view.PassGroupCount == runnable && view.PassGroupCount > 0, $"{view.PassGroupCount} groups for {runnable} passes");
                List<string> args = view.BuildRunArgs("x");
                MapGenCheck("tab_args_preset_only", !args.Contains("--set") && !args.Contains("--disable"), string.Join(' ', args));
            }
            else
            {
                MapGenCheck("tab_present", false, "no schema or no Map Generator view");
            }

            _report["mapgen"] = _mapgenReport;
            return true;
        }

        if (_mapgenClock.Elapsed.TotalSeconds > 300)
        {
            MapGenCheck("finished_in_time", false, "the stage did not finish within 300 s");
            _report["mapgen"] = _mapgenReport;
            return true;
        }

        return false;
    }

    private async Task RunMapGenAsync()
    {
        string root = Path.Combine(Path.GetFullPath(_out), "mapgen" + Suffix);
        Directory.CreateDirectory(root);

        (JsonElement? schema, string error) = await MapGenCli.QueryAsync(new[] { "schema" });
        MapGenCheck("schema", schema != null, error);
        if (schema is not JsonElement s)
        {
            return;
        }

        _mapgenSchema = s;
        int passes = s.GetProperty("passes").GetArrayLength();
        int tunables = s.GetProperty("passes").EnumerateArray().Sum(p => p.GetProperty("tunables").GetArrayLength());
        _mapgenReport["passes"] = passes;
        _mapgenReport["tunables"] = tunables;
        _mapgenReport["preset"] = s.GetProperty("preset").GetProperty("id").GetString();
        MapGenCheck("schema_counts", passes >= 30 && tunables >= 200, $"{passes} passes, {tunables} tunables");

        var common = new[] { "--size", "256", "--seed", "42" };
        string a = Path.Combine(root, "run-a"), b = Path.Combine(root, "run-b"), c = Path.Combine(root, "run-c");
        string hashA = await RunOnce(a, common);
        string hashB = await RunOnce(b, common);
        string hashC = await RunOnce(c, common.Concat(new[] { "--disable", "River Carve" }).ToArray());
        _mapgenReport["hash"] = hashA;
        _mapgenReport["hash_no_rivers"] = hashC;
        MapGenCheck("deterministic", hashA != null && hashA == hashB, $"{hashA} vs {hashB}");
        MapGenCheck("toggle_changes_hash", hashC != null && hashC != hashA, $"{hashC}");
        MapGenCheck("previews_written", File.Exists(Path.Combine(a, "radar.png")) && Directory.GetFiles(Path.Combine(a, "steps"), "*.png").Length > 0);

        string world = Path.Combine(root, "world-a");
        JsonElement? done = null;
        int code = await MapGenCli.RunAsync(new[] { "export", "--run", a, "--world-project", world },
            j => { if (j.TryGetProperty("event", out JsonElement e) && e.GetString() == "done") done = j; }, _ => { }, default);
        bool ok = code == 0 && done is JsonElement d && d.GetProperty("ok").GetBoolean();
        MapGenCheck("export_verified", ok, $"exit {code}");
        if (ok)
        {
            JsonElement v = done.Value.GetProperty("verify");
            _mapgenReport["export_cells"] = v.GetProperty("land_cells_checked").GetInt32();
            _mapgenReport["export_statics"] = v.GetProperty("statics_found").GetInt32();
            _mapgenReport["world_blocks"] = Directory.GetFiles(Path.Combine(world, "blocks", "0"), "*.json").Length;
            MapGenCheck("export_mismatches_zero", v.GetProperty("land_mismatches").GetInt32() == 0 && v.GetProperty("static_mismatches").GetInt32() == 0);
            MapGenCheck("world_project_blocks", (int)_mapgenReport["world_blocks"] == 32 * 32, $"{_mapgenReport["world_blocks"]} blocks");
        }
    }

    private static async Task<string> RunOnce(string outDir, string[] extra)
    {
        string hash = null;
        var args = new List<string> { "run", "--out", outDir, "--step-previews" };
        args.AddRange(extra);
        int code = await MapGenCli.RunAsync(args,
            j => { if (j.TryGetProperty("event", out JsonElement e) && e.GetString() == "done") hash = j.GetProperty("hash").GetString(); }, _ => { }, default);
        return code == 0 ? hash : null;
    }
}
#endif
