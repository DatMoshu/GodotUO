#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock's Chat tab: a provider picker (Ollama, the OpenAI-compatible endpoints the user
/// added, agents started in the Agents tab), a model picker, and a streaming transcript.
/// Nothing is sent anywhere until the user presses Send.
/// </summary>
[Tool]
public partial class AiChatTab : VBoxContainer
{
    private AiHub _hub;
    private OptionButton _provider, _model;
    private LineEdit _ollamaUrl, _input;
    private CheckBox _think;
    private Button _send, _stop;
    private Label _status;
    private RichTextLabel _log;
    private ConfirmationDialog _endpointDialog;
    private LineEdit _epName, _epUrl, _epModel, _epKey;

    private readonly List<IChatProvider> _providers = new();
    private readonly List<ChatMessage> _history = new();
    private CancellationTokenSource _turn;
    private OllamaProvider _ollama;
    private bool _built;
    private bool _wantModelsOnShow = true;

    /// <summary>True while an answer is streaming.</summary>
    public bool Busy { get; private set; }

    /// <summary>The last error shown in the status line, or null.</summary>
    public string LastError { get; private set; }

    /// <summary>The transcript as plain text.</summary>
    public string Transcript => _log?.GetParsedText() ?? "";

    /// <summary>The conversation so far.</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    public AiChatTab() : this(null)
    {
    }

    internal AiChatTab(AiHub hub)
    {
        Name = "Chat";
        _hub = hub;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_built || _hub == null)
        {
            return;
        }

