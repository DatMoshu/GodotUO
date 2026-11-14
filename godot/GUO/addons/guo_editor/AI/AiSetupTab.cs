#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>Optional onboarding using the same ACP sessions and service registry as the AI dock.</summary>
[Tool]
public partial class AiSetupTab : ScrollContainer
{
    internal const string CompletedSetting = "guo/ai/setup_completed";
    private AiDock _dock;
    private OptionButton _provider, _auth;
    private Label _intro, _detected, _status;
    private TextEdit _commands;
    private Button _check, _signIn, _finish;
    private Button _copyInstall, _copyLogin;
    private Button _openSignIn;
    private string _signInUrl;
    private bool _busy, _built;
    private AgentSession _session;
    private double _elapsed;
    private readonly AgentPreset[] _presets = { AgentCatalog.Codex, AgentCatalog.ClaudeCode, AgentCatalog.Antigravity, AgentCatalog.OpenCode };
    internal event Action Completed;

    public AiSetupTab() { Name = "Setup"; }
    internal AiSetupTab(AiDock dock) : this() { _dock = dock; }
    internal static bool IsCompleted => EditorInterface.Singleton.GetEditorSettings().HasSetting(CompletedSetting)
        && EditorInterface.Singleton.GetEditorSettings().GetSetting(CompletedSetting).AsBool();
    private AgentPreset Preset => _presets[Math.Max(0, _provider.Selected)];

