#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// An Agent Client Protocol client (ADR-0028): JSON-RPC 2.0 over the agent
/// process's standard input and output, one JSON message per line. It starts
/// the agent CLI (no shell: <see cref="ProcessStartInfo.ArgumentList"/>),
/// matches replies to requests, hands <c>session/update</c> notifications to
/// <see cref="Update"/> and <c>session/request_permission</c> to
/// <see cref="PermissionHandler"/>. It touches no Godot type, so it runs on
/// any thread; the events fire on a worker thread and the UI marshals them.
/// </summary>
/// <remarks>
/// The CLI logs in with its own account: this class never sees a token, and
/// it advertises no file system or terminal capability, so an agent asks
/// before it can do either.
/// </remarks>
public sealed class AcpClient : IDisposable
{
    /// <summary>The protocol version this client speaks.</summary>
    public const int ProtocolVersion = 1;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _life = new();
    private Process _process;
    private long _nextId;
    private int _disposed;

    /// <summary>A <c>session/update</c> notification's params (worker thread).</summary>
    public event Action<JsonNode> Update;

    /// <summary>A line the agent wrote to standard error (worker thread).</summary>
    public event Action<string> Log;

    /// <summary>The agent process ended (worker thread). Argument: a short reason.</summary>
    public event Action<string> Closed;

    /// <summary>
    /// Answers <c>session/request_permission</c>: given the request's params, returns the
    /// response <c>result</c> (<c>{"outcome":{"outcome":"selected","optionId":..}}</c> or cancelled).
    /// Null means every request is cancelled.
    /// </summary>
    public Func<JsonNode, Task<JsonNode>> PermissionHandler { get; set; }

    /// <summary>What <c>initialize</c> answered (agent capabilities, auth methods), or null.</summary>
    public JsonNode AgentInfo { get; private set; }

    public string SessionId { get; private set; }

    public bool Running => _process != null && !_process.HasExited && _disposed == 0;

    public int ProcessId => _process?.Id ?? 0;

    /// <summary>Every raw line sent (&gt;) and received (&lt;), for tests and the log.</summary>
    public ConcurrentQueue<string> Wire { get; } = new();

    /// <summary>Starts the agent. False, with the reason, if it cannot be started.</summary>
    public bool Start(string exe, IReadOnlyList<string> args, string workingDirectory, out string error)
    {
        error = null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (_process == null)
        {
            error = "the process did not start";
            return false;
        }

        Process p = _process;
        _ = Task.Run(() => ReadLoop(p));
        _ = Task.Run(() => ErrorLoop(p));
        return true;
    }

