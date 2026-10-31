#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

public partial class EditorSmoke
{
    private void CheckGridRefreshWork()
    {
        if (!_data.IsLoaded) return;
        var cases = new Dictionary<string, object>();
        _report["grid_refresh"] = cases;
        foreach (var scenario in new[]
        {
            ("page0", "", 0, 500, true, false),
            ("page1", "", 1, 500, true, false),
            ("last_page", "", 2, 500, true, false),
            ("numeric", "260", 0, 500, true, false),
            ("missing_numeric", "900", 0, 500, true, false),
            ("name_page1", "even", 1, 500, true, false),
            ("name_selected482", "even", 1, 500, true, false),
            ("no_results", "absent", 0, 500, true, false),
            ("shrinking", "", 2, 100, true, false),
            ("removed_selection", "260", 0, 200, true, false),
            ("hidden", "", 0, 500, false, false),
            ("reparented", "", 1, 500, true, true),
        })
        {
            var (name, query, steps, count, visible, reparent) = scenario;
            var panel = new SmokeGridPanel();
            panel.Attach(_data);
            AddChild(panel);
            try
            {
                panel.Search(query);
                var list = panel.GetChildren().OfType<ItemList>().Single();
                var next = panel.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == ">");
                for (int i = 0; i < steps; i++) next.EmitSignal(Button.SignalName.Pressed);
                if (name == "name_selected482")
                {
                    list.Select(1);
                    list.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
                    if (panel.Selected != 482) throw new InvalidOperationException("fixture did not select 482");
                }
                panel.Visible = visible;
                if (reparent) { RemoveChild(panel); AddChild(panel); }
                int? selected = panel.Selected;
                panel.IdCount = count;
                var inspected = new List<string>();
                var selectedDuringInspect = new List<int?>();
                panel.Inspect += inspection => { inspected.Add(inspection.Id); selectedDuringInspect.Add(panel.Selected); };
                panel.ResetCounts();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                panel.OnAssetsChanged();
                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                int[] ids = Enumerable.Range(0, list.ItemCount).Select(i => (int)(long)list.GetItemMetadata(i)).ToArray();
                string status = panel.FindChildren("*", "Label", true, false).OfType<Label>().Single().Text;
                // Fiddler's workbench retains highlights on a rebuilt page.
                // A search callback must not highlight its temporary first result
                // instead of the saved selection (482 on the second name page).
                int[] expectedHighlights = name switch
                {
                    "numeric" => new[] { 20 },
                    "name_selected482" => new[] { 1 },
                    _ => Array.Empty<int>(),
                };
                bool ok = panel.Selected == selected && panel.IdScans == 1
                    && list.GetSelectedItems().SequenceEqual(expectedHighlights);
                // Golden final states from the unmodified grid with these synthetic IDs.
                var expected = name switch
                {
                    "page1" or "reparented" => (240, 240, 1, "500 found, page 2/3"),
                    "numeric" => (240, 240, 1, "500 found, page 2/3"),
                    "last_page" or "missing_numeric" => (480, 20, 1, "500 found, page 3/3"),
                    "name_page1" or "name_selected482" => (480, 10, 2, "250 found, page 2/2"),
                    "no_results" => (0, 0, 1, "0 found, page 1/1"),
                    "shrinking" => (0, 100, 1, "100 found, page 1/1"),
                    "removed_selection" => (0, 200, 1, "200 found, page 1/1"),
                    _ => (0, 240, 1, "500 found, page 1/3"),
                };
                string[] expectedInspections = name switch
                {
                    "numeric" => new[] { "260", "260" },
                    "name_page1" => new[] { "0", "0" },
                    "name_selected482" => new[] { "0", "482" },
                    "removed_selection" => new[] { "260" },
                    _ => Array.Empty<string>(),
                };
                ok &= ids.SequenceEqual(Enumerable.Range(0, expected.Item2).Select(i => expected.Item1 + i * expected.Item3))
                    && status == expected.Item4 && inspected.SequenceEqual(expectedInspections)
                    && selectedDuringInspect.SequenceEqual(expectedInspections.Select(id => (int?)int.Parse(id)))
                    && panel.IconCalls == ids.Length;
                cases[name] = new Dictionary<string, object>
                {
                    ["ok"] = ok, ["icon_calls"] = panel.IconCalls, ["id_scans"] = panel.IdScans,
                    ["descriptions"] = panel.Descriptions, ["inspect_ids"] = inspected.ToArray(),
                    ["visible_ids"] = ids, ["selected"] = panel.Selected, ["status"] = status,
                    ["highlights"] = list.GetSelectedItems(), ["ms"] = ms, ["managed_bytes"] = bytes,
                    ["selected_during_inspect"] = selectedDuringInspect.ToArray(),
                };
                if (!ok) _failures.Add("Grid refresh " + name + ": selection, scan or highlight contract changed");
            }
            catch (Exception ex)
            {
                _failures.Add("Grid refresh " + name + ": " + ex.Message);
            }
            finally { panel.Shutdown(); panel.Free(); }
        }
    }
}
#endif
