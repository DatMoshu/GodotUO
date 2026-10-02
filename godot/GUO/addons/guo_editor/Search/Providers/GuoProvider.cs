#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// The workbench's own commands: every UO Assets tab, each World tab toggle
/// and tool, undo and redo, reload project, the Bulk tab's export and verify,
/// the Shard dock's connect, and the run bar's Start server and Start
/// clients. The two that open windows (the run bar's) run only on Enter,
/// like everything else: nothing here acts on hover or on a half typed word.
/// </summary>
public sealed class GuoProvider : SearchProvider
{
    private readonly SearchContext _ctx;
    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public GuoProvider(SearchContext ctx)
    {
        _ctx = ctx;
    }

    public override string Name => "GUO commands";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override IEnumerable<SearchEntry> Suggested => _entries.Where(e => e.Kind == "GUO").Take(8);

    public override void Step(double ms)
    {
        if (_built)
        {
            return;
        }

        _built = true;

        // --- UO Assets tabs --------------------------------------------------
        foreach (AssetPanel panel in _ctx.Assets.Panels.ToList())
        {
            AssetPanel p = panel;
            string tab = p.Name;
            Add("GUO", $"UO Assets: {tab}", "open this tab of the UO Assets dock", $"assets tab panel {tab} browse",
                () =>
                {
                    _ctx.Assets.MakeVisible();
                    _ctx.Assets.ShowPanel(p);
                });
        }

        // --- World tab ---------------------------------------------------------
        foreach (string name in _ctx.World.ToggleNames)
        {
            string toggle = name;
            Add("World", $"{toggle}: toggle", "World tab layer or guide",
                $"world {toggle} layer guide show hide toggle switch {toggle}s",
                () =>
                {
                    _ctx.ShowWorldTab();
                    bool on = !(_ctx.World.GetToggle(toggle) ?? false);
                    _ctx.World.SetToggle(toggle, on);
                    SearchContext.Toast($"World: {toggle} {(on ? "on" : "off")}");
                });
        }

        foreach (WorldTool tool in Enum.GetValues<WorldTool>())
        {
            WorldTool t = tool;
            Add("World", $"Tool: {t}", "World tab, what a left click does", $"world tool brush {t}",
                () =>
                {
                    _ctx.ShowWorldTab();
                    _ctx.World.Tool = t;
                });
        }

        foreach (GUO.Game.Managers.Season season in Enum.GetValues<GUO.Game.Managers.Season>())
        {
            GUO.Game.Managers.Season s = season;
            Add("World", $"Season: {s}", "World tab, the game's seasonal graphics", $"world season weather {s}",
                () =>
                {
                    _ctx.ShowWorldTab();
                    _ctx.World.Season = s;
                });
        }

        for (int f = 0; f < 6; f++)
        {
            int facet = f;
            string[] names = { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "Ter Mur" };
            Add("World", $"Map: {names[f]} (map{f})", "World tab, switch facet", $"world facet map{f} {names[f]} switch",
                () => _ctx.ShowInWorld(facet, 1496, 1628));
        }

        Add("World", "Undo", "World tab edit history (Ctrl+Z)", "undo world edit history revert",
            () =>
            {
                _ctx.ShowWorldTab();
                _ctx.World.Editor.Undo();
            }, bonus: 10);
        Add("World", "Redo", "World tab edit history (Ctrl+Y)", "redo world edit history",
            () =>
            {
                _ctx.ShowWorldTab();
                _ctx.World.Editor.Redo();
            }, bonus: 10);
        Add("World", "Reload project", "re-read the world project's blocks from disk", "world project overlay reload refresh",
            () =>
            {
                _ctx.ShowWorldTab();
                int n = _ctx.World.ReloadOverlay();
                SearchContext.Toast($"World project reloaded: {n} block(s) laid over the map");
            }, bonus: 10);

        // --- export and verify (the Bulk tab; the install is never written) ----
        BulkPanel bulk = _ctx.Assets.Panel<BulkPanel>();
        if (bulk != null)
        {
            Add("GUO", "Export / Pack: open the Bulk tab", "unpack assets, pack a staged data set (never the install)",
                "export pack unpack stage bulk uopack",
                () =>
                {
                    _ctx.Assets.MakeVisible();
                    _ctx.Assets.ShowPanel(bulk);
                });
            Add("GUO", "Verify stage", "Bulk tab: verify the staged data set", "verify check stage bulk uodata",
                () =>
                {
                    _ctx.Assets.MakeVisible();
                    _ctx.Assets.ShowPanel(bulk);
                    bulk.RunVerify();
                });
        }

        // --- shard -------------------------------------------------------------
        Add("GUO", "Shard: connect (live)", "UO Shard dock: connect the editor bridge", "shard live connect bridge server link",
            () =>
            {
                _ctx.Shard.MakeVisible();
                bool ok = _ctx.Shard.Connect();
                SearchContext.Toast(ok ? "UO Shard: live" : "UO Shard: could not connect (is the shard's bridge running?)",
                    ok ? EditorToaster.Severity.Info : EditorToaster.Severity.Warning);
            });
        Add("GUO", "Shard: disconnect", "UO Shard dock: close the bridge", "shard live disconnect bridge",
            () =>
            {
                _ctx.Shard.Disconnect();
            });
        Add("GUO", "Show UO Shard dock", "the live tier's dock", "shard dock live panel",
            () => _ctx.Shard.MakeVisible());
        Add("GUO", "Show UO Inspector dock", "what the last pick or search selected", "inspector dock panel details",
            () => _ctx.Inspector.MakeVisible());
        Add("GUO", "Show UO Assets dock", "browse the client's assets", "assets dock panel browse",
            () => _ctx.Assets.MakeVisible());
        Add("GUO", "UO World tab", "the world viewer", "world tab screen map view",
            () => _ctx.ShowWorldTab(), bonus: 10);

        // --- the run bar: these open windows, so they run only on Enter ---------
        Add("GUO", "Start server", "run bar: the chosen shard in its own window", "run bar start shard server launch play",
            () => _ctx.Run.StartServerNow());
        Add("GUO", "Start clients", "run bar: GUO clients (the count chosen in the bar)", "run bar start client clients launch play game",
            () => _ctx.Run.StartClientsNow());

        // --- placeholder for the layout command another task provides ------------
        Add("GUO", "Reset GUO layout", "put the UO docks back where they start", "layout dock reset default arrange",
            () =>
            {
                Action reset = _ctx.ResetLayout;
                if (reset == null)
                {
                    SearchContext.Toast("Reset GUO layout is not available yet.", EditorToaster.Severity.Warning);
                }
                else
                {
                    reset();
                }
            });
    }

    private void Add(string kind, string title, string hint, string tags, Action run, int bonus = 0)
    {
        _entries.Add(new SearchEntry { Kind = kind, Title = title, Hint = hint, Tags = tags, Key = kind + ":" + title, Bonus = 40 + bonus, Run = run }.Prepare());
    }
}
#endif
