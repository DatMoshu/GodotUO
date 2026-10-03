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

        // --- render modes and map layers (ADR-0027) ------------------------------
        foreach (string mode in new[] { "Off" }.Concat(_ctx.World.Modes?.ModeNames ?? WorldModeNames()))
        {
            string m = mode;
            Add("World", $"View: {m}", m == "Off" ? "World tab render mode: none" : "World tab render mode: recolour the world",
                $"world view mode render diagnostic overlay {m}",
                () =>
                {
                    _ctx.ShowWorldTab();
                    _ctx.World.SetViewMode(m);
                    SearchContext.Toast($"World view: {m}");
                }, bonus: 8);
        }

        foreach (string layer in _ctx.World.LayerNames)
        {
            string l = layer;
            Add("World", $"Layer: {l}", "World tab and minimap map layer", $"world map layer overlay show hide toggle {l}",
                () =>
                {
                    _ctx.ShowWorldTab();
                    bool on = !_ctx.World.LayerOn(l);
                    _ctx.World.SetLayer(l, on);
                    SearchContext.Toast($"World layer {l}: {(on ? "on" : "off")}");
                }, bonus: 8);
        }

        Add("World", "Scene pack", "write the frame, mode images and scene.json under build/scene_packs", "scene pack export screenshot review modes json",
            () =>
            {
                _ctx.ShowWorldTab();
                string dir = _ctx.World.WriteScenePack();
                SearchContext.Toast(dir == null ? "Scene pack: the world is not up" : $"Scene pack written to {dir}");
            });

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

        // --- the Multi Editor (ADR-0031) ------------------------------------------
        MultiEditView me = _ctx.MultiEdit;
        if (me != null)
        {
            void Show() => (_ctx.Plugin as GuoEditorPlugin)?.ShowMultiEditor();
            Add("Multi", "Multi Editor: open the tab", "edit houses, keeps and boats", "multi editor house keep boat tab build", Show, bonus: 10);
            Add("Multi", "Multi Editor: new multi", "a blank multi (asks first when there are unsaved changes)", "multi editor new blank house", () => { Show(); me.GuardUnsaved(me.NewMulti); });
            Add("Multi", "Multi Editor: open the Generate panel", "styles, generators and the live ghost preview", "multi editor generate panel style house wall roof stairs preview", () => { Show(); me.ShowGenerateTab(); });
            Add("Multi", "Multi Editor: apply the generated multi (replace)", "the Generate panel's preview replaces the multi, one undo step", "multi editor generate apply replace",
                async () => { Show(); me.ShowGenerateTab(); await me.ApplyGeneratedAsync(true); });
            Add("Multi", "Multi Editor: apply the generated multi (add)", "the Generate panel's preview is added, one undo step", "multi editor generate apply add",
                async () => { Show(); me.ShowGenerateTab(); await me.ApplyGeneratedAsync(false); });
            Add("Multi", "Multi Editor: rotate 90", "the selection, or the whole multi", "multi editor rotate turn 90 clockwise", async () => { Show(); await me.TransformAsync("rotate", 1); });
            Add("Multi", "Multi Editor: flip east-west", "mirror the selection, or the whole multi", "multi editor mirror flip east west x", async () => { Show(); await me.TransformAsync("mirror", axis: "x"); });
            Add("Multi", "Multi Editor: flip north-south", "mirror the selection, or the whole multi", "multi editor mirror flip north south y", async () => { Show(); await me.TransformAsync("mirror", axis: "y"); });
            Add("Multi", "Multi Editor: copy", "Ctrl+C", "multi editor copy selection clipboard", () => me.CopySelection());
            Add("Multi", "Multi Editor: cut", "Ctrl+X", "multi editor cut selection clipboard", () => me.CutSelection());
            Add("Multi", "Multi Editor: paste", "Ctrl+V: a ghost follows the pointer", "multi editor paste clipboard ghost", () => { Show(); me.PasteClipboard(); });
            Add("Multi", "Multi Editor: erase the whole stair or roof", "the group the selection touches", "multi editor erase group stair roof", () => me.EraseGroupOfSelection());
            Add("Multi", "Multi Editor: save selection as a stamp", "into the user's GUO folder", "multi editor stamp save selection library",
                () => { string n = $"stamp_{DateTime.Now:yyyyMMdd_HHmmss}"; SearchContext.Toast(me.SaveStamp(n) != null ? $"Saved stamp {n}" : "Select something first", EditorToaster.Severity.Info); });
            foreach (string stamp in me.Stamps())
            {
                string s = stamp;
                Add("Multi", $"Multi stamp: {s}", "place it with a ghost", $"multi editor stamp place {s}", () => { Show(); me.PlaceStamp(s); });
            }

            foreach ((string format, _, string label) in MultiEditView.LegacyFormats)
            {
                string f = format;
                Add("Multi", $"Multi Editor: import {label}", "another tool's multi file", $"multi editor import open {f} {label}", () => { Show(); me.ImportDialog(f); });
                Add("Multi", $"Multi Editor: export {label}", "write the multi in another tool's format", $"multi editor export save {f} {label}", () => { Show(); me.ExportDialog(f); });
            }

            Add("Multi", "Multi Editor: deploy to private shard", "write to the stage, place it on the private shard (asks first, never 2593)", "multi editor deploy private shard place prove",
                () => { Show(); me.RequestDeploy(); });
            Add("Multi", "Multi Editor: save description", "write build/multi/edit/NAME.multi.json", "multi editor save description json",
                () => { Show(); SearchContext.Toast($"Saved {me.SaveDescription()}"); });
            Add("Multi", "Multi Editor: write to stage", "into the staged data set, never the install", "multi editor write stage save uodata",
                async () => { Show(); var r = await me.SaveToStageAsync(); SearchContext.Toast(r.Ok ? $"{r.Name} = multi 0x{r.Id:X4}" : r.Error ?? "write failed", r.Ok ? EditorToaster.Severity.Info : EditorToaster.Severity.Warning); });
            Add("Multi", "Multi Editor: undo", "Ctrl+Z", "multi editor undo history", () => me.Doc.Undo());
            Add("Multi", "Multi Editor: redo", "Ctrl+Y", "multi editor redo history", () => me.Doc.Redo());
            Add("Multi", "Multi Editor: preview in World", "place the last written multi in the World tab", "multi editor preview world place", () => me.PreviewNow());
            foreach (MultiTool tool in Enum.GetValues<MultiTool>())
            {
                MultiTool t = tool;
                Add("Multi", $"Multi tool: {t}", "Multi Editor tool", $"multi editor tool {t}", () => { Show(); me.SetTool(t); });
            }

            foreach (StoryVision mode in Enum.GetValues<StoryVision>())
            {
                StoryVision m = mode;
                Add("Multi", $"Multi vision: {m}", "the client's per-story vision mode for the story being edited", $"multi editor vision story floor {m}",
                    () => { Show(); me.SetVision(me.Canvas.ActiveStory, m); });
            }

            Add("World", "World selection to multi", "take the Area tool's statics into a new multi", "world area selection multi statics house save",
                () =>
                {
                    int n = _ctx.World.SaveAreaAsMulti();
                    SearchContext.Toast(n > 0 ? $"{n} statics are in the Multi Editor" : "Pick an area first: World tool Area, two corner clicks",
                        n > 0 ? EditorToaster.Severity.Info : EditorToaster.Severity.Warning);
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

    private static IEnumerable<string> WorldModeNames() => WorldModeList.All().Select(m => m.Name);

    private void Add(string kind, string title, string hint, string tags, Action run, int bonus = 0)
    {
        _entries.Add(new SearchEntry { Kind = kind, Title = title, Hint = hint, Tags = tags, Key = kind + ":" + title, Bonus = 40 + bonus, Run = run }.Prepare());
    }
}
#endif
