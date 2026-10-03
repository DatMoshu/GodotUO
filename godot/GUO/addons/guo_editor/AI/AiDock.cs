#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock (ADR-0028, docs/editor_plan.md): Chat with local and OpenAI-compatible models,
/// Agents started over ACP, and the request Queue. It owns every child process it starts and
/// kills them in <see cref="Shutdown"/>, which the plugin calls when the dock closes and before
/// an assembly reload.
/// </summary>
[Tool]
public partial class AiDock : EditorDock
{
    private readonly AiHub _hub = new();
    private TabContainer _tabs;
    private AiChatTab _chat;
    private AiAgentsTab _agents;
    private AiQueueTab _queue;
    private readonly Queue<(AgentSession Session, JsonNode Params, TaskCompletionSource<JsonNode> Result)> _asks = new();
    private AcceptDialog _dialog;
    private TaskCompletionSource<JsonNode> _dialogResult;
    private bool _ready;

    public AiChatTab Chat => _chat;
    public AiAgentsTab Agents => _agents;
    public AiQueueTab Queue => _queue;
    internal AiHub Hub => _hub;

    /// <summary>The permission dialog on screen, or null (the smoke answers it).</summary>
    public AcceptDialog PermissionDialog => _dialog;

    public AiDock()
    {
        Name = "UOAI";
        Title = "AI";
        LayoutKey = "guo_ai";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Vertical | DockLayout.Floating;
        IconName = "Script";
    }

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(480, 220) };
        root.AddChild(_tabs);
        _chat = new AiChatTab(_hub);
        _agents = new AiAgentsTab(_hub);
        _queue = new AiQueueTab(_hub);
        _tabs.AddChild(_chat);
        _tabs.AddChild(_agents);
        _tabs.AddChild(_queue);
        _hub.Permission = AskPermission;
    }

    public override void _Process(double delta) => _hub.Drain();

    /// <summary>Brings a tab forward: "Chat", "Agents" or "Queue".</summary>
    public void ShowTab(string name)
    {
        MakeVisible();
        for (int i = 0; i < _tabs.GetTabCount(); i++)
        {
            if (_tabs.GetChild(i).Name == name)
            {
                _tabs.CurrentTab = i;
            }
        }
    }

    /// <summary>The F3 command "AI: start agent X": shows the Agents tab and starts it (the CLI's own login pays).</summary>
    public void StartAgent(AgentPreset preset)
    {
        ShowTab("Agents");
        _ = _agents.StartPresetAsync(preset);
    }

    /// <summary>The F3 command "AI: new chat".</summary>
    public void NewChat()
    {
        ShowTab("Chat");
        _chat.NewChat();
    }

    // --- permission requests from agents -------------------------------------------------------

    private Task<JsonNode> AskPermission(AgentSession session, JsonNode request)
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hub.Post(() =>
        {
            _asks.Enqueue((session, request, tcs));
            ShowNextAsk();
        });
        return tcs.Task;
    }

    private void ShowNextAsk()
    {
        if (_dialog != null || _asks.Count == 0)
        {
            return;
        }

        var (session, p, result) = _asks.Dequeue();
        string title = (string)p?["toolCall"]?["title"] ?? "an action";
        string kind = (string)p?["toolCall"]?["kind"] ?? "";
        var text = new StringBuilder($"{session.Name} wants to do this:\n\n{title}");
        if (kind.Length > 0)
        {
            text.Append($"\n(kind: {kind})");
        }

        string raw = p?["toolCall"]?["rawInput"]?.ToJsonString();
        if (!string.IsNullOrEmpty(raw))
        {
            text.Append("\n\n").Append(raw.Length > 700 ? raw[..700] + "..." : raw);
        }

        text.Append("\n\nEach action is approved on its own: there is no \"always\" here.");

        _dialog = new AcceptDialog { Title = $"{session.Name} asks permission", DialogText = text.ToString(), DialogAutowrap = true, Exclusive = true };
        _dialog.GetOkButton().Visible = false;
        _dialogResult = result;
        if (p?["options"] is JsonArray options)
        {
            foreach (JsonNode o in options)
            {
                string k = (string)o?["kind"] ?? "";
                if (k.EndsWith("_always", StringComparison.Ordinal))
                {
                    continue;
                }

                _dialog.AddButton((string)o?["name"] ?? k, k.StartsWith("reject", StringComparison.Ordinal), (string)o?["optionId"] ?? "");
            }
        }

        AcceptDialog dlg = _dialog;
        dlg.CustomAction += action => FinishAsk(dlg, AiHub.Selected(action.ToString()));
        dlg.Canceled += () => FinishAsk(dlg, AiHub.Cancelled());
        dlg.Confirmed += () => FinishAsk(dlg, AiHub.Cancelled());
        EditorInterface.Singleton.GetBaseControl().AddChild(dlg);
        dlg.PopupCentered(new Vector2I(520, 240));
    }

    private void FinishAsk(AcceptDialog dlg, JsonNode answer)
    {
        if (dlg != _dialog)
        {
            return;
        }

        _dialogResult?.TrySetResult(answer);
        _dialog = null;
        _dialogResult = null;
        dlg.Hide();
        dlg.QueueFree();
        ShowNextAsk();
    }

    /// <summary>Releases everything: declines open permission requests, kills agent processes, stops streams.</summary>
    public void Shutdown()
    {
        _hub.Permission = null;
        _dialogResult?.TrySetResult(AiHub.Cancelled());
        while (_asks.Count > 0)
        {
            _asks.Dequeue().Result.TrySetResult(AiHub.Cancelled());
        }

        if (_dialog != null && IsInstanceValid(_dialog))
        {
            _dialog.Hide();
            _dialog.QueueFree();
        }

        _dialog = null;
        _chat?.Shutdown();
        _queue?.Shutdown();
        SetProcess(false);
        _hub.Shutdown();
    }
}
#endif
