#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs the map generator CLI (tools/mapgen, ADR-0030) as a process: <c>python tools/mapgen/run.py</c>
/// builds <c>guo-mapgen</c> on first use and passes it the config's client data and generator data
/// folder. Every stdout line is one JSON value (docs/data_formats.md §24); callbacks run on the
/// reader thread, so a UI marshals them itself.
/// </summary>
public static class MapGenCli
{
    /// <summary>Where the tab's runs go: build/mapgen/runs, never the install.</summary>
    public static string RunsRoot => Path.Combine(EditorData.RepoRoot, "build", "mapgen", "runs");

    /// <summary>The user's own presets: &lt;UO_MAPGEN_DATA&gt;/presets.</summary>
    public static string UserPresetsDir => Path.Combine(DataDir, "presets");

    public static string DataDir
    {
        get
        {
            string dir = Environment.ExpandEnvironmentVariables(EditorData.Setting("UO_MAPGEN_DATA", ""));
            return dir.Length > 0 && !dir.Contains('%')
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GUO", "mapgen");
        }
    }

    /// <summary>A fresh folder name for a run: time, preset and seed.</summary>
    public static string NewRunDir(string preset, long seed)
    {
        string name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Slug(Path.GetFileNameWithoutExtension(preset).Replace(".preset", ""))}-{seed}";
        string dir = Path.Combine(RunsRoot, name);
        for (int i = 2; Directory.Exists(dir); i++)
        {
            dir = Path.Combine(RunsRoot, $"{name}-{i}");
        }

        return dir;
    }

    /// <summary>A fresh world project folder beside the editor's own (UO_WORLD_PROJECT's parent).</summary>
    public static string NewWorldProjectDir(string runDir)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(EditorData.ProjectRoot())) ?? Path.Combine(EditorData.RepoRoot, "build", "world");
        string name = "generated-" + Path.GetFileName(runDir.TrimEnd('\\', '/'));
        string dir = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(dir); i++)
        {
            dir = Path.Combine(parent, $"{name}-{i}");
        }

        return dir;
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>Runs one command to completion. <paramref name="onJson"/> gets each stdout line parsed; stderr goes to <paramref name="onLog"/>.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, Action<JsonElement> onJson, Action<string> onLog, CancellationToken cancel)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = EditorData.RepoRoot,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.ArgumentList.Add(Path.Combine(EditorData.RepoRoot, "tools", "mapgen", "run.py"));
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(e.Data);
                    onJson(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    onLog(e.Data);
                }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    onLog(e.Data);
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (cancel.Register(() =>
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }))
            {
                p.WaitForExit();
            }

            return cancel.IsCancellationRequested ? -1 : p.ExitCode;
        }, CancellationToken.None);
    }

    /// <summary>Runs a command that prints one JSON object (schema, presets) and returns it.</summary>
    public static async Task<(JsonElement? Value, string Error)> QueryAsync(IReadOnlyList<string> args, CancellationToken cancel = default)
    {
        JsonElement? value = null;
        string error = "";
        var log = new StringBuilder();
        int code = await RunAsync(args, j =>
        {
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("event", out JsonElement ev) && ev.GetString() == "error")
            {
                error = j.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "error";
            }
            else
            {
                value = j;
            }
        }, l => log.AppendLine(l), cancel);
        if (code != 0 && error.Length == 0)
        {
            error = $"guo-mapgen exited {code}: {Tail(log.ToString())}";
        }

        return (code == 0 ? value : null, error);
    }

    private static string Tail(string s) => s.Length > 400 ? s[^400..] : s;
}
#endif
