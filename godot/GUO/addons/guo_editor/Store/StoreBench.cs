#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GUO.Configuration;
using GUO.Store;

/// <summary>
/// What the UO Store tab shares (ADR-0026 section 8): the editor's own pack store, its catalogue
/// trust list, and the Python tools it shells out to. Not a Godot type, so a hot reload has
/// nothing of it to serialize; <see cref="StoreView"/> owns one and calls <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// The store is a project-local folder under build/ (gitignored): never the game's user://store,
/// never the client install. It reuses the client's <see cref="StoreClient"/> and
/// <see cref="StoreTrust"/> unchanged, so approval, signatures, hashes and refusal are the
/// client's own code.
/// </remarks>
internal sealed class StoreBench : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Process> _children = new();
    private StoreTrust _trust;

    /// <summary>Where the editor's installed packs live.</summary>
    public string Root { get; }

    /// <summary>Hides the official catalogue from the lists (the smoke check must not reach the internet).</summary>
    public bool SkipOfficial { get; set; }

    public CancellationTokenSource Cancel { get; } = new();

    public StoreBench(string root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(EditorData.RepoRoot, "build", "editor_store"));
    }

    public StoreTrust Trust => _trust ??= new StoreTrust(Path.Combine(Root, ".catalogues.json"));

    public string ProfilesPath => Path.Combine(EditorData.RepoRoot, "build", "editor_servers", "profiles.json");

    public string DeployRoot => Path.Combine(Root, "deployments");

    public StoreClient NewClient(string url) =>
        new(url, Root, PlatformDefaults.CurrentVersion) { Trust = Trust };

    /// <summary>The catalogues to list from: the official one first (unless skipped), then the added ones.</summary>
    public List<StoreCatalogueRecord> Sources() =>
        Trust.Catalogues().Where(r => !SkipOfficial || !r.Official).ToList();

    // ---- where each installed pack came from (needed again to deploy it to a shard) -----------------

    private string SourcesPath => Path.Combine(Root, ".sources.json");

    public void RememberSource(string id, string version, string catalogueUrl)
    {
        lock (_gate)
        {
            JsonObject book = ReadBook();
            book[id + "@" + version] = catalogueUrl;
            Directory.CreateDirectory(Root);
            File.WriteAllText(SourcesPath, book.ToJsonString());
        }
    }

    public string SourceOf(string id, string version)
    {
        lock (_gate)
        {
            return ReadBook()[id + "@" + version]?.GetValue<string>();
        }
    }

    private JsonObject ReadBook()
    {
        try
        {
            return File.Exists(SourcesPath) ? JsonNode.Parse(File.ReadAllText(SourcesPath)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }

    // ---- the Python tools ------------------------------------------------------------------------------

    public static string Tool(params string[] relative) =>
        Path.Combine(new[] { EditorData.RepoRoot, "tools" }.Concat(relative).ToArray());

    /// <summary>Runs a tool script and returns its exit code and combined output. Kills it on timeout or Dispose.</summary>
    public async Task<(int Code, string Text)> RunPythonAsync(string script, IEnumerable<string> args, Action<string> line = null, int timeoutSeconds = 900)
    {
        string python = QueueClient.FindPython();
        if (python == null)
        {
            return (-1, "python was not found on PATH");
        }

        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = EditorData.RepoRoot,
        };
        psi.ArgumentList.Add(script);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // Tools never see a signing key the editor did not set up for them: it does not handle keys.
        psi.Environment.Remove("UO_STORE_SIGNING_KEY");
        var text = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        void Take(string s)
        {
            if (s == null)
            {
                return;
            }

            lock (text)
            {
                text.AppendLine(s);
            }

            line?.Invoke(s);
        }

        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            return (-1, $"could not start {python}: {e.Message}");
        }

        lock (_gate)
        {
            _children.Add(process);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.WaitForExit(); // flush the async readers
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return (-1, text + "\n(stopped)");
        }
        finally
        {
            lock (_gate)
            {
                _children.Remove(process);
            }
        }

        return (process.ExitCode, text.ToString().TrimEnd());
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public Task<(int Code, string Text)> StoreToolAsync(IEnumerable<string> args, Action<string> line = null) =>
        RunPythonAsync(Tool("asset_store", "run.py"), args, line);

    public Task<(int Code, string Text)> ShardToolAsync(IEnumerable<string> args, Action<string> line = null) =>
        RunPythonAsync(Tool("shard_content", "run.py"), args, line);

    /// <summary>Re-inspects an installed pack with the publisher's own checks; null when python is missing.</summary>
    public async Task<JsonNode> InspectInstalledAsync(string id, string version)
    {
        string folder = Path.Combine(Root, id, version);
        var (code, text) = await StoreToolAsync(new[] { "check", "--json", folder }).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(text.Split('\n').Last(l => l.TrimStart().StartsWith('{')));
        }
        catch (Exception)
        {
            return code == -1 ? null : new JsonObject { ["ok"] = false, ["error"] = text };
        }
    }

    public void Dispose()
    {
        Cancel.Cancel();
        List<Process> kill;
        lock (_gate)
        {
            kill = _children.ToList();
            _children.Clear();
        }

        foreach (Process p in kill)
        {
            Kill(p);
        }
    }

    /// <summary>The verdict as one line, for lists and the smoke report.</summary>
    public static string VerdictLine(JsonNode report)
    {
        if (report == null)
        {
            return "not checked (python is missing)";
        }

        if (report["ok"]?.GetValue<bool>() != true)
        {
            return "does not verify: " + report["error"];
        }

        return (string)report["policy"]?["verdict"] ?? "unknown";
    }
}
#endif
