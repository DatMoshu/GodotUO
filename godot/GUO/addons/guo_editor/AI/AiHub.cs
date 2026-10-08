#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// What the AI dock's three tabs share (ADR-0028): the running agent sessions, the endpoint book,
/// and the way work on a worker thread reaches the main thread. Anything a worker produces is
/// queued here and the dock runs it from <c>_Process</c>; no Godot object is touched off the main thread.
/// </summary>
public sealed class AiHub
{
    private readonly ConcurrentQueue<Action> _main = new();
    private bool _closed;

    internal EndpointBook Endpoints { get; private set; }

    /// <summary>Every third-party service endpoint and key (OpenAI-compatible, ComfyUI, Retro Diffusion, Ollama).</summary>
    public ServiceBook Services { get; private set; }

    /// <summary>The services changed (added, edited, removed): the Chat and Services tabs redraw.</summary>
    public event Action ServicesChanged;

    /// <summary>The read-only tools offered to chat models; the plugin fills it in (null: none).</summary>
    public AiToolHost Tools { get; set; }

    /// <summary>The picture of what is selected in the UO Inspector (client art), or null. Set by the plugin.</summary>
    public Func<Godot.Image> SelectionImage { get; set; }

    /// <summary>What the selection is called ("Statics 0x0E75"), or null.</summary>
    public Func<string> SelectionLabel { get; set; }

    public AiHub()
    {
        // Godot reconstructs [Tool] docks before the plugin restores the preference.
        // Keep those temporary objects inert; the plugin creates the live hub after applying it.
        if (AiFeatures.Enabled) UseBooks(new EndpointBook(), null);
    }

    /// <summary>Points the hub at other files (the smoke uses temporary ones); the user's own are the default.</summary>
    internal void UseBooks(EndpointBook endpoints, string servicesPath)
    {
        Endpoints = endpoints;
        Services = new ServiceBook(endpoints, servicesPath);
        NotifyServices();
    }

    /// <summary>The services changed: tell the tabs.</summary>
    public void NotifyServices()
    {
        ServicesChanged?.Invoke();
        SessionsChanged?.Invoke();
    }

    /// <summary>Running agents; changed only on the main thread.</summary>
    public List<AgentSession> Sessions { get; } = new();

    public event Action SessionsChanged;

    /// <summary>Shows a permission request to the user (set by the dock). Returns the ACP result.</summary>
    public Func<AgentSession, JsonNode, Task<JsonNode>> Permission { get; set; }

    /// <summary>Queues work for the main thread.</summary>
    public void Post(Action a)
    {
        if (!_closed && AiFeatures.Enabled) _main.Enqueue(a);
    }

    /// <summary>Runs queued work; called from the dock's <c>_Process</c>.</summary>
    public void Drain()
    {
        int budget = 200;
        while (budget-- > 0 && _main.TryDequeue(out Action a))
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Godot.GD.PushWarning($"[GUO editor] AI dock: {ex.Message}");
            }
        }
    }

    public void AddSession(AgentSession s)
    {
        Sessions.Add(s);
        s.Ask = p => Permission != null ? Permission(s, p) : Task.FromResult<JsonNode>(null);
        SessionsChanged?.Invoke();
    }

    /// <summary>A session became ready (or ended): the pickers rebuild.</summary>
    public void NotifySessions() => SessionsChanged?.Invoke();

    public void RemoveSession(AgentSession s)
    {
        if (Sessions.Remove(s))
        {
            s.Dispose();
            SessionsChanged?.Invoke();
        }
    }

    /// <summary>Kills every child process. Called when the dock closes and before an assembly reload.</summary>
    public void Shutdown()
    {
        _closed = true;
        Permission = null;
        while (_main.TryDequeue(out _)) { }
        foreach (AgentSession s in Sessions.ToArray())
        {
            s.Dispose();
        }

        Sessions.Clear();
    }

    /// <summary>The result that declines: the user closed the dialog, or the editor is shutting down.</summary>
    public static JsonNode Cancelled() => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

    /// <summary>The result that picks an option.</summary>
    public static JsonNode Selected(string optionId) =>
        new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };

    /// <summary>BBCode-safe text.</summary>
    public static string Esc(string s) => (s ?? "").Replace("[", "[lb]");
}
#endif
