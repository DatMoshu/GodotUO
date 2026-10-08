#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;

/// <summary>
/// Phase 4b's smoke stage: the F3 search on the real install. Waits for the
/// background index to finish, then types queries into the popup's own
/// pipeline and asserts what comes out on top: "backpack" and "0x0E75" and
/// "3701" find the static, "statics" and "grid" the World tab's toggles, a
/// coordinate a Place, "project settings" an item of the editor's own menu;
/// "bp" and a typo ("bakcpack") still find the backpack. It then runs two
/// harmless entries (open the Gumps tab; open the backpack in Art) and checks
/// the Assets dock and the UO Inspector followed. Windowed, it also saves a
/// frame of the open popup to the output folder.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _searchReport = new();
    private readonly Stopwatch _searchClock = Stopwatch.StartNew();
    private int _searchPhase;
    private HashSet<ulong> _settingsVisibleBefore = new();
    private Window _settingsWindow;

    private void SearchFail(string why)
    {
        _searchReport["ok"] = false;
        _failures.Add($"Search: {why}");
    }

    /// <summary>One tick of the stage; true when it is finished.</summary>
    private bool StepSearch()
    {
        if (Search == null)
        {
            SearchFail("the F3 popup was not created");
            _report["search"] = _searchReport;
            return true;
        }

        switch (_searchPhase)
        {
            case 0:
                if (!Search.Index.Ready && _searchClock.Elapsed.TotalSeconds < 150)
                {
                    return false;
                }

                _searchReport["index_ms"] = _searchClock.ElapsedMilliseconds;
                _searchReport["index_ready"] = Search.Index.Ready;
                if (!Search.Index.Ready)
                {
                    SearchFail($"the index was still building after 150 s: {Search.Index.Pending}");
                }

                RunSearchChecks();
                _report["search"] = _searchReport;
                // First frame of the popup: the status line is inside the panel.
                Search.Open("backpack");
                float overflow = Search.StatusOverflow();
                Search.Close();
                _searchReport["status_overflow_px"] = overflow;
                if (overflow > 0.5f)
                {
                    SearchFail($"the popup's status line is {overflow} px below the panel's bottom edge on its first frame");
                }

                _frames = 0;
                _searchPhase = 3;
                RunSettingsEntry("ProjectSetting:application/config/name");
                return false;

            case 3:
            case 4:
                if (_frames < 15)
                {
                    return false;
                }

                try
                {
                    CheckSettingsEntry(_searchPhase == 3 ? "project" : "editor", _searchPhase == 3 ? "Project Settings" : "Editor Settings");
                }
                finally
                {
                    // Native editor dialogs are reused, not ours to free. Hide only
                    // the window this test brought from hidden to visible.
                    if (_settingsWindow != null && IsInstanceValid(_settingsWindow))
                    {
                        _settingsWindow.Hide();
                    }
                }

                _searchPhase += 2;
                _frames = 0;
                return false;

            case 5:
            case 6:
                // Let Godot release native exclusive-child ownership before
                // opening the next dialog or advancing to the ACP fixture.
                if (_frames < 3) return false;
                string which = _searchPhase == 5 ? "project" : "editor";
                bool closed = _settingsWindow != null && IsInstanceValid(_settingsWindow) && !_settingsWindow.Visible;
                _searchReport[$"settings_{which}_closed"] = closed;
                if (!closed) SearchFail($"the test-opened {which} settings window did not close");
                _settingsWindow = null;
                if (_searchPhase == 5)
                {
                    _frames = 0;
                    _searchPhase = 4;
                    RunSettingsEntry("EditorSetting:");
                    return false;
                }

                bool noExclusive = !GodotUi.All<Window>(GetTree().Root).Any(w => w.Visible && w.Exclusive);
                _searchReport["settings_no_leftover_exclusive"] = noExclusive;
                if (!noExclusive) SearchFail("an exclusive child window remains before the ACP permission fixture");

                if (Headless)
                {
                    return true;
                }

                Search.Open();
                Search.SetQuery("backpack");
                _frames = 0;
                _searchPhase = 1;
                return false;

            default:
                if (_frames < 25)
                {
                    return false;
                }

                Image frame = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
                if (frame == null || frame.IsEmpty())
                {
                    SearchFail("could not capture the popup");
                }
                else
                {
                    Directory.CreateDirectory(_out);
                    string path = Path.Combine(_out, "search_backpack.png");
                    frame.SavePng(path);
                    _searchReport["screenshot"] = path;
                }

                Search.Close();
                return true;
        }
    }

    private void RunSettingsEntry(string key)
    {
        _settingsWindow = null;
        _settingsVisibleBefore = GodotUi.All<Window>(GetTree().Root).Where(w => w.Visible)
            .Select(w => w.GetInstanceId()).ToHashSet();
        // A preexisting modal belongs to another workflow. Fail without closing
        // it or trying to open a second exclusive child on top of it.
        if (GodotUi.All<Window>(GetTree().Root).Any(w => w.Visible && w.Exclusive))
        {
            SearchFail($"cannot test {key} while a preexisting exclusive window is visible");
            return;
        }
        GodotSettingsProvider settings = Search.Index.Providers.OfType<GodotSettingsProvider>().First();
        SearchEntry entry = settings.Entries.FirstOrDefault(e => key.EndsWith(":") ? e.Key.StartsWith(key) : e.Key == key);
        if (entry == null)
        {
            SearchFail($"no settings entry {key}");
            return;
        }

        entry.Run();
    }

    // Enter on a settings entry opens the dialog and fills its search box.
    private void CheckSettingsEntry(string which, string dialogText)
    {
        string dialogClass = which == "project" ? "ProjectSettingsEditor" : "EditorSettingsDialog";
        _settingsWindow = GodotUi.All<Window>(GetTree().Root).FirstOrDefault(w => w.Visible
            && w.GetClass() == dialogClass && !_settingsVisibleBefore.Contains(w.GetInstanceId()));
        _searchReport[$"settings_{which}_opened"] = _settingsWindow != null;
        if (_settingsWindow == null) SearchFail($"the test did not open its own {dialogText} window");
        GodotSettingsProvider settings = Search.Index.Providers.OfType<GodotSettingsProvider>().First();
        var open = settings.LastOpen;
        _searchReport[$"settings_{which}_dialog"] = open?.Dialog;
        _searchReport[$"settings_{which}_search"] = open?.Search;
        if (open == null || open.Value.Dialog == null || string.IsNullOrEmpty(open.Value.Search))
        {
            SearchFail($"a {which} settings entry did not open {dialogText} with its search filled: {open?.Dialog} / {open?.Search}");
        }
    }

    private void RunSearchChecks()
    {
        SearchIndex index = Search.Index;
        index.Refresh();
        _searchReport["providers"] = index.Providers.ToDictionary(p => p.Name, p => (object)p.Entries.Count());

        var queries = new List<object>();
        _searchReport["queries"] = queries;

        List<SearchGroup> Ask(string text, string wantKind, Func<SearchEntry, bool> also = null)
        {
            var sw = Stopwatch.StartNew();
            List<SearchGroup> groups = Search.SetQuery(text);
            SearchEntry top = SearchIndex.Top(groups);
            bool ok = top != null && top.Kind == wantKind && (also == null || also(top));
            queries.Add(new Dictionary<string, object>
            {
                ["query"] = text,
                ["want"] = wantKind,
                ["top_kind"] = top?.Kind,
                ["top"] = top?.Title,
                ["hint"] = top?.Hint,
                ["ms"] = sw.ElapsedMilliseconds,
                ["ok"] = ok,
            });
            if (!ok)
            {
                SearchFail($"'{text}': wanted {wantKind} on top, got {top?.Kind} '{top?.Title}'");
            }

            return groups;
        }

        bool Has(List<SearchGroup> groups, string kind, string titlePart) =>
            groups.Any(g => g.Items.Any(i => i.Entry.Kind == kind && i.Entry.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase)));

        Ask("backpack", "Static", e => e.Title == "backpack");
        Ask("0x0E75", "Static", e => e.Hint.StartsWith("0x0E75", StringComparison.Ordinal));
        Ask("3701", "Static", e => e.Hint.StartsWith("0x0E75", StringComparison.Ordinal));
        Ask("statics", "World", e => e.Title.StartsWith("Statics", StringComparison.Ordinal));
        Ask("grid", "World", e => e.Title.StartsWith("Grid", StringComparison.Ordinal));
        Ask("view walkability", "World", e => e.Title == "View: Walkability");
        Ask("view height", "World", e => e.Title == "View: Height");
        Ask("layer regions", "World", e => e.Title == "Layer: Regions");
        Ask("layer spawns", "World", e => e.Title == "Layer: Spawns");
        Ask("1434 1699", "Place", e => e.Target == (0, 1434, 1699));
        Ask("1434 1699 0 map1", "Place", e => e.Target == (1, 1434, 1699));
        Ask("store publish", "Store", e => e.Title == "Store: publish a pack");
        Ask("store server content", "Store", e => e.Title == "Store: server content");
        Ask("store approve key", "Store", e => e.Title == "Store: approve a catalogue key");
        Ask("project settings", "Menu");
        Ask("gump 100", "Gump");
        Ask("hue 33", "Hue");

        // "bp" may rank other things first; it only has to find the backpack.
        bool bpFound = Has(Search.SetQuery("bp"), "Static", "backpack");
        bool typoFound = Has(Search.SetQuery("bakcpack"), "Static", "backpack");
        queries.Add(new Dictionary<string, object> { ["query"] = "bp", ["finds_backpack"] = bpFound });
        queries.Add(new Dictionary<string, object> { ["query"] = "bakcpack", ["finds_backpack"] = typoFound });
        if (!bpFound)
        {
            SearchFail("'bp' did not find the backpack");
        }

        if (!typoFound)
        {
            SearchFail("'bakcpack' (one typo) did not find the backpack");
        }

        _searchReport["menu_items"] = index.Providers.OfType<GodotMenuProvider>().First().Entries.Count();
        if ((int)_searchReport["menu_items"] == 0)
        {
            SearchFail("no editor menu items were found");
        }

        // Harmless action 1: the Gumps tab comes forward.
        Search.SetQuery("assets gumps");
        SearchEntry tab = SearchIndex.Top(Search.Groups.ToList());
        if (tab == null || tab.Title != "UO Assets: Gumps")
        {
            SearchFail($"'assets gumps' gave '{tab?.Title}'");
        }
        else
        {
            Search.Activate(tab, now: true);
            if (!_assets.Panel<GumpPanel>().Visible)
            {
                SearchFail("running 'UO Assets: Gumps' did not bring the Gumps tab forward");
            }
        }

        // Harmless action 2: the backpack opens in Art and the Inspector shows it.
        SearchEntry pack = SearchIndex.Top(Search.SetQuery("backpack"));
        if (pack == null)
        {
            SearchFail("no backpack to open");
        }
        else
        {
            Search.Activate(pack, now: true);
            ArtPanel art = _assets.Panel<ArtPanel>();
            // There are several backpacks (0x09B2, 0x0E75, ...): the one opened is the top result's.
            int want = Convert.ToInt32(pack.Hint[2..6], 16);
            if (art.Selected != want || _inspector.Current?.Source != "Art")
            {
                SearchFail($"opening the backpack left Art on {art.Selected} (wanted {want}) and the Inspector on {_inspector.Current?.Source}");
            }
        }

        _searchReport["history_entries"] = Search.Index.History.Count;
        if (Search.Index.History.Count < 2)
        {
            SearchFail("the history did not record the two entries run");
        }

        _searchReport["ok"] = !_searchReport.ContainsKey("ok");
        Search.SetQuery("");
    }
}
#endif
