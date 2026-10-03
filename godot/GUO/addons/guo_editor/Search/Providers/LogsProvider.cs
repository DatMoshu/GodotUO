#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;

/// <summary>
/// F3 commands for the Logs dock: "Logs: server", "Logs: client N" (1 to 4), "Logs: add file". Each only
/// brings a log forward (or asks for a file to follow); nothing here writes to a log.
/// </summary>
public sealed class LogsProvider : SearchProvider
{
    private readonly SearchContext _ctx;
    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public LogsProvider(SearchContext ctx)
    {
        _ctx = ctx;
    }

    public override string Name => "Logs";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_built)
        {
            return;
        }

        _built = true;
        if (_ctx.Logs == null)
        {
            return;
        }

        Add("Logs: server", "Logs dock: the selected server's log files", "logs log server shard modernuo console tail", () => Show("server", "the server"));
        for (int n = 1; n <= 4; n++)
        {
            int slot = n;
            Add($"Logs: client {n}", $"Logs dock: the console of client {n} started from the run bar (times in UTC)", $"logs log client {n} console tail utc", () => Show("client" + slot, $"client {slot}"));
        }

        Add("Logs: client files", "Logs dock: the client's own file logs (packets, crashes)", "logs log client files packets crash", () => Show("clientfiles", "the client files"));
        Add("Logs: Godot log", "Logs dock: the editor's and project's Godot log", "logs log godot editor output", () => Show("godot", "the Godot log"));
        Add("Logs: add file", "Logs dock: follow any other log file, read only", "logs log add file open tail follow", () => _ctx.Logs.AddFile());
    }

    private void Show(string key, string what)
    {
        if (!_ctx.Logs.ShowSource(key))
        {
            SearchContext.Toast($"No log for {what} yet: start it from the run bar first.");
        }
    }

    private void Add(string title, string hint, string tags, System.Action run)
    {
        _entries.Add(new SearchEntry { Kind = "Logs", Title = title, Hint = hint, Tags = tags, Key = "Logs:" + title, Bonus = 30, Run = run }.Prepare());
    }
}
#endif