        _built = true;
        var top = new HBoxContainer();
        AddChild(top);
        top.AddChild(new Label { Text = "Provider" });
        _provider = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        _provider.ItemSelected += _ => OnProviderChanged();
        top.AddChild(_provider);
        top.AddChild(new Label { Text = "Model" });
        _model = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        _model.ItemSelected += i =>
        {
            if (Current != null && i >= 0)
            {
                Current.Model = _model.GetItemText((int)i);
            }
        };
        top.AddChild(_model);
        var refresh = new Button { Text = "Refresh", TooltipText = "List the provider's models again" };
        refresh.Pressed += () => _ = RefreshModelsAsync();
        top.AddChild(refresh);
        _think = new CheckBox { Text = "Think", TooltipText = "Ollama: let a reasoning model (qwen3 and the like) reason before it answers. Slower." };
        top.AddChild(_think);
        var endpoints = new Button { Text = "Add endpoint...", TooltipText = "An OpenAI-compatible server (LM Studio, vLLM, a hosted API). Its key is kept in the operating system's store." };
        endpoints.Pressed += () => _endpointDialog.PopupCentered(new Vector2I(460, 230));
        top.AddChild(endpoints);
        var remove = new Button { Text = "Remove endpoint", TooltipText = "Forget the selected endpoint and its key" };
        remove.Pressed += RemoveSelectedEndpoint;
        top.AddChild(remove);
        _status = new Label { Text = "", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        top.AddChild(_status);

        var urlRow = new HBoxContainer();
        AddChild(urlRow);
        urlRow.AddChild(new Label { Text = "Ollama at" });
        _ollamaUrl = new LineEdit { Text = DefaultOllamaUrl(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ollamaUrl.TextSubmitted += _ => RebuildProviders();
        urlRow.AddChild(_ollamaUrl);

        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
            FocusMode = FocusModeEnum.Click,
        };
        AddChild(_log);

        var row = new HBoxContainer();
        AddChild(row);
        _input = new LineEdit { PlaceholderText = "Ask something. Enter sends.", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _input.TextSubmitted += _ => Send();
        row.AddChild(_input);
        _send = new Button { Text = "Send" };
        _send.Pressed += Send;
        row.AddChild(_send);
        _stop = new Button { Text = "Stop", Disabled = true };
        _stop.Pressed += Stop;
        row.AddChild(_stop);
        var clear = new Button { Text = "New chat" };
        clear.Pressed += NewChat;
        row.AddChild(clear);

        BuildEndpointDialog();
        _hub.SessionsChanged += RebuildProviders;
        RebuildProviders();
        VisibilityChanged += () =>
        {
            if (IsVisibleInTree() && _wantModelsOnShow)
            {
                _wantModelsOnShow = false;
                _ = RefreshModelsAsync();
            }
        };
    }

    public override void _ExitTree()
    {
        if (_hub != null)
        {
            _hub.SessionsChanged -= RebuildProviders;
        }
    }

    private static string DefaultOllamaUrl()
    {
        // Ollama's own variable: "127.0.0.1:11434" or a full URL.
        string host = System.Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return OllamaProvider.DefaultUrl;
        }

        host = host.Trim();
        return host.Contains("://", StringComparison.Ordinal) ? host : "http://" + host;
    }

    /// <summary>The Ollama server the tab talks to; setting it rebuilds the provider list.</summary>
    public string OllamaUrl
    {
        get => _ollamaUrl?.Text ?? DefaultOllamaUrl();
        set
        {
            _ollamaUrl.Text = value;
            RebuildProviders();
        }
    }

    public IChatProvider Current =>
        _provider != null && _provider.Selected >= 0 && _provider.Selected < _providers.Count ? _providers[_provider.Selected] : null;

    public IReadOnlyList<IChatProvider> Providers => _providers;

    private void RebuildProviders()
    {
        if (_provider == null)
        {
            return;
        }

        string keep = Current?.Id;
        _ollama?.Dispose();
        _ollama = new OllamaProvider(OllamaUrl) { Think = _think.ButtonPressed };
        foreach (IChatProvider p in _providers)
        {
            if (p is IDisposable d && p != _ollama)
            {
                d.Dispose();
            }
        }

        _providers.Clear();
        _providers.Add(_ollama);
        foreach (EndpointBook.Entry e in _hub.Endpoints.Entries)
        {
            _providers.Add(_hub.Endpoints.Provider(e));
        }

        foreach (AgentSession s in _hub.Sessions)
        {
            if (s.Ready)
            {
                _providers.Add(new AcpChatProvider(s));
            }
        }

        _provider.Clear();
        int select = 0;
        for (int i = 0; i < _providers.Count; i++)
        {
            _provider.AddItem(_providers[i].Name);
            if (_providers[i].Id == keep)
            {
                select = i;
            }
        }

        _provider.Select(select);
        OnProviderChanged();
    }

    private void OnProviderChanged()
    {
        _model.Clear();
        if (Current is IChatProvider p && !string.IsNullOrEmpty(p.Model))
        {
            _model.AddItem(p.Model);
        }

        _model.Disabled = Current is AcpChatProvider;
        _think.Visible = Current is OllamaProvider;
        _ollamaUrl.GetParent<Control>().Visible = Current is OllamaProvider;
        if (IsVisibleInTree())
        {
            _ = RefreshModelsAsync();
        }
    }

    /// <summary>Selects a provider by id ("ollama", "openai:NAME", "agent:NAME"). False if there is none.</summary>
    public bool SelectProvider(string id)
    {
        for (int i = 0; i < _providers.Count; i++)
        {
            if (_providers[i].Id == id)
            {
                _provider.Select(i);
                OnProviderChanged();
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists the current provider's models (off the main thread, with a timeout) and fills the picker.</summary>
    public async Task<IReadOnlyList<string>> RefreshModelsAsync()
    {
        IChatProvider p = Current;
        if (p == null)
        {
            return Array.Empty<string>();
        }

        SetStatus($"asking {p.Name} for its models...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            IReadOnlyList<string> models = await Task.Run(() => p.ListModelsAsync(cts.Token));
            _hub.Post(() =>
            {
                if (Current != p)
                {
                    return;
                }

                string want = p.Model;
                _model.Clear();
                foreach (string m in models)
                {
                    _model.AddItem(m);
                }

                int at = Math.Max(0, models.ToList().IndexOf(want));
                if (models.Count > 0)
                {
                    _model.Select(at);
                    p.Model = models[at];
                }

                SetStatus(p is AcpChatProvider ? "" : $"{models.Count} model(s)");
            });
            return models;
        }
        catch (Exception ex)
        {
            string why = ex is TimeoutException || ex is OperationCanceledException ? "no answer" : ex.Message;
            _hub.Post(() => SetStatus($"{p.Name}: {why}", true));
            return Array.Empty<string>();
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        LastError = error ? text : null;
        if (_status != null)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", error ? new Color(1f, 0.6f, 0.3f) : new Color(0.7f, 0.7f, 0.7f));
        }
    }

    private void Send() => SendText(_input.Text);

    /// <summary>Sends one user turn to the current provider and streams the answer into the transcript.</summary>
    public void SendText(string text)
    {
        text = text?.Trim() ?? "";
        IChatProvider p = Current;
        if (text.Length == 0 || p == null || Busy)
        {
            return;
        }

        if (p is OllamaProvider o)
        {
            o.Think = _think.ButtonPressed;
        }

        if (string.IsNullOrEmpty(p.Model) && p is not AcpChatProvider)
        {
            SetStatus("pick a model first (is the server running?)", true);
            return;
        }

        _input.Text = "";
        _history.Add(new ChatMessage("user", text));
        _log.AppendText($"[color=#e0b050]You:[/color] {AiHub.Esc(text)}\n[color=#8fb8e0]{AiHub.Esc(p.Name)}{(p.Model.Length > 0 ? " / " + AiHub.Esc(p.Model) : "")}:[/color] ");
        Busy = true;
        _send.Disabled = true;
        _stop.Disabled = false;
        SetStatus("waiting for the first words...");
        _turn = new CancellationTokenSource();
        CancellationToken ct = _turn.Token;
        var answer = new StringBuilder();
        IReadOnlyList<ChatMessage> snapshot = _history.ToArray();
        bool thinkingShown = false;

        _ = Task.Run(async () =>
        {
            string stop;
            string error = null;
            try
            {
                stop = await p.StreamAsync(snapshot, (chunk, thinking) =>
                {
                    _hub.Post(() =>
                    {
                        if (thinking)
                        {
                            thinkingShown = true;
                            _log.AppendText($"[color=#7a7a7a]{AiHub.Esc(chunk)}[/color]");
                        }
                        else
                        {
                            if (thinkingShown)
                            {
                                _log.AppendText("\n");
                                thinkingShown = false;
                            }

                            lock (answer)
                            {
                                answer.Append(chunk);
                            }

                            _log.AppendText(AiHub.Esc(chunk));
                        }

                        SetStatus("streaming...");
                    });
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stop = "stopped";
            }
            catch (Exception ex)
            {
                stop = "error";
                error = ex.Message;
            }

            _hub.Post(() =>
            {
                string finalText;
                lock (answer)
                {
                    finalText = answer.ToString();
                }

                if (finalText.Length > 0)
                {
                    _history.Add(new ChatMessage("assistant", finalText));
                }

                _log.AppendText(error != null ? $"\n[color=orange]{AiHub.Esc(error)}[/color]\n\n" : "\n\n");
                Busy = false;
                _send.Disabled = false;
                _stop.Disabled = true;
                SetStatus(error ?? $"done ({stop})", error != null);
            });
        });
    }

    /// <summary>Cancels the answer that is streaming.</summary>
    public void Stop()
    {
        // For an agent the token also reaches session/cancel (AcpChatProvider.StreamAsync).
        _turn?.Cancel();
    }

    public void NewChat()
    {
        Stop();
        _history.Clear();
        _log.Clear();
        SetStatus("");
        _input.GrabFocus();
    }

    private void BuildEndpointDialog()
    {
        _endpointDialog = new ConfirmationDialog { Title = "Add an OpenAI-compatible endpoint", OkButtonText = "Save" };
        var grid = new GridContainer { Columns = 2 };
        _endpointDialog.AddChild(grid);
        grid.AddChild(new Label { Text = "Name" });
        _epName = new LineEdit { PlaceholderText = "LM Studio", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_epName);
        grid.AddChild(new Label { Text = "Base URL" });
        _epUrl = new LineEdit { PlaceholderText = "http://127.0.0.1:1234/v1", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_epUrl);
        grid.AddChild(new Label { Text = "Model" });
        _epModel = new LineEdit { PlaceholderText = "(optional, pick it from the list later)" };
        grid.AddChild(_epModel);
        grid.AddChild(new Label { Text = "API key" });
        _epKey = new LineEdit
        {
            Secret = true,
            PlaceholderText = EndpointBook.CanKeepKeys ? "kept in the operating system's store" : "this system keeps no keys; type it again each time",
        };
        grid.AddChild(_epKey);
        _endpointDialog.Confirmed += () =>
        {
            string name = _epName.Text.Trim();
            string url = _epUrl.Text.Trim();
            if (name.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("an endpoint needs a name and an http(s) URL", true);
                return;
            }

            if (!_hub.Endpoints.Put(name, url, _epModel.Text.Trim(), _epKey.Text, out string why))
            {
                SetStatus($"the key was not saved: {why}", true);
            }

            _epKey.Text = "";
            RebuildProviders();
            SelectProvider("openai:" + name);
        };
        AddChild(_endpointDialog);
    }

    private void RemoveSelectedEndpoint()
    {
        if (Current is OpenAiCompatProvider p)
        {
            _hub.Endpoints.Remove(p.Name);
            RebuildProviders();
        }
        else
        {
            SetStatus("select an endpoint you added first", true);
        }
    }

    /// <summary>Releases what the tab holds: the stream, the HTTP clients.</summary>
    public void Shutdown()
    {
        _turn?.Cancel();
        foreach (IChatProvider p in _providers)
        {
            (p as IDisposable)?.Dispose();
        }

        _providers.Clear();
        _ollama = null;
    }
}
#endif
