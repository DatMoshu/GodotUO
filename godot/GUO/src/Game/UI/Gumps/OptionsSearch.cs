// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner request 2026-11-21): searchable Options. A box under the
// page buttons finds a setting by name or keyword across every Options page
// and the GUO detail panels; picking a result opens its page, scrolls to the
// row and outlines it. Reads the built controls, so new rows are found with
// no registration. OptionsGump calls Attach once (one PORT DEVIATION seam).
using System;
using System.Collections.Generic;
using System.Text;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Renderer;
using GUO.Resources;
using GUO.Compat;

namespace GUO.Game.UI.Gumps
{
    internal static class OptionsSearch
    {
        internal const int RESULTS_PAGE = 14;
        private const int MAX_RESULTS = 80;

        internal static void Attach(OptionsGump gump)
        {
            var results = new ScrollArea(190, 20, 700 - 210, 420, true);
            var box = new SearchBox(gump, results) { X = 10, Y = 450 };
            gump.Add(results, RESULTS_PAGE);
            gump.Add(box);
        }

        // Page numbers are OptionsGump's own (its left buttons' ButtonParameter).
        internal static string PageName(int page) => page switch
        {
            1 => ResGumps.General,
            2 => ResGumps.Sound,
            3 => ResGumps.Video,
            4 => ResGumps.Macros,
            5 => ResGumps.Tooltip,
            6 => ResGumps.Fonts,
            7 => ResGumps.Speech,
            8 => ResGumps.CombatSpells,
            9 => ResGumps.Counters,
            10 => ResGumps.InfoBar,
            11 => ResGumps.Containers,
            12 => ResGumps.Experimental,
            13 => "GUO",
            _ => "Options"
        };

        private static string DetailName(GuoSection s) => s switch
        {
            GuoSection.Lighting => "GUO > Lighting and shadows",
            GuoSection.Weather => "GUO > Weather and ambience",
            GuoSection.World => "GUO > World map and water",
            _ => "GUO"
        };

        // Words players use for the same thing. A query word matches an entry
        // when the entry holds the word or any word of its group.
        private static readonly string[][] Synonyms =
        {
            new[] { "sound", "audio", "volume", "music", "sfx", "footstep" },
            new[] { "light", "lighting", "brightness", "bright", "dark", "night", "lamp", "torch" },
            new[] { "shadow", "shadows" },
            new[] { "zoom", "scale", "camera", "magnify" },
            new[] { "fps", "frame", "framerate", "performance", "vsync", "lag" },
            new[] { "color", "colour", "hue", "tint" },
            new[] { "font", "text", "letters" },
            new[] { "speech", "chat", "say", "talk", "journal", "message" },
            new[] { "health", "hp", "hits", "healthbar", "life" },
            new[] { "tooltip", "tooltips", "hint", "property", "properties" },
            new[] { "mouse", "cursor", "click", "pointer" },
            new[] { "key", "keyboard", "hotkey", "macro", "shortcut", "bind", "binding" },
            new[] { "window", "gump", "ui", "interface", "panel" },
            new[] { "water", "sea", "ocean", "wave", "shore", "coast" },
            new[] { "weather", "rain", "snow", "fog", "storm", "wind" },
            new[] { "container", "bag", "backpack", "inventory", "grid", "loot", "corpse" },
            new[] { "screen", "fullscreen", "resolution", "display", "monitor", "windowed" },
            new[] { "pad", "gamepad", "controller", "joystick" },
            new[] { "touch", "mobile", "phone", "tablet", "finger" },
            new[] { "name", "names", "overhead", "label" },
            new[] { "combat", "war", "attack", "fight", "pvp" },
            new[] { "spell", "spells", "magic", "cast", "casting" },
            new[] { "run", "walk", "movement", "move", "pathfind", "pathfinding" },
        };

        private static Dictionary<string, string[]> _synonymIndex;

        private static string[] Expand(string word)
        {
            if (_synonymIndex == null)
            {
                _synonymIndex = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (string[] group in Synonyms)
                    foreach (string w in group)
                        _synonymIndex[w] = group;
            }

            return _synonymIndex.TryGetValue(word, out string[] g) ? g : null;
        }

        internal sealed class Entry
        {
            public int Page;            // OptionsGump page, or 0 for a GUO detail panel
            public GuoSection Detail;   // the panel, when Page == 0
            public string Location;
            public string Text;
            public string Haystack;
            public Control Target;      // null for detail-panel rows (rebuilt on open)
            public ScrollArea Area;
            public int Score;
        }

