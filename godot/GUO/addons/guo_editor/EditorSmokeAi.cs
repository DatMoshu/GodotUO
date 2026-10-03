#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The smoke stage for the AI hub (ADR-0028). Nothing here reaches a real model or a paid service:
/// ACP runs against tools/ai_hub/fake_acp_agent.py, Ollama and an OpenAI-compatible endpoint against
/// stub HTTP servers this stage starts itself, the queue against a temporary database. Then the dock
/// itself is driven: an agent started from the Agents tab, a permission dialog answered, a chat
/// turn streamed into the Chat tab, a request posted from the Queue tab, and every child process
/// killed on shutdown.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _aiReport = new();
    private Task _aiTask;

    private void AiFail(string why)
    {
        _aiReport["ok"] = false;
        _failures.Add($"AI: {why}");
        GD.Print($"[GUO editor] smoke AI FAIL: {why}");
    }

    private void AiCheck(string name, bool ok, string detail = "")
    {
        _aiReport[name] = ok;
        if (!ok)
        {
            AiFail($"{name} {detail}".Trim());
        }
    }

    /// <summary>One tick of the stage; true when it is finished.</summary>
    private bool StepAi()
    {
        if (System.Environment.GetEnvironmentVariable("GUO_AI_SKIP") != null)
        {
            return true;
        }

        if (_aiTask == null)
        {
            _aiReport["ok"] = true;
            _aiTask = RunAiAsync();
            _aiClock.Restart();
        }

        if (_aiTask.IsCompleted)
        {
            if (_aiTask.IsFaulted)
            {
                AiFail($"threw {_aiTask.Exception?.GetBaseException().GetType().Name}: {_aiTask.Exception?.GetBaseException().Message}");
            }

            _report["ai"] = _aiReport;
            return true;
        }

        if (_aiClock.Elapsed.TotalSeconds > 120)
        {
            AiFail("the stage did not finish within 120 s");
            _report["ai"] = _aiReport;
            return true;
        }

        return false;
    }

    private readonly Stopwatch _aiClock = new();

    private async Task Delay(double seconds) =>
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        double t = 0;
        while (!done() && t < seconds)
        {
            await Delay(0.05);
            t += 0.05;
        }

        return done();
    }

    private static bool Alive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task RunAiAsync()
    {
        string root = EditorData.RepoRoot;
        string fake = Path.Combine(root, "tools", "ai_hub", "fake_acp_agent.py");
        string python = QueueClient.FindPython();
        string temp = Path.Combine(_out, "ai");
        Directory.CreateDirectory(temp);

        if (python == null || !File.Exists(fake))
        {
            AiFail($"python or the fake agent is missing (python: {python}, agent: {fake})");
            return;
        }

        // --- (a) ACP against the fake agent -------------------------------------------------------
        using (var client = new AcpClient())
        {
            var kinds = new List<string>();
            var text = new StringBuilder();
            int chunks = 0;
            client.Update += p =>
            {
                lock (kinds)
                {
                    string kind = (string)p["update"]?["sessionUpdate"];
                    kinds.Add(kind);
                    if (kind == "agent_message_chunk")
                    {
                        chunks++;
                        text.Append(AcpClient.TextOf(p["update"]["content"]));
                    }
                }
            };
            client.PermissionHandler = p => Task.FromResult(AiHub.Selected("allow-once"));
            bool started = client.Start(python, new[] { fake, "--banner" }, temp, out string why);
            AiCheck("acp_start", started, why);
            if (started)
            {
                int pid = client.ProcessId;
                JsonNode init = await client.InitializeAsync(TimeSpan.FromSeconds(10));
                AiCheck("acp_initialize", (int?)init?["protocolVersion"] == 1 && (string)init?["agentInfo"]?["name"] == "fake-acp-agent");
                string session = await client.NewSessionAsync(temp, TimeSpan.FromSeconds(10));
                AiCheck("acp_session_new", session == "fake-1", session);
                string stop = await client.PromptAsync("hello hub", TimeSpan.FromSeconds(10));
                string got;
                lock (kinds)
                {
                    got = text.ToString();
                }

                AiCheck("acp_prompt_stop", stop == "end_turn", stop);
                AiCheck("acp_streamed", chunks >= 3 && got == "echo: hello hub", $"{chunks} chunk(s): '{got}'");
                AiCheck("acp_update_kinds", kinds.Contains("agent_thought_chunk") && kinds.Contains("tool_call") && kinds.Contains("tool_call_update"),
                    string.Join(",", kinds.Distinct()));
                _aiReport["acp_chunks"] = chunks;

                lock (kinds)
                {
                    text.Clear();
                }

                stop = await client.PromptAsync("permission please", TimeSpan.FromSeconds(10));
                lock (kinds)
                {
                    got = text.ToString();
                }

                AiCheck("acp_permission", stop == "end_turn" && got.Contains("selected allow-once"), got);

                // An unsupported client method is refused, not hung on: fs was never advertised.
                client.Dispose();
                await Delay(0.3);
                AiCheck("acp_process_killed", !Alive(pid), $"pid {pid} still running after Dispose");
            }
        }

        using (var silent = new AcpClient())
        {
            bool ok = silent.Start(python, new[] { fake, "--silent" }, temp, out string why);
            int pid = silent.ProcessId;
            bool timedOut = false;
            var sw = Stopwatch.StartNew();
            try
            {
                await silent.InitializeAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            AiCheck("acp_timeout", ok && timedOut && sw.Elapsed.TotalSeconds < 5, $"started {ok} {why}, timed out {timedOut}, {sw.ElapsedMilliseconds} ms");
            silent.Dispose();
            await Delay(0.3);
            AiCheck("acp_timeout_killed", !Alive(pid));
        }

        // --- (b) Ollama and an OpenAI-compatible endpoint against stub servers ---------------------
        using (var stub = new StubServer())
        {
            using var ollama = new OllamaProvider(stub.Url);
            IReadOnlyList<string> models = await ollama.ListModelsAsync(CancellationToken.None);
            AiCheck("ollama_models", models.Count == 2 && models[0] == "stub:1b", string.Join(",", models));
            ollama.Model = "stub:1b";
            var answer = new StringBuilder();
            var thought = new StringBuilder();
            int pieces = 0;
            string stop = await ollama.StreamAsync(new[] { new ChatMessage("user", "hi") }, (t, thinking) =>
            {
                (thinking ? thought : answer).Append(t);
                if (!thinking)
                {
                    pieces++;
                }
            }, CancellationToken.None);
            AiCheck("ollama_stream", answer.ToString() == "Hello from the stub." && pieces == 3 && thought.ToString() == "hmm" && stop == "stop",
                $"'{answer}' in {pieces} piece(s), thought '{thought}', stop {stop}");
            AiCheck("ollama_request", stub.LastChatBody?["model"]?.ToString() == "stub:1b" && (bool?)stub.LastChatBody?["stream"] == true
                && (string)stub.LastChatBody?["messages"]?[0]?["content"] == "hi", stub.LastChatBody?.ToJsonString());

            using var compat = new OpenAiCompatProvider("Stub", stub.Url + "/v1", () => "k-123") { Model = "gpt-stub" };
            IReadOnlyList<string> compatModels = await compat.ListModelsAsync(CancellationToken.None);
            var compatText = new StringBuilder();
            await compat.StreamAsync(new[] { new ChatMessage("user", "yo") }, (t, _) => compatText.Append(t), CancellationToken.None);
            AiCheck("openai_compat", compatModels.Count == 1 && compatModels[0] == "gpt-stub" && compatText.ToString() == "Hi there" && stub.LastAuth == "Bearer k-123",
                $"models {compatModels.Count}, text '{compatText}', auth '{stub.LastAuth}'");

            // A silent server must not hang the editor: the idle timeout ends the turn.
            using var quiet = new OllamaProvider(stub.Url + "/quiet") { Model = "x", IdleTimeout = TimeSpan.FromSeconds(1) };
            bool idled = false;
            var swIdle = Stopwatch.StartNew();
            try
            {
                await quiet.StreamAsync(new[] { new ChatMessage("user", "hi") }, (_, _) => { }, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                idled = true;
            }

            AiCheck("ollama_idle_timeout", idled && swIdle.Elapsed.TotalSeconds < 6, $"{swIdle.ElapsedMilliseconds} ms");

            // The key for an endpoint is sealed by the operating system's store, never written as text.
            if (EndpointBook.CanKeepKeys)
            {
                string book = Path.Combine(temp, $"endpoints{Suffix}.json");
                var eb = new EndpointBook(book);
                bool put = eb.Put("Stub", stub.Url + "/v1", "gpt-stub", "k-secret-xyz", out string whyPut);
                string file = File.ReadAllText(book);
                AiCheck("endpoint_key_sealed", put && !file.Contains("k-secret-xyz") && eb.KeyFor(new EndpointBook(book).Entries[0]) == "k-secret-xyz", whyPut);
            }

            // --- the dock -------------------------------------------------------------------------
            if (Ai == null || Ai.Chat == null || Ai.Agents == null || Ai.Queue == null)
            {
                AiFail("the AI dock or one of its tabs does not exist");
                return;
            }

            Ai.ShowTab("Agents");
            string detection = Ai.Agents.DetectionText;
            AiCheck("detect_lists_presets", detection.Contains("OpenCode") && detection.Contains("Codex") && detection.Contains("Claude Code") && detection.Contains("Gemini CLI"), detection);
            _aiReport["detection"] = detection.Replace("\n", " | ");
            if (AgentCatalog.OpenCode.Find() == null)
            {
                AiCheck("detect_missing_shows_install", detection.Contains("npm i -g opencode-ai"), detection);
            }

            bool up = await Ai.Agents.StartAsync(AgentCatalog.Custom, "Fake", python, new[] { fake });
            AiCheck("dock_agent_started", up && Ai.Hub.Sessions.Count == 1 && Ai.Hub.Sessions[0].Ready, Ai.Hub.Sessions.FirstOrDefault()?.Error);
            int fakePid = Ai.Hub.Sessions.FirstOrDefault()?.Client.ProcessId ?? 0;
            if (up)
            {
                Task<string> turn = Ai.Agents.SendPromptAsync("permission via dock");
                bool dialog = await Until(() => Ai.PermissionDialog != null, 10);
                AiCheck("dock_permission_dialog", dialog);
                if (dialog)
                {
                    string title = Ai.PermissionDialog.DialogText;
                    AiCheck("dock_permission_text", title.Contains("Edit notes.txt"), title);
                    Ai.PermissionDialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "allow-once");
                }

                string stopped = await WithTimeout(turn, 10);
                await Until(() => Ai.Agents.Transcript.Contains("permission outcome"), 5);
                AiCheck("dock_agent_transcript", stopped == "end_turn" && Ai.Agents.Transcript.Contains("permission outcome: selected allow-once")
                    && Ai.Agents.Transcript.Contains("tool: Read notes.txt"), Ai.Agents.Transcript);

                // The same agent as a chat provider (Agents over ACP).
                await Delay(0.2);
                AiCheck("chat_lists_agent", Ai.Chat.SelectProvider("agent:Fake"), string.Join(",", Ai.Chat.Providers.Select(p => p.Id)));
                Ai.Chat.SendText("through chat");
                await Until(() => !Ai.Chat.Busy && Ai.Chat.History.Count >= 2, 10);
                AiCheck("chat_via_agent", Ai.Chat.History.Count >= 2 && Ai.Chat.History[^1].Text == "echo: through chat", Ai.Chat.Transcript);
                Ai.Chat.NewChat();
            }

            // Chat with the stub Ollama.
            Ai.ShowTab("Chat");
            Ai.Chat.OllamaUrl = stub.Url;
            AiCheck("chat_picks_ollama", Ai.Chat.SelectProvider("ollama"));
            await Ai.Chat.RefreshModelsAsync();
            bool picked = await Until(() => Ai.Chat.Current?.Model == "stub:1b", 5);
            AiCheck("chat_model_list", picked, Ai.Chat.LastError ?? Ai.Chat.Current?.Model);
            Ai.Chat.SendText("hi");
            await Until(() => Ai.Chat.Busy, 2);
            bool finished = await Until(() => !Ai.Chat.Busy, 15);
            await Delay(0.2);
            AiCheck("chat_streams", finished && Ai.Chat.Transcript.Contains("Hello from the stub.") && Ai.Chat.History.Count == 2, Ai.Chat.Transcript);

            // An unreachable server is a message, not a hang.
            Ai.Chat.OllamaUrl = "http://127.0.0.1:1";
            var swDown = Stopwatch.StartNew();
            await Ai.Chat.RefreshModelsAsync();
            await Delay(0.2);
            AiCheck("chat_server_down", Ai.Chat.LastError != null && swDown.Elapsed.TotalSeconds < 12, Ai.Chat.LastError);
            Ai.Chat.OllamaUrl = OllamaProvider.DefaultUrl;

            // Optional, by hand: GUO_AI_REAL_OLLAMA=qwen3:8b chats with the local Ollama (free, nothing leaves the machine).
            string real = System.Environment.GetEnvironmentVariable("GUO_AI_REAL_OLLAMA");
            if (!string.IsNullOrEmpty(real))
            {
                Ai.Chat.SelectProvider("ollama");
                IReadOnlyList<string> have = await Ai.Chat.RefreshModelsAsync();
                _aiReport["real_ollama_models"] = string.Join(",", have);
                Ai.Chat.Current.Model = real;
                Ai.Chat.NewChat();
                Ai.Chat.SendText("Reply with exactly the word: pong");
                await Until(() => Ai.Chat.Busy, 2);
                await Until(() => !Ai.Chat.Busy, 100);
                string reply = Ai.Chat.History.Count >= 2 ? Ai.Chat.History[^1].Text : "";
                _aiReport["real_ollama_reply"] = reply;
                GD.Print($"[GUO editor] smoke AI real Ollama {real}: '{reply.Trim()}'");
                AiCheck("real_ollama_chat", reply.Length > 0, Ai.Chat.LastError ?? "no reply");
                Ai.Chat.NewChat();
            }

            // --- (c) the queue, in a temporary database -----------------------------------------------
            string db = Path.Combine(temp, $"queue{Suffix}.db");
            var q = new QueueClient(root, db);
            AiCheck("queue_available", q.Available, q.Why);
            if (q.Available)
            {
                (long id, string err) = await q.PostAsync("claude", "guo-smoke", "hello\nqueue");
                AiCheck("queue_post", id > 0, err);
                (long refused, string whyRefused) = await q.PostAsync("claude", "guo-smoke", "my password = hunter2hunter2");
                AiCheck("queue_refuses_secret", refused == 0 && whyRefused.Length > 0, whyRefused);
                var (list, listErr) = await q.ListAsync();
                AiCheck("queue_list", list != null && list.Count == 1 && list[0].Id == id && list[0].Status == "new" && list[0].Text == "hello\nqueue", listErr);
                // An agent answers through the tool, as a running session would.
                var reply = Process.Start(new ProcessStartInfo(python)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    ArgumentList = { Path.Combine(root, "tools", "agent_queue", "run.py"), "--db", db, "reply", id.ToString(), "got it", "--from", "claude" },
                });
                await reply.WaitForExitAsync();
                var (shown, showErr) = await q.ShowAsync(id);
                AiCheck("queue_reply_seen", shown != null && shown.Status == "answered" && shown.Replies.Count == 1 && shown.Replies[0].Text == "got it", showErr);

                // The Queue tab, pointed at the same file, posts and shows it live.
                Ai.Queue.Db = db;
                Ai.ShowTab("Queue");
                long second = await Ai.Queue.PostAsync("claude", "guo-smoke", "from the tab");
                AiCheck("queue_tab_post", second > id, second.ToString());
                await Ai.Queue.RefreshAsync();
                AiCheck("queue_tab_list", Ai.Queue.Requests.Count == 2 && Ai.Queue.Requests[^1].Text == "from the tab");
            }

            // --- shutdown: every child process dies ---------------------------------------------------
            Ai.Shutdown();
            await Delay(0.4);
            if (fakePid != 0)
            {
                AiCheck("dock_shutdown_kills_agent", !Alive(fakePid), $"pid {fakePid} still running");
            }
        }

        _aiReport["chunks_streamed"] = _aiReport.GetValueOrDefault("acp_chunks");
    }

    private async Task<string> WithTimeout(Task<string> t, double seconds)
    {
        await Until(() => t.IsCompleted, seconds);
        return t.IsCompletedSuccessfully ? t.Result : null;
    }

    /// <summary>Ollama and OpenAI-compatible stubs on a loopback port.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public string Url { get; }
        public JsonNode LastChatBody { get; private set; }
        public string LastAuth { get; private set; }

        public StubServer()
        {
            for (int port = 18434; ; port++)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Url = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException) when (port < 18500)
                {
                }
            }

            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try
                {
                    c = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Handle(c));
            }
        }

        private async Task Handle(HttpListenerContext c)
        {
            try
            {
                string path = c.Request.Url!.AbsolutePath;
                string body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
                LastAuth = c.Request.Headers["Authorization"];
                c.Response.ContentType = "application/json";
                if (path == "/api/tags")
                {
                    await Write(c, "{\"models\":[{\"name\":\"stub:1b\"},{\"name\":\"stub:7b\"}]}");
                }
                else if (path == "/api/chat")
                {
                    LastChatBody = JsonNode.Parse(body);
                    await Write(c,
                        "{\"message\":{\"role\":\"assistant\",\"thinking\":\"hmm\"},\"done\":false}\n"
                        + "{\"message\":{\"role\":\"assistant\",\"content\":\"Hello \"},\"done\":false}\n"
                        + "{\"message\":{\"role\":\"assistant\",\"content\":\"from the \"},\"done\":false}\n"
                        + "{\"message\":{\"role\":\"assistant\",\"content\":\"stub.\"},\"done\":false}\n"
                        + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n");
                }
                else if (path == "/quiet/api/chat")
                {
                    await Task.Delay(8000, _stop.Token);
                }
                else if (path == "/v1/models")
                {
                    await Write(c, "{\"data\":[{\"id\":\"gpt-stub\"}]}");
                }
                else if (path == "/v1/chat/completions")
                {
                    await Write(c,
                        "data: {\"choices\":[{\"delta\":{\"content\":\"Hi \"}}]}\n\n"
                        + "data: {\"choices\":[{\"delta\":{\"content\":\"there\"},\"finish_reason\":\"stop\"}]}\n\n"
                        + "data: [DONE]\n\n");
                }
                else
                {
                    c.Response.StatusCode = 404;
                    await Write(c, "{}");
                }
            }
            catch (Exception)
            {
                // The client went away (the idle-timeout check does that on purpose).
            }
            finally
            {
                try
                {
                    c.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private static async Task Write(HttpListenerContext c, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            await c.Response.OutputStream.WriteAsync(b);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
