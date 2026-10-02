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
        Ask("1434 1699", "Place", e => e.Target == (0, 1434, 1699));
        Ask("1434 1699 0 map1", "Place", e => e.Target == (1, 1434, 1699));
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