        private static string TextOf(Control c) => c switch
        {
            Checkbox cb => cb.Text,
            NiceButton nb => nb.TextLabel?.Text,
            Label l => l.Text,
            _ => null
        };

        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (s.Length < 2 || s.Length > 160) return null;
            // Pure numbers and value readouts are not setting names.
            bool letter = false;
            foreach (char ch in s) if (char.IsLetter(ch)) { letter = true; break; }
            return letter ? s : null;
        }

        // The title Label of an OptionsGump.SettingsSection (Label then Line).
        private static string SectionTitle(Control c)
        {
            if (c.Children.Count >= 2 && c.Children[0] is Label l && c.Children[1] is Line)
                return Clean(l.Text);
            return null;
        }

        internal static void Collect(Control node, int page, GuoSection detail, string location, string section,
            ScrollArea area, List<Entry> into, HashSet<string> seen)
        {
            foreach (Control child in node.Children)
            {
                if (child == null || child.IsDisposed || !child.IsVisible) continue;

                string own = SectionTitle(child);
                string text = Clean(TextOf(child));
                if (text != null)
                {
                    string key = location + "|" + text;
                    if (seen.Add(key))
                    {
                        string where = section != null && !string.Equals(section, text, StringComparison.Ordinal)
                            ? location + " > " + section : location;
                        into.Add(new Entry
                        {
                            Page = page, Detail = detail, Location = where, Text = text,
                            Haystack = (text + " \u0001 " + where).ToLowerInvariant(),
                            Target = page != 0 ? child : null, Area = area
                        });
                    }
                }

                if (child is Checkbox || child is NiceButton) continue;
                Collect(child, page, detail, location, own ?? section, area, into, seen);
            }
        }

        internal static List<Entry> BuildIndex(OptionsGump gump, ScrollArea results, List<Entry> details)
        {
            var list = new List<Entry>(512);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Control c in gump.Children)
            {
                if (c is ScrollArea area && c != results && c.Page > 0 && c.Page != RESULTS_PAGE)
                    Collect(area, c.Page, GuoSection.All, PageName(c.Page), null, area, list, seen);
            }

