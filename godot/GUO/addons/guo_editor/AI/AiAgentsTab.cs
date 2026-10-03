#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock's Agents tab: start a coding agent CLI over ACP, watch its transcript, send it
/// prompts, answer its permission requests. Each CLI signs in with its own account, so nothing here
/// holds a token. A missing CLI is not installed for the user: the tab shows the commands to run.
/// </summary>
[Tool]
public partial class AiAgentsTab : VBoxContainer
{
    private AiHub _hub;
    private OptionButton _preset, _running;
    private LineEdit _custom, _cwd, _input;
    private Button _start, _stop, _send, _cancel;
    private RichTextLabel _detect;
    private Control _logHost;
    private Label _status;
    private readonly Dictionary<AgentSession, RichTextLabel> _logs = new();
    private readonly Dictionary<AgentSession, string> _lastKind = new();
    private bool _built;

    public AiAgentsTab() : this(null)
    {
    }

    internal AiAgentsTab(AiHub hub)
    {
        Name = "Agents";
        _hub = hub;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>The selected agent's transcript as plain text.</summary>
    public string Transcript => SelectedSession != null && _logs.TryGetValue(SelectedSession, out RichTextLabel l) ? l.GetParsedText() : "";

    /// <summary>The detection report as plain text (what is installed, what to run for the rest).</summary>
    public string DetectionText => _detect?.GetParsedText() ?? "";

    public AgentSession SelectedSession
    {
        get
        {
            int i = _running?.Selected ?? -1;
            return i >= 0 && i < _hub.Sessions.Count ? _hub.Sessions[i] : null;
        }
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
        top.AddChild(new Label { Text = "Agent" });
        _preset = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        foreach (AgentPreset p in AgentCatalog.Presets)
        {
            _preset.AddItem(p.Name);
        }

        _preset.ItemSelected += _ => ShowPreset();
        top.AddChild(_preset);
        _custom = new LineEdit { PlaceholderText = "command line, for example: my-agent --acp", SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        top.AddChild(_custom);
        _start = new Button { Text = "Start" };
        _start.Pressed += () => _ = StartSelectedAsync();
        top.AddChild(_start);
        _stop = new Button { Text = "Stop" };
        _stop.Pressed += StopSelected;
        top.AddChild(_stop);
        top.AddChild(new Label { Text = "Running" });
        _running = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        _running.ItemSelected += _ => ShowSelectedLog();
        top.AddChild(_running);
        _status = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        top.AddChild(_status);

        var folder = new HBoxContainer();
        AddChild(folder);
        folder.AddChild(new Label { Text = "Folder" });
        _cwd = new LineEdit
        {
            Text = EditorData.RepoRoot,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "The agent can read this folder. It changes nothing without your approval in the permission dialog.",
        };
        folder.AddChild(_cwd);

        var copyRow = new HBoxContainer();
        AddChild(copyRow);
        _detect = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SelectionEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 64),
            FocusMode = FocusModeEnum.Click,
        };
        copyRow.AddChild(_detect);
        var copy = new VBoxContainer();
        copyRow.AddChild(copy);
        var copyInstall = new Button { Text = "Copy install" };
        copyInstall.Pressed += () => DisplayServer.ClipboardSet(CommandOnly(CurrentPreset.Install));
        copy.AddChild(copyInstall);
        var copyLogin = new Button { Text = "Copy sign-in" };
        copyLogin.Pressed += () => DisplayServer.ClipboardSet(CommandOnly(CurrentPreset.Login));
        copy.AddChild(copyLogin);

        _logHost = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 110) };
        AddChild(_logHost);

        var row = new HBoxContainer();
        AddChild(row);
        _input = new LineEdit { PlaceholderText = "Prompt for the selected agent. Enter sends.", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _input.TextSubmitted += t => _ = SendPromptAsync(_input.Text);
        row.AddChild(_input);
        _send = new Button { Text = "Send" };
        _send.Pressed += () => _ = SendPromptAsync(_input.Text);
        row.AddChild(_send);
        _cancel = new Button { Text = "Cancel turn" };
        _cancel.Pressed += () => SelectedSession?.Cancel();
        row.AddChild(_cancel);

        _hub.SessionsChanged += RefreshRunning;
        ShowPreset();
        RefreshDetection();
    }

    public override void _ExitTree()
    {
        if (_hub != null)
        {
            _hub.SessionsChanged -= RefreshRunning;
        }
    }

    private AgentPreset CurrentPreset => AgentCatalog.Presets[Math.Max(0, _preset.Selected)];

    /// <summary>The command part of a hint such as "npm i -g x   (older name: y)".</summary>
    internal static string CommandOnly(string hint)
    {
        int paren = hint.IndexOf("   (", StringComparison.Ordinal);
        return paren > 0 ? hint[..paren].Trim() : hint.Trim();
    }

    private void ShowPreset()
    {
        _custom.Visible = CurrentPreset.IsCustom;
    }

    /// <summary>Looks for every preset on PATH and writes what it found, and for a missing one what to run.</summary>
    public void RefreshDetection()
    {
        _detect.Clear();
        foreach (AgentPreset p in AgentCatalog.Presets)
        {
            if (p.IsCustom)
            {
                continue;
            }

            string found = p.Find();
            if (found != null)
            {
                _detect.AppendText($"[color=#7fcf7f]{AiHub.Esc(p.Name)}[/color]: found ({AiHub.Esc(found)}). Uses {AiHub.Esc(p.Paid)}.\n");
            }
            else
            {
                _detect.AppendText($"[color=orange]{AiHub.Esc(p.Name)}[/color]: not installed. Install: [code]{AiHub.Esc(p.Install)}[/code]  Sign in: [code]{AiHub.Esc(p.Login)}[/code]\n");
            }
        }
    }

    private void RefreshRunning()
    {
        if (_running == null)
        {
            return;
        }

        string keep = SelectedSession?.Name;
        _running.Clear();
        int select = 0;
        for (int i = 0; i < _hub.Sessions.Count; i++)
        {
            AgentSession s = _hub.Sessions[i];
            _running.AddItem(s.Name + (s.Ready ? "" : " (starting)"));
            if (s.Name == keep)
            {
                select = i;
            }
        }

        if (_hub.Sessions.Count > 0)
        {
            _running.Select(select);
        }

        ShowSelectedLog();
    }

    private void ShowSelectedLog()
    {
        foreach (var (s, label) in _logs)
        {
            label.Visible = s == SelectedSession;
        }
    }

    public async Task<bool> StartSelectedAsync()
    {
        AgentPreset preset = CurrentPreset;
        if (preset.IsCustom)
        {
            List<string> words = AgentCatalog.SplitCommandLine(_custom.Text);
            if (words.Count == 0)
            {
                SetStatus("type a command line first", true);
                return false;
            }

            string exe = AgentCatalog.Resolve(words[0]);
            if (exe == null)
            {
                SetStatus($"{words[0]} was not found on PATH", true);
                return false;
            }

            return await StartAsync(preset, $"Custom: {System.IO.Path.GetFileNameWithoutExtension(words[0])}", exe, words.GetRange(1, words.Count - 1));
        }

        return await StartPresetAsync(preset);
    }

    /// <summary>Starts a preset if it is installed; otherwise says what to run and returns false.</summary>
    public async Task<bool> StartPresetAsync(AgentPreset preset)
    {
        string exe = preset.Find();
        if (exe == null)
        {
            RefreshDetection();
            SetStatus($"{preset.Name} is not installed: see the install and sign-in commands above", true);
            return false;
        }

        return await StartAsync(preset, preset.Name, exe, preset.Args);
    }

    /// <summary>Starts an agent process, initializes, and opens a session. Never sends a prompt.</summary>
    public async Task<bool> StartAsync(AgentPreset preset, string name, string exe, IReadOnlyList<string> args)
    {
        var session = new AgentSession(preset, name);
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            FocusMode = FocusModeEnum.Click,
            Visible = false,
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        _logHost.AddChild(label);
        _logs[session] = label;
        _lastKind[session] = "";
        session.Transcript += line => _hub.Post(() => Append(session, line));
        _hub.AddSession(session);
        _running.Select(_hub.Sessions.Count - 1);
        ShowSelectedLog();
        SetStatus($"starting {name}...");

        bool ok = await session.StartAsync(exe, args, _cwd.Text);
        var done = new TaskCompletionSource<bool>();
        _hub.Post(() =>
        {
            if (ok)
            {
                SetStatus($"{name} is ready");
                _hub.NotifySessions();
            }
            else
            {
                SetStatus(session.Error ?? $"{name} did not start", true);
            }

            done.SetResult(ok);
        });
        return await done.Task;
    }

    private void StopSelected()
    {
        AgentSession s = SelectedSession;
        if (s == null)
        {
            return;
        }

        Append(s, new AgentSession.Line("info", $"[{s.Name} stopped by you]"));
        _hub.RemoveSession(s);
        if (_logs.Remove(s, out RichTextLabel l))
        {
            l.QueueFree();
        }

        _lastKind.Remove(s);
        SetStatus("");
    }

    /// <summary>Sends a prompt to the selected agent. This may spend the agent's plan: only a person's action calls it.</summary>
    public async Task<string> SendPromptAsync(string text)
    {
        AgentSession s = SelectedSession;
        text = text?.Trim() ?? "";
        if (s == null || !s.Ready || s.Busy || text.Length == 0)
        {
            SetStatus(s == null ? "start an agent first" : s.Busy ? "the agent is still answering" : "", s == null);
            return null;
        }

        _input.Text = "";
        try
        {
            string stop = await s.PromptAsync(text);
            _hub.Post(() => SetStatus($"turn ended: {stop}"));
            return stop;
        }
        catch (Exception ex)
        {
            _hub.Post(() =>
            {
                Append(s, new AgentSession.Line("error", ex.Message));
                SetStatus(ex.Message, true);
            });
            return null;
        }
    }

    private void Append(AgentSession s, AgentSession.Line line)
    {
        if (!_logs.TryGetValue(s, out RichTextLabel l) || !IsInstanceValid(l))
        {
            return;
        }

        string last = _lastKind.GetValueOrDefault(s, "");
        string text = AiHub.Esc(line.Text);
        switch (line.Kind)
        {
            case "message":
                if (last != "message")
                {
                    l.AppendText($"\n[color=#8fb8e0]{AiHub.Esc(s.Name)}:[/color] ");
                }

                l.AppendText(text);
                break;
            case "thought":
                if (last != "thought")
                {
                    l.AppendText("\n");
                }

                l.AppendText($"[color=#7a7a7a]{text}[/color]");
                break;
            case "user":
                l.AppendText($"\n[color=#e0b050]You:[/color] {text}");
                break;
            case "tool":
                l.AppendText($"\n[color=#b0a060]tool:[/color] {text}");
                break;
            case "plan":
                l.AppendText($"\n[color=#b0a060]{text}[/color]");
                break;
            case "error":
                l.AppendText($"\n[color=orange]{text}[/color]");
                break;
            default:
                l.AppendText($"\n[color=#7a7a7a]{text}[/color]");
                break;
        }

        _lastKind[s] = line.Kind;
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_status != null)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", error ? new Color(1f, 0.6f, 0.3f) : new Color(0.7f, 0.7f, 0.7f));
        }
    }
}
#endif
