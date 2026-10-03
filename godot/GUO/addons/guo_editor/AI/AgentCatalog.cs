#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>One agent the dock knows how to start, and what to tell the user when it is missing.</summary>
public sealed record AgentPreset(string Id, string Name, string Command, string[] Args, string Install, string Login, string Paid)
{
    public bool IsCustom => Command == "";

    /// <summary>The full path of the command on this machine, or null if it is not installed.</summary>
    public string Find() => IsCustom ? null : AgentCatalog.Resolve(Command);
}

/// <summary>
/// The agents the AI dock can start over ACP (ADR-0028). They are detected on PATH at run time;
/// a missing one is not installed for the user, the dock shows the exact commands to run themselves.
/// Each CLI signs in with its own account, so GUO holds no token.
/// </summary>
public static class AgentCatalog
{
    public static readonly AgentPreset OpenCode = new("opencode", "OpenCode", "opencode", new[] { "acp" },
        "npm i -g opencode-ai",
        "opencode auth login   (choose OpenAI, then ChatGPT Plus/Pro; or run opencode and type /connect)",
        "your ChatGPT plan, or whichever provider you connect");

    public static readonly AgentPreset Codex = new("codex", "Codex (ChatGPT plan)", "codex-acp", Array.Empty<string>(),
        "npm i -g @agentclientprotocol/codex-acp   (older name: @zed-industries/codex-acp)",
        "the adapter offers ChatGPT browser sign-in when it starts (initialize lists the auth methods); or codex login after npm i -g @openai/codex",
        "your ChatGPT plan");

    public static readonly AgentPreset ClaudeCode = new("claude", "Claude Code", "claude-agent-acp", Array.Empty<string>(),
        "npm i -g @agentclientprotocol/claude-agent-acp   (older name: @zed-industries/claude-code-acp, binary claude-code-acp)",
        "claude   then /login   (the adapter uses the Claude Code login)",
        "your Claude plan");

    public static readonly AgentPreset Gemini = new("gemini", "Gemini CLI", "gemini", new[] { "--experimental-acp" },
        "npm i -g @google/gemini-cli",
        "gemini   (sign in with Google once; newer versions use --acp instead of --experimental-acp)",
        "your Google account");

    public static readonly AgentPreset Custom = new("custom", "Custom command", "", Array.Empty<string>(),
        "type the command line, for example: my-agent --acp",
        "whatever that CLI needs",
        "whatever that CLI uses");

    /// <summary>The presets in the order the picker shows them.</summary>
    public static IReadOnlyList<AgentPreset> Presets { get; } = new[] { OpenCode, Codex, ClaudeCode, Gemini, Custom };

    /// <summary>
    /// The executable for a command name, searching PATH the way the shell does but without one:
    /// on Windows the .exe, .cmd and .bat forms (npm installs shims), elsewhere the bare name.
    /// A path with a directory in it is taken as given. Null if not found.
    /// </summary>
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string[] exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
        if (command.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            if (File.Exists(command))
            {
                return Path.GetFullPath(command);
            }

            foreach (string ext in exts)
            {
                if (File.Exists(command + ext))
                {
                    return Path.GetFullPath(command + ext);
                }
            }

            return null;
        }