            if (details != null) list.AddRange(details);
            return list;
        }

        // The GUO detail panels are separate gumps; build each once, offscreen,
        // read its rows, and drop it. Nothing is added to the UI or the profile.
        internal static List<Entry> BuildDetailIndex(World world)
        {
            var list = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuoSection s in new[] { GuoSection.Lighting, GuoSection.Weather, GuoSection.World })
            {
                UoPlusOptionsGump temp = null;
                try
                {
                    temp = new UoPlusOptionsGump(world, ProfileManager.CurrentProfile, s);
                    foreach (Control c in temp.Children)
                        if (c is ScrollArea a)
                            Collect(a, 0, s, DetailName(s), null, null, list, seen);
                }
                catch (Exception e)
                {
                    Utility.Logging.Log.Warn($"[options search] detail panel {s} not indexed: {e.Message}");
                }
                finally
                {
                    temp?.Dispose();
                }
            }

            return list;
        }

        internal static List<Entry> Query(List<Entry> index, string query)
        {
            var hits = new List<Entry>();
            if (string.IsNullOrWhiteSpace(query)) return hits;
            string q = query.Trim().ToLowerInvariant();
            string[] words = q.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (Entry e in index)
            {
                int split = e.Haystack.IndexOf('\u0001');
                string text = split > 0 ? e.Haystack.Substring(0, split) : e.Haystack;
                bool all = true, direct = true;
                foreach (string w in words)
                {
                    if (text.Contains(w, StringComparison.Ordinal)) continue;
                    direct = false;
                    if (e.Haystack.Contains(w, StringComparison.Ordinal)) continue;
                    string[] group = Expand(w);
                    bool found = false;
                    if (group != null)
                        foreach (string g in group)
                            if (e.Haystack.Contains(g, StringComparison.Ordinal)) { found = true; break; }
                    if (!found) { all = false; break; }
                }

                if (!all) continue;
                e.Score = text.StartsWith(q, StringComparison.Ordinal) ? 4
                    : text.Contains(q, StringComparison.Ordinal) ? 3
                    : direct ? 2 : 1;
                hits.Add(e);
            }

            hits.Sort((a, b) => b.Score != a.Score ? b.Score - a.Score : a.Text.Length - b.Text.Length);
            if (hits.Count > MAX_RESULTS) hits.RemoveRange(MAX_RESULTS, hits.Count - MAX_RESULTS);
            return hits;
        }

        private sealed class SearchBox : Control
        {
            private readonly OptionsGump _gump;
            private readonly ScrollArea _results;
            private readonly StbTextBox _text;
            private readonly Label _placeholder;
            private List<Entry> _details;
            private string _lastQuery = string.Empty;
            private int _returnPage = 1;

            // A scroll asked for before its page has laid out; applied on the next updates.
            private ScrollArea _pendingArea;
            private Control _pendingTarget;
            private int _pendingFrames;

            private Highlight _highlight;

            public SearchBox(OptionsGump gump, ScrollArea results)
            {
                _gump = gump;
                _results = results;
                Width = 140;
                Height = 26;
                AcceptMouseInput = false;
                WantUpdateSize = false;

                Add(new ResizePic(0x0BB8) { Width = Width, Height = Height });
                _placeholder = new Label("Search settings...", true, 0x0386, Width - 10, 0xFF,
                    FontStyle.BlackBorder | FontStyle.Cropped) { X = 5, Y = 5 };
                Add(_placeholder);
                _text = new StbTextBox(0xFF, 40, Width - 10, true, FontStyle.Cropped | FontStyle.BlackBorder, 0xFFFF)
                {
                    X = 4, Y = 4, Width = Width - 8, Height = Height - 8
                };
                _text.TextChanged += (_, _) => OnQueryChanged();
                Add(_text);
            }

            private void OnQueryChanged()
            {
                string q = _text.Text ?? string.Empty;
                _placeholder.IsVisible = q.Length == 0;
                if (q == _lastQuery) return;
                bool wasEmpty = _lastQuery.Trim().Length == 0;
                _lastQuery = q;

                if (q.Trim().Length == 0)
                {
                    if (_gump.ActivePage == RESULTS_PAGE)
                    {
                        _gump.ChangePage(_returnPage);
                        SelectNav(_returnPage);
                    }
                    return;
                }

                if (wasEmpty && _gump.ActivePage != RESULTS_PAGE)
                    _returnPage = _gump.ActivePage > 0 ? _gump.ActivePage : 1;

                Refresh();
                if (_gump.ActivePage != RESULTS_PAGE) _gump.ChangePage(RESULTS_PAGE);
                SelectNav(RESULTS_PAGE);
            }

            // Highlights the left page button of a page; none while results show.
            private void SelectNav(int page)
            {
                foreach (Control c in _gump.Children)
                {
                    if (c is not NiceButton nb || nb.X != 10) continue;
                    bool mine = page != RESULTS_PAGE && nb.ButtonParameter == page && nb.TextLabel?.Text == PageName(page);
                    if (mine) nb.IsSelected = true;
                    else if (page == RESULTS_PAGE) nb.IsSelected = false;
                }
            }

            private void Refresh()
            {
                _details ??= BuildDetailIndex(_gump.World);
                List<Entry> hits = Query(BuildIndex(_gump, _results, _details), _lastQuery);
                _results.Clear();

                var header = new Label(hits.Count == 0
                        ? $"No setting matches \"{_lastQuery.Trim()}\"."
                        : $"{hits.Count}{(hits.Count >= MAX_RESULTS ? "+" : "")} settings match \"{_lastQuery.Trim()}\". Click one to go to it.",
                    true, 0x0386, 460, 0xFF, FontStyle.BlackBorder) { X = 5, Y = 2 };
                _results.Add(header);

                int y = header.Height + 8;
                foreach (Entry e in hits)
                {
                    var row = new ResultRow(e, 455, this) { X = 5, Y = y };
                    _results.Add(row);
                    y += row.Height + 2;
                }
            }

            internal void Go(Entry e)
            {
                if (e.Page == 0)
                {
                    UoPlusOptionsGump.Open(_gump.World, e.Detail);
                    var panel = UIManager.GetGump<UoPlusOptionsGump>();
                    if (panel == null) return;
                    foreach (Control c in panel.Children)
                    {
                        if (c is not ScrollArea a) continue;
                        Control target = Find(a, e.Text);
                        if (target != null) QueueScroll(a, target);
                    }
                    return;
                }

                _gump.ChangePage(e.Page);
                SelectNav(e.Page);

                if (e.Target != null && !e.Target.IsDisposed && e.Area != null) QueueScroll(e.Area, e.Target);
            }

            private static Control Find(Control node, string text)
            {
                foreach (Control child in node.Children)
                {
                    if (child == null || !child.IsVisible) continue;
                    if (Clean(TextOf(child)) == text) return child;
                    if (child is Checkbox || child is NiceButton) continue;
                    Control deeper = Find(child, text);
                    if (deeper != null) return deeper;
                }
                return null;
            }

            private void QueueScroll(ScrollArea area, Control target)
            {
                _pendingArea = area;
                _pendingTarget = target;
                _pendingFrames = 3; // a page shown for the first time lays out (and sizes its scrollbar) on its first update
                ApplyScroll();
            }

            private static (int x, int y) OffsetIn(ScrollArea area, Control c)
            {
                int x = 0, y = 0;
                for (Control p = c; p != null && p != area; p = p.Parent)
                {
                    x += p.X;
                    y += p.Y;
                }
                return (x, y);
            }

            private void ApplyScroll()
            {
                if (_pendingArea == null || _pendingArea.IsDisposed || _pendingTarget == null || _pendingTarget.IsDisposed)
                {
                    _pendingArea = null;
                    return;
                }

                (int x, int y) = OffsetIn(_pendingArea, _pendingTarget);
                if (_pendingArea.Children.Count > 0 && _pendingArea.Children[0] is ScrollBarBase bar)
                    bar.Value = Math.Max(0, y - 40);

                if (_highlight == null || _highlight.IsDisposed || _highlight.Parent != _pendingArea)
                {
                    _highlight?.Dispose();
                    _highlight = new Highlight();
                    _pendingArea.Add(_highlight);
                }

                int w = Math.Max(_pendingTarget.Width, 60);
                w = Math.Min(w, _pendingArea.Width - 20 - x);
                _highlight.Place(x - 3, y - 2, Math.Max(w + 6, 40), _pendingTarget.Height + 4);

                if (--_pendingFrames <= 0) _pendingArea = null;
            }

            public override void Update()
            {
                base.Update();
                if (_pendingArea != null) ApplyScroll();
            }
        }

        private sealed class ResultRow : Control
        {
            private readonly Entry _entry;
            private readonly SearchBox _owner;
            private readonly Label _title;

            public ResultRow(Entry entry, int width, SearchBox owner)
            {
                _entry = entry;
                _owner = owner;
                AcceptMouseInput = true;
                CanMove = false;
                WantUpdateSize = false;
                Width = width;

                _title = new Label(entry.Text, true, 0xFFFF, width - 10, 0xFF, FontStyle.BlackBorder | FontStyle.Cropped)
                    { X = 4, Y = 2 };
                var where = new Label(entry.Location, true, 0x0386, width - 10, 0xFF,
                    FontStyle.BlackBorder | FontStyle.Cropped) { X = 14, Y = _title.Height + 2 };
                Add(_title);
                Add(where);
                Add(new Line(0, where.Y + where.Height + 3, width - 20, 1, 0xFF4a4a4a));
                Height = where.Y + where.Height + 4;
            }

            public override void Update()
            {
                base.Update();
                _title.Hue = MouseIsOver ? (ushort)0x0035 : (ushort)0xFFFF;
            }

            protected override void OnMouseUp(int x, int y, MouseButtonType button)
            {
                if (button == MouseButtonType.Left) _owner.Go(_entry);
            }
        }

        // A yellow outline around the found row that fades after a few seconds.
        private sealed class Highlight : Control
        {
            private uint _until;

            public Highlight()
            {
                AcceptMouseInput = false;
                CanMove = false;
                WantUpdateSize = false;
            }

            public void Place(int x, int y, int w, int h)
            {
                X = x; Y = y; Width = w; Height = h;
                Clear();
                uint gold = 0xFF33CCFF; // ABGR packed: opaque yellow-gold
                Add(new Line(0, 0, w, 2, gold));
                Add(new Line(0, h - 2, w, 2, gold));
                Add(new Line(0, 0, 2, h, gold));
                Add(new Line(w - 2, 0, 2, h, gold));
                _until = Time.Ticks + 3500;
                IsVisible = true;
            }

            public override void Update()
            {
                base.Update();
                if (IsVisible && Time.Ticks > _until) IsVisible = false;
            }
        }
    }
}
