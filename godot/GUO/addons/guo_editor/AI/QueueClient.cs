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

/// <summary>One request in the agent queue, with the replies seen so far (data_formats section 21).</summary>
public sealed record QueueRequest(long Id, string To, string From, string Text, string Status, string Created, List<QueueReply> Replies);

/// <summary>One reply to a request.</summary>
public sealed record QueueReply(long Id, string From, string Text, string Created);

/// <summary>
/// The editor's side of tools/agent_queue (ADR-0028): it runs that tool's own commands, so there is
/// one writer of the SQLite file and one place its rules (limits, the secret tripwire, atomic taking)
/// live. No shell: python is started with an argument list. Every call has a timeout.
/// </summary>
public sealed class QueueClient
{
    private readonly string _script;
    private readonly string _python;
    private readonly CancellationTokenSource _life = new();

    /// <summary>The queue file; the tool's own default (UO_AGENT_QUEUE) when empty.</summary>
    public string Db { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    public QueueClient(string repoRoot = null, string db = null)
    {
        _script = Path.Combine(repoRoot ?? EditorData.RepoRoot, "tools", "agent_queue", "run.py");
        _python = FindPython();
        Db = string.IsNullOrWhiteSpace(db) ? EditorData.Setting("UO_AGENT_QUEUE", "") : db;
    }

    /// <summary>Whether python and the tool are there.</summary>
    public bool Available => _python != null && File.Exists(_script);

    public string Why => _python == null ? "python was not found on PATH" : !File.Exists(_script) ? $"{_script} is missing" : null;

    public static string FindPython()
    {
        return AgentCatalog.Resolve("python") ?? AgentCatalog.Resolve("python3") ?? AgentCatalog.Resolve("py");
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(IEnumerable<string> args, string stdin = null)
    {
        if (!Available)
        {
            return (-1, "", Why);
        }

        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(_script);
        if (!string.IsNullOrEmpty(Db))
        {
            psi.ArgumentList.Add("--db");
            psi.ArgumentList.Add(Db);
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        cts.CancelAfter(Timeout);
        using Process p = Process.Start(psi);
        try
        {
            if (stdin != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(stdin);
                await p.StandardInput.BaseStream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
                p.StandardInput.Close();
            }

            Task<string> o = p.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> e = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (p.ExitCode, await o.ConfigureAwait(false), await e.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return (-2, "", $"agent_queue did not answer within {Timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Stops everything in flight: the editor is closing or reloading.</summary>
    public void Shutdown() => _life.Cancel();

    /// <summary>Posts a request; the new id, or 0 with the reason (the tool refuses secrets and over-long text).</summary>
    public async Task<(long Id, string Error)> PostAsync(string to, string from, string text, IEnumerable<string> attach = null)
    {
        var args = new List<string> { "post", "--to", to, "--from", from };
        if (attach != null)
        {
            foreach (string a in attach)
            {
                args.Add("--attach");
                args.Add(a);
            }
        }

        args.Add("-");
        var (code, o, e) = await RunAsync(args, text).ConfigureAwait(false);
        return code == 0 && long.TryParse(o.Trim(), out long id) ? (id, null) : (0, (e.Length > 0 ? e : o).Trim());
    }

    /// <summary>The newest requests, oldest first.</summary>
    public async Task<(List<QueueRequest> Requests, string Error)> ListAsync(int limit = 30)
    {
        var (code, o, e) = await RunAsync(new[] { "list", "--json", "--limit", limit.ToString() }).ConfigureAwait(false);
        if (code != 0)
        {
            return (null, e.Trim());
        }

        var list = new List<QueueRequest>();
        foreach (string line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            list.Add(Parse(JsonNode.Parse(line)));
        }

        return (list, null);
    }

    /// <summary>One request with every reply so far.</summary>
    public async Task<(QueueRequest Request, string Error)> ShowAsync(long id)
    {
        var (code, o, e) = await RunAsync(new[] { "show", id.ToString() }).ConfigureAwait(false);
        return code == 0 ? (Parse(JsonNode.Parse(o)), null) : (null, e.Trim());
    }

    public async Task<string> CancelAsync(long id)
    {
        var (code, _, e) = await RunAsync(new[] { "cancel", id.ToString() }).ConfigureAwait(false);
        return code == 0 ? null : e.Trim();
    }

    private static QueueRequest Parse(JsonNode j)
    {
        var replies = new List<QueueReply>();
        if (j?["replies"] is JsonArray rs)
        {
            foreach (JsonNode r in rs)
            {
                replies.Add(new QueueReply((long)r["id"], (string)r["from"] ?? "", (string)r["text"] ?? "", (string)r["created"] ?? ""));
            }
        }

        return new QueueRequest((long)j["id"], (string)j["to"] ?? "", (string)j["from"] ?? "", (string)j["text"] ?? "",
            (string)j["status"] ?? "", (string)j["created"] ?? "", replies);
    }
}
#endif