        string path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), command + ext);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }

        return null;
    }

    /// <summary>Splits a command line into words; double quotes group, nothing else is special (no shell runs it).</summary>
    public static List<string> SplitCommandLine(string line)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line ?? "")
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || cur.Length > 0)
                {
                    words.Add(cur.ToString());
                    cur.Clear();
                    any = false;
                }
            }
            else
            {
                cur.Append(c);
            }
        }

        if (any || cur.Length > 0)
        {
            words.Add(cur.ToString());
        }

        return words;
    }

    /// <summary>A line per preset: found (where) or missing (what to run).</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        foreach (AgentPreset p in Presets)
        {
            if (p.IsCustom)
            {
                continue;
            }

            string found = p.Find();
            sb.AppendLine(found != null ? $"{p.Name}: found at {found}" : $"{p.Name}: not installed. Install: {p.Install}   Sign in: {p.Login}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// A running agent: its ACP connection and session, and a transcript event the dock draws. The
/// session's first prompt may spend the user's plan, which is why starting one never prompts.
/// </summary>
public sealed class AgentSession : IDisposable
{
    /// <summary>What the transcript shows. Kind: message, thought, tool, plan, info, error, user.</summary>
    public readonly record struct Line(string Kind, string Text);

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(15);

    private readonly AcpClient _client = new();
    private readonly Dictionary<string, string> _tools = new();
    private CancellationTokenSource _turn;

    public string Name { get; }
    public AgentPreset Preset { get; }
    public AcpClient Client => _client;
    public bool Ready { get; private set; }
    public bool Busy { get; private set; }
    public string Error { get; private set; }

    /// <summary>Every transcript line (worker thread; the dock queues them to the main thread).</summary>
    public event Action<Line> Transcript;

    /// <summary>The agent asked for permission. Return the ACP result; set by the dock.</summary>
    public Func<JsonNode, Task<JsonNode>> Ask
    {
        get => _client.PermissionHandler;
        set => _client.PermissionHandler = value;
    }

    public AgentSession(AgentPreset preset, string name = null)
    {
        Preset = preset;
        Name = name ?? preset.Name;
        _client.Update += OnUpdate;
        _client.Log += s => Transcript?.Invoke(new Line("info", s));
        _client.Closed += why =>
        {
            Ready = false;
            Transcript?.Invoke(new Line("info", $"[{Name} ended: {why}]"));
        };
    }

    /// <summary>Starts the process, initializes, and opens a session in <paramref name="cwd"/>. Never prompts.</summary>
    public async Task<bool> StartAsync(string exe, IReadOnlyList<string> args, string cwd)
    {
        if (!_client.Start(exe, args, cwd, out string why))
        {
            Error = $"could not start {exe}: {why}";
            Transcript?.Invoke(new Line("error", Error));
            return false;
        }

        Transcript?.Invoke(new Line("info", $"started {exe} {string.Join(' ', args)} (pid {_client.ProcessId})"));
        try
        {
            JsonNode init = await _client.InitializeAsync(HandshakeTimeout).ConfigureAwait(false);
            string agent = (string)init?["agentInfo"]?["name"] ?? Name;
            string version = (string)init?["agentInfo"]?["version"] ?? "";
            Transcript?.Invoke(new Line("info", $"initialized: {agent} {version}, protocol {(int?)init?["protocolVersion"]}"));
            if (init?["authMethods"] is JsonArray methods && methods.Count > 0)
            {
                var names = new List<string>();
                foreach (var m in methods)
                {
                    names.Add((string)m?["name"] ?? (string)m?["id"] ?? "?");
                }

                Transcript?.Invoke(new Line("info", "sign-in methods the agent offers: " + string.Join(", ", names)));
            }

            string session = await _client.NewSessionAsync(cwd, HandshakeTimeout).ConfigureAwait(false);
            Transcript?.Invoke(new Line("info", $"session {session} open in {cwd}"));
            Ready = true;
            return true;
        }
        catch (AcpException ex) when (ex.Code == -32000 || ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            Error = $"{Name} needs you to sign in first ({ex.Message}). Sign-in: {Preset.Login}";
        }
        catch (Exception ex)
        {
            Error = $"{Name}: {ex.Message}";
        }

        Transcript?.Invoke(new Line("error", Error));
        return false;
    }

    /// <summary>Sends one prompt and waits for the turn to finish. <paramref name="onText"/> gets the answer's text as it streams.</summary>
    public async Task<string> PromptAsync(string text, Action<string> onText = null)
    {
        if (!Ready || Busy)
        {
            throw new InvalidOperationException(Ready ? "the agent is still answering" : "the agent is not running");
        }

        Busy = true;
        _turn = new CancellationTokenSource();
        Action<Line> tap = null;
        if (onText != null)
        {
            tap = l =>
            {
                if (l.Kind == "message")
                {
                    onText(l.Text);
                }
            };
            Transcript += tap;
        }

        try
        {
            Transcript?.Invoke(new Line("user", text));
            string stop = await _client.PromptAsync(text, PromptTimeout, _turn.Token).ConfigureAwait(false);
            Transcript?.Invoke(new Line("info", $"[turn ended: {stop}]"));
            return stop;
        }
        catch (OperationCanceledException)
        {
            return "cancelled";
        }
        finally
        {
            if (tap != null)
            {
                Transcript -= tap;
            }

            Busy = false;
        }
    }

    /// <summary>Asks the agent to stop the turn it is on.</summary>
    public void Cancel() => _client.Cancel();

    private void OnUpdate(JsonNode p)
    {
        JsonNode u = p?["update"];
        switch ((string)u?["sessionUpdate"])
        {
            case "agent_message_chunk":
                Transcript?.Invoke(new Line("message", AcpClient.TextOf(u["content"])));
                break;
            case "agent_thought_chunk":
                Transcript?.Invoke(new Line("thought", AcpClient.TextOf(u["content"])));
                break;
            case "tool_call":
            {
                string id = (string)u["toolCallId"] ?? "";
                string title = (string)u["title"] ?? "tool";
                lock (_tools)
                {
                    _tools[id] = title;
                }

                Transcript?.Invoke(new Line("tool", $"{title} ({(string)u["kind"] ?? "other"}) {(string)u["status"] ?? "pending"}"));
                break;
            }

            case "tool_call_update":
            {
                string id = (string)u["toolCallId"] ?? "";
                string title;
                lock (_tools)
                {
                    _tools.TryGetValue(id, out title);
                }

                string status = (string)u["status"];
                if (status != null)
                {
                    Transcript?.Invoke(new Line("tool", $"{(string)u["title"] ?? title ?? id}: {status}"));
                }

                break;
            }

            case "plan":
                if (u["entries"] is JsonArray entries)
                {
                    var sb = new StringBuilder("plan:");
                    foreach (var e in entries)
                    {
                        sb.Append($"\n  [{(string)e?["status"] ?? "pending"}] {(string)e?["content"]}");
                    }

                    Transcript?.Invoke(new Line("plan", sb.ToString()));
                }

                break;
        }
    }

    public void Dispose()
    {
        Ready = false;
        _turn?.Cancel();
        _client.Dispose();
    }
}

/// <summary>An agent session as a chat provider, so the Chat tab can talk to a running agent (Agents over ACP).</summary>
public sealed class AcpChatProvider : IChatProvider
{
    private readonly AgentSession _session;

    public AcpChatProvider(AgentSession session)
    {
        _session = session;
    }

    public string Id => "agent:" + _session.Name;
    public string Name => "Agent: " + _session.Name;
    public string Model { get; set; } = "";

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> history, Action<string, bool> onChunk, CancellationToken ct)
    {
        // The agent keeps its own conversation: only the newest user turn goes.
        string last = "";
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == "user")
            {
                last = history[i].Text;
                break;
            }
        }

        using (ct.Register(_session.Cancel))
        {
            return await _session.PromptAsync(last, t => onChunk(t, false)).ConfigureAwait(false);
        }
    }
}
#endif