    private async Task ReadLoop(Process p)
    {
        string reason = "the agent exited";
        try
        {
            string line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Wire.Enqueue("< " + line);
                JsonNode msg;
                try
                {
                    msg = JsonNode.Parse(line);
                }
                catch (Exception)
                {
                    // Agents sometimes print a banner or a stray log line on stdout; not a frame.
                    Log?.Invoke("(not JSON on stdout) " + line);
                    continue;
                }

                if (msg is JsonObject obj)
                {
                    // Updates are handled in order; a permission request may wait for a person,
                    // so it must not hold up the lines behind it.
                    if ((string)obj["method"] == "session/request_permission")
                    {
                        _ = Task.Run(() => Dispatch(obj));
                    }
                    else
                    {
                        await Dispatch(obj).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        finally
        {
            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new IOException("the agent closed the connection"));
            }

            _pending.Clear();
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
            {
                Closed?.Invoke(reason);
            }
        }
    }

    private async Task ErrorLoop(Process p)
    {
        try
        {
            string line;
            while ((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Log?.Invoke(line);
            }
        }
        catch (Exception)
        {
            // The pipe closed with the process.
        }
    }

    private async Task Dispatch(JsonObject msg)
    {
        string method = (string)msg["method"];
        JsonNode id = msg["id"];

        if (method == null)
        {
            // A response to one of our requests.
            if (id != null && _pending.TryRemove((long)id, out var tcs))
            {
                if (msg["error"] is JsonNode err)
                {
                    tcs.TrySetException(new AcpException((int?)err["code"] ?? 0, (string)err["message"] ?? "error", err["data"]));
                }
                else
                {
                    tcs.TrySetResult(msg["result"]);
                }
            }

            return;
        }

        if (id == null)
        {
            if (method == "session/update" && msg["params"] != null)
            {
                try
                {
                    Update?.Invoke(msg["params"]);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"an update handler threw: {ex.Message}");
                }
            }

            return;
        }

        // A request from the agent: it expects an answer.
        try
        {
            if (method == "session/request_permission")
            {
                JsonNode result = PermissionHandler != null
                    ? await PermissionHandler(msg["params"]).ConfigureAwait(false)
                    : null;
                result ??= new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
            }
            else
            {
                // No fs/* and no terminal/*: the client advertised neither.
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not supported: {method}" },
                });
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"answering {method} failed: {ex.Message}");
        }
    }

    private void Send(JsonObject msg)
    {
        string line = msg.ToJsonString();
        Wire.Enqueue("> " + line);
        lock (_writeLock)
        {
            if (_process == null || _process.HasExited)
            {
                throw new IOException("the agent is not running");
            }

            _process.StandardInput.Write(line + "\n");
            _process.StandardInput.Flush();
        }
    }

    /// <summary>Sends a request and waits for its result; times out instead of hanging.</summary>
    public async Task<JsonNode> RequestAsync(string method, JsonNode parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct);
        timer.CancelAfter(timeout);
        using (timer.Token.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                _pending.TryRemove(id, out _);
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                throw new TimeoutException($"{method}: no answer within {timeout.TotalSeconds:0} s");
            }
        }
    }

    public void Notify(string method, JsonNode parameters) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    /// <summary><c>initialize</c>: no client file system, no terminal.</summary>
    public async Task<JsonNode> InitializeAsync(TimeSpan timeout)
    {
        AgentInfo = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject { ["name"] = "guo-editor", ["title"] = "GUO editor", ["version"] = "1" },
        }, timeout).ConfigureAwait(false);
        return AgentInfo;
    }

    /// <summary><c>authenticate</c> with one of the methods <c>initialize</c> listed.</summary>
    public Task<JsonNode> AuthenticateAsync(string methodId, TimeSpan timeout) =>
        RequestAsync("authenticate", new JsonObject { ["methodId"] = methodId }, timeout);

    /// <summary><c>session/new</c> in a folder; no MCP servers.</summary>
    public async Task<string> NewSessionAsync(string cwd, TimeSpan timeout)
    {
        JsonNode r = await RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, timeout).ConfigureAwait(false);
        SessionId = (string)r?["sessionId"] ?? throw new InvalidDataException("session/new returned no sessionId");
        return SessionId;
    }

    /// <summary><c>session/prompt</c>: returns the stop reason once the turn is over; updates stream meanwhile.</summary>
    public async Task<string> PromptAsync(string text, TimeSpan timeout, CancellationToken ct = default)
    {
        JsonNode r = await RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = SessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }, timeout, ct).ConfigureAwait(false);
        return (string)r?["stopReason"] ?? "unknown";
    }

    /// <summary><c>session/cancel</c>: asks the agent to stop the turn.</summary>
    public void Cancel()
    {
        if (SessionId != null && Running)
        {
            try
            {
                Notify("session/cancel", new JsonObject { ["sessionId"] = SessionId });
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _life.Cancel();
        Process p = _process;
        if (p == null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    /// <summary>The text of a content block (<c>{"type":"text","text":..}</c>), or "".</summary>
    public static string TextOf(JsonNode content) =>
        content is JsonObject o && (string)o["type"] == "text" ? (string)o["text"] ?? "" : "";
}

/// <summary>A JSON-RPC error answer.</summary>
public sealed class AcpException : Exception
{
    public int Code { get; }
    public JsonNode Data { get; }

    public AcpException(int code, string message, JsonNode data) : base(message)
    {
        Code = code;
        Data = data;
    }
}
#endif
