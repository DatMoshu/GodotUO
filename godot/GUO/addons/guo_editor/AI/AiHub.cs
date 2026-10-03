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

    internal EndpointBook Endpoints { get; } = new();

    /// <summary>Running agents; changed only on the main thread.</summary>
    public List<AgentSession> Sessions { get; } = new();

    public event Action SessionsChanged;

    /// <summary>Shows a permission request to the user (set by the dock). Returns the ACP result.</summary>
    public Func<AgentSession, JsonNode, Task<JsonNode>> Permission { get; set; }

    /// <summary>Queues work for the main thread.</summary>
    public void Post(Action a) => _main.Enqueue(a);

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
        Permission = null;
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
