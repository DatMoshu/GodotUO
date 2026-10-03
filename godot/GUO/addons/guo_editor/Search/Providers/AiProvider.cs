#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;

/// <summary>
/// The AI dock's commands (ADR-0028): a new chat, start an agent (each preset), the dock's tabs.
/// Starting an agent only starts the CLI and opens a session; nothing is prompted, so nothing is
/// spent until the user types a prompt.
/// </summary>
public sealed class AiProvider : SearchProvider
{
    private readonly SearchContext _ctx;
    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public AiProvider(SearchContext ctx)
    {
        _ctx = ctx;
    }

    public override string Name => "AI";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_built)
        {
            return;
        }

        _built = true;
        if (_ctx.Ai == null)
        {
            return;
        }

        Add("AI: new chat", "AI dock, Chat tab: clear it and type", "ai chat new ollama model llm ask", () => _ctx.Ai.NewChat());
        Add("AI: show queue", "AI dock: requests to running agent sessions and their replies", "ai queue agent request reply post", () => _ctx.Ai.ShowTab("Queue"));
        Add("AI: show agents", "AI dock: agents over ACP", "ai agents acp codex opencode claude gemini", () => _ctx.Ai.ShowTab("Agents"));
        foreach (AgentPreset preset in AgentCatalog.Presets)
        {
            if (preset.IsCustom)
            {
                continue;
            }

            AgentPreset p = preset;
            Add($"AI: start agent {p.Name}", $"starts {p.Command} over ACP; signs in with its own login ({p.Paid})",
                $"ai agent start acp {p.Id} {p.Command}", () => _ctx.Ai.StartAgent(p));
        }
    }

    private void Add(string title, string hint, string tags, Action run)
    {
        _entries.Add(new SearchEntry { Kind = "AI", Title = title, Hint = hint, Tags = tags, Key = "AI:" + title, Bonus = 30, Run = run }.Prepare());
    }
}
#endif
