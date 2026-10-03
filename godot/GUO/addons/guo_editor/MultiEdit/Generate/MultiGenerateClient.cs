#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>One component of a generated multi: item id, offsets from the multi's centre, whether it is drawn.</summary>
public readonly record struct GenComponent(int Item, int X, int Y, int Z, bool Visible);

/// <summary>What a generator answered (data_formats section 27): components, notes, problems, how long it took.</summary>
public sealed record GenResult(List<GenComponent> Components, List<string> Notes, List<string> Problems, double Ms, string Error, JsonObject Raw)
{
    public bool Ok => Error == null;
}

/// <summary>One style the generators know: its key and display name, what it can build.</summary>
public sealed record GenStyle(string Key, string Name, string Material, List<string> Roofs, List<string> Stairs);

/// <summary>
/// The editor's side of tools/multi (section 27): it keeps one `run.py serve` process and sends it a JSON
/// request per line, so a slider change regenerates in the time the generator takes (a few ms), not a Python
/// start. Like QueueClient it starts python with an argument list (no shell) and every call has a timeout.
/// Self-contained: the canvas tab wires the result in.
/// </summary>
public sealed class MultiGenerateClient : IDisposable
{
    private readonly string _script;
    private readonly string _python;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process _proc;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Extra style files (the user's, besides tools/multi/styles and build/multi/styles).</summary>
    public List<string> StyleFiles { get; } = new();

    public MultiGenerateClient(string repoRoot = null)
    {
        _script = Path.Combine(repoRoot ?? EditorData.RepoRoot, "tools", "multi", "run.py");
        _python = QueueClient.FindPython();
    }

    public bool Available => _python != null && File.Exists(_script);

    public string Why => _python == null ? "python was not found on PATH" : !File.Exists(_script) ? $"{_script} is missing" : null;

    private void Start()
    {
        if (_proc is { HasExited: false })
        {
            return;
        }

        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(_script);
        psi.ArgumentList.Add("serve");
        if (StyleFiles.Count > 0)
        {
            psi.ArgumentList.Add("--styles");
            foreach (string f in StyleFiles)
            {
                psi.ArgumentList.Add(f);
            }
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        _proc = Process.Start(psi);
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginErrorReadLine();
    }

    /// <summary>One request: op is house, autowall, roof, stairs, rotate, mirror, import, export or styles.</summary>
    public async Task<JsonObject> RequestAsync(string op, JsonObject args = null)
    {
        if (!Available)
        {
            return new JsonObject { ["error"] = Why };
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    Start();
                    JsonObject req = args != null ? (JsonObject)args.DeepClone() : new JsonObject();
                    req["op"] = op;
                    await _proc.StandardInput.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);
                    await _proc.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);
                    string line = await _proc.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (line != null)
                    {
                        return JsonNode.Parse(line) as JsonObject ?? new JsonObject { ["error"] = "the generator answered something that is not JSON" };
                    }
                }
                catch (OperationCanceledException)
                {
                    Kill();
                    return new JsonObject { ["error"] = $"tools/multi did not answer within {Timeout.TotalSeconds:0} s" };
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                }

                Kill();                                    // the process died: start a fresh one once
            }

            return new JsonObject { ["error"] = "the generator process stopped" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GenResult> GenerateAsync(string op, JsonObject args) => Parse(await RequestAsync(op, args).ConfigureAwait(false));

    public async Task<List<GenStyle>> StylesAsync()
    {
        JsonObject r = await RequestAsync("styles").ConfigureAwait(false);
        var list = new List<GenStyle>();
        if (r["styles"] is JsonArray arr)
        {
            foreach (JsonNode n in arr)
            {
                list.Add(new GenStyle((string)n["key"], (string)n["name"], (string)n["material"], Strings(n["roofs"]), Strings(n["stairs"])));
            }
        }

        return list;
    }

    public static GenResult Parse(JsonObject r)
    {
        var comps = new List<GenComponent>();
        if (r["components"] is JsonArray arr)
        {
            foreach (JsonNode n in arr)
            {
                var a = (JsonArray)n;
                comps.Add(new GenComponent((int)a[0], (int)a[1], (int)a[2], (int)a[3], a.Count < 5 || (int)a[4] != 0));
            }
        }

        return new GenResult(comps, Strings(r["notes"]), Strings(r["problems"]), r["ms"]?.GetValue<double>() ?? 0,
            r["error"]?.GetValue<string>(), r);
    }

    private static List<string> Strings(JsonNode n)
    {
        var l = new List<string>();
        if (n is JsonArray a)
        {
            foreach (JsonNode s in a)
            {
                l.Add((string)s);
            }
        }

        return l;
    }

    private void Kill()
    {
        try
        {
            _proc?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        _proc = null;
    }

    public void Dispose() => Kill();
}
#endif