    public override void _Ready()
    {
        if (_built || _dock == null || !AiFeatures.Enabled) return;
        _built = true;
        HorizontalScrollMode = ScrollMode.Disabled;
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        AddChild(body);
        var title = new Label { Text = "Connect your AI assistant" };
        body.AddChild(title);
        title.AddThemeFontSizeOverride("font_size", title.GetThemeFontSize("font_size") + 4);
        body.AddChild(Copy("Use a coding subscription through its own agent, or configure an API/local service separately. GUO never needs your subscription password."));
        body.AddChild(new Label { Text = "1. Choose your assistant" });
        _provider = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (AgentPreset preset in _presets) _provider.AddItem(preset.Name);
        _provider.ItemSelected += _ => SelectProvider();
        body.AddChild(_provider);
        _intro = Copy(""); body.AddChild(_intro);
        body.AddChild(new Label { Text = "2. Install and sign in" });
        _detected = Copy(""); body.AddChild(_detected);
        _commands = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(0, 74), WrapMode = TextEdit.LineWrappingMode.Boundary };
        body.AddChild(_commands);
        var commands = new HFlowContainer(); body.AddChild(commands);
        _copyInstall = AddButton(commands, "Copy install command", () => DisplayServer.ClipboardSet(AiAgentsTab.CommandOnly(Preset.Install)));
        _copyLogin = AddButton(commands, "Copy sign-in command", () => DisplayServer.ClipboardSet(LoginCommand(Preset)));
        AddButton(commands, "Refresh detection", SelectProvider);
        AddButton(commands, "Setup guide", () => OS.ShellOpen(Guide(Preset)));
        body.AddChild(Copy("Run these commands in your terminal if needed, then return here. Installation and sign-in are never started automatically."));
        body.AddChild(new Label { Text = "3. Check the connection" });
        body.AddChild(Copy("This opens an ACP session without sending a prompt. It checks the adapter and session readiness; your first message checks the model response and uses your plan."));
        var connection = new HFlowContainer(); body.AddChild(connection);
        _check = AddButton(connection, "Check connection", () => _ = CheckAsync());
        _auth = new OptionButton { Visible = false, CustomMinimumSize = new Vector2(170, 0) };
        connection.AddChild(_auth);
        _signIn = AddButton(connection, "Sign in with selected method", () => _ = SignInAsync());
        _signIn.Visible = false;
        _openSignIn = AddButton(connection, "Open sign-in page", () => { if (_signInUrl != null) OS.ShellOpen(_signInUrl); });
        _openSignIn.Visible = false;
        _status = Copy("Choose an assistant to begin."); body.AddChild(_status);
        var end = new HFlowContainer(); body.AddChild(end);
        _finish = AddButton(end, "Finish setup and open Chat", Finish);
        _finish.Disabled = true;
        AddButton(end, "View agent transcript", () => _dock.ShowTab("Agents"));
        AddButton(end, "API or local services…", () => _dock.ShowTab("Services"));
        body.AddChild(Copy("API keys use separate provider billing. ChatGPT sign-in, Claude sign-in and Google sign-in belong in Agents. OpenCode uses the provider or Go plan you connect inside OpenCode."));
        SelectProvider();
    }

    private static Label Copy(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private static Button AddButton(Node parent, string text, Action action)
    {
        var button = new Button { Text = text }; button.Pressed += action; parent.AddChild(button); return button;
    }

    internal static string LoginCommand(AgentPreset preset) => preset.Id switch
    {
        "codex" => "codex login",
        "claude" => "claude auth login",
        "gemini" => "gemini",
        "opencode" => "opencode auth login",
        _ => "",
    };

    private static string Guide(AgentPreset preset) => preset.Id switch
    {
        "codex" => "https://developers.openai.com/codex/auth/",
        "claude" => "https://code.claude.com/docs/en/authentication",
        "gemini" => "https://geminicli.com/docs/get-started/authentication/",
        "antigravity" => "https://github.com/agentclientprotocol/registry/tree/main/antigravity-acp",
        _ => "https://opencode.ai/docs/providers/",
    };

    private void SelectProvider()
    {
        if (_busy || !AiFeatures.Enabled) return;
        _session = _dock.Hub.Sessions.FirstOrDefault(s => s.Preset.Id == Preset.Id && s.Ready)
            ?? _dock.Hub.Sessions.LastOrDefault(s => s.Preset.Id == Preset.Id);
        _intro.Text = Preset.Id switch
        {
            "codex" => "OpenAI / Codex: choose Sign in with ChatGPT to use your eligible ChatGPT plan. An OpenAI API key is a separate billing route.",
            "claude" => "Claude: sign in with your Claude account through Claude Code. Plan eligibility and usage limits are controlled by Anthropic.",
            "gemini" => "Gemini CLI: Google may require individual Google AI subscribers to migrate to Antigravity. Check current eligibility before installing; an API key is a separate billing route.",
            "antigravity" => "Google Gemini: use Google's official Antigravity ACP server with Google account sign-in. Legacy Gemini CLI no longer accepts individual Google AI subscriptions. Antigravity CLI (agy) and the ACP server are separate downloads.",
            _ => "OpenCode: connect ChatGPT Plus/Pro with browser sign-in. For an existing Go plan, find its key in the Console: Settings > your account name > Create. Paste it only into OpenCode's terminal. Console browser login connects a separate provider. Claude subscriptions use the Claude agent above.",
        };
        string path = Preset.Find();
        _detected.Text = path == null ? "Adapter not found. Install it, then refresh detection. Restart GUO if your terminal PATH changed." : $"Adapter detected: {path}";
        _commands.Text = AiAgentsTab.CommandOnly(Preset.Install) + "\n" + LoginCommand(Preset);
        _copyInstall.Disabled = _copyLogin.Disabled = Preset.Id == "antigravity";
        _check.Disabled = path == null;
        _auth.Clear(); _auth.Visible = _signIn.Visible = false;
        _finish.Disabled = _session?.Ready != true;
        _status.Text = _session?.Ready == true ? "Session ready. You can finish setup or connect another assistant." : "Not checked. A detected adapter does not confirm sign-in.";
    }

    // Do not turn an inherited API key into an accidental subscription test.
    internal static bool HasBillingOverride(AgentPreset preset) => (preset.Id switch
    {
        "codex" => new[] { "OPENAI_API_KEY" },
        "claude" => new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX" },
        "gemini" => new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI" },
        "antigravity" => new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI" },
        _ => Array.Empty<string>(),
    }).Any(name => !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(name)));

    internal async Task<bool> CheckAsync()
    {
        if (_busy || !AiFeatures.Enabled) return false;
        if (HasBillingOverride(Preset))
        {
            _status.Text = "This editor inherited an API or cloud billing override. Restart GUO without that override to check subscription sign-in. No connection was started.";
            return false;
        }
        _session = _dock.Hub.Sessions.FirstOrDefault(s => s.Preset.Id == Preset.Id && s.Ready) ?? _session;
        if (_session?.Ready == true)
        {
            _finish.Disabled = false;
            _status.Text = "Existing session ready. No message has been sent.";
            return true;
        }
        if (_session != null) { _dock.Agents.StopSession(_session); _session = null; }
        SetBusy(true, "Checking adapter and opening a session…");
        bool ready = false;
        try
        {
            ready = await _dock.Agents.StartPresetAsync(Preset);
            if (!Alive()) return false;
            _session = _dock.Hub.Sessions.LastOrDefault(s => s.Preset.Id == Preset.Id);
            _finish.Disabled = !ready;
            _status.Text = ready ? "Session ready. No message has been sent. Finish setup to try your first chat, or choose another assistant." : _session?.Error ?? "Connection failed. Refresh detection or follow the sign-in guide, then retry.";
            RefreshAuthMethods();
        }
        catch (Exception ex) { if (Alive()) _status.Text = $"Connection failed: {ex.Message}. See the agent transcript or sign-in guide."; }
        finally { if (Alive()) SetBusy(false); }
        return ready;
    }

    private void RefreshAuthMethods()
    {
        _auth.Clear();
        if (_session?.Ready != true && _session?.Client.AgentInfo?["authMethods"] is JsonArray methods)
        {
            foreach (JsonNode method in methods)
            {
                string id = (string)method?["id"] ?? "";
                // Key and gateway authentication belongs in the explicit API path.
                if (!SubscriptionMethod(id)) continue;
                _auth.AddItem((string)method?["name"] ?? id);
                _auth.SetItemMetadata(_auth.ItemCount - 1, id);
            }
        }
        _auth.Visible = _signIn.Visible = _auth.ItemCount > 0;
    }

    internal static bool SubscriptionMethod(string id) => !string.IsNullOrEmpty(id)
        && !new[] { "api", "gateway", "vertex", "agent-platform" }.Any(part => id.Contains(part, StringComparison.OrdinalIgnoreCase));

    private async Task SignInAsync()
    {
        if (_busy || !AiFeatures.Enabled || _session == null || _auth.Selected < 0) return;
        string method = _auth.GetItemMetadata(_auth.Selected).AsString();
        AgentSession signingSession = _session;
        _signInUrl = null;
        Action<string> open = url => _dock.Hub.Post(() =>
        {
            if (!Alive() || !_busy || _signInUrl != null) return;
            _signInUrl = url;
            _openSignIn.Visible = true;
            OS.ShellOpen(url);
            _status.Text = "Complete Google account sign-in in your browser. If no browser appeared, choose Open sign-in page.";
        });
        signingSession.SignInPage += open;
        SetBusy(true, "Waiting for the agent's sign-in flow. Complete it in your browser or terminal. You can close setup; no prompt will be sent.");
        try
        {
            bool ready = await signingSession.SignInAsync(method, EditorData.RepoRoot);
            if (!Alive()) return;
            _dock.Hub.NotifySessions();
            _finish.Disabled = !ready;
            _status.Text = ready ? "Signed in and session ready. No message has been sent." : _session.Error;
            RefreshAuthMethods();
        }
        catch (Exception ex) { if (Alive()) _status.Text = $"Sign-in failed: {ex.Message}. Use the sign-in command in your terminal, then check again."; }
        finally
        {
            signingSession.SignInPage -= open;
            _signInUrl = null;
            if (Alive()) { _openSignIn.Visible = false; SetBusy(false); }
        }
    }

    private bool Alive() => GodotObject.IsInstanceValid(this) && IsInsideTree() && AiFeatures.Enabled;
    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (!_built || _busy || _elapsed < 1 || _session == null) return;
        _elapsed = 0;
        if (!_session.Ready && !_finish.Disabled)
        {
            _finish.Disabled = true;
            _status.Text = "The agent disconnected. Check the connection again before finishing setup.";
        }
    }
    private void SetBusy(bool busy, string status = null)
    {
        _busy = busy; _provider.Disabled = busy; _check.Disabled = busy; _signIn.Disabled = busy;
        if (status != null) _status.Text = status;
    }

    internal void Finish()
    {
        if (_busy || !AiFeatures.Enabled || _session?.Ready != true) { _finish.Disabled = true; return; }
        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        settings.SetSetting(CompletedSetting, true);
        Completed?.Invoke();
        _dock.Chat.SelectProvider("agent:" + _session.Name);
        _dock.ShowTab("Chat");
    }
}
#endif
