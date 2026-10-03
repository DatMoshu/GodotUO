#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Multi Editor smoke stage, phase 2: the generator panel's preview and Apply, the placement tools, rotate and
/// mirror, copy and paste, stamps, the legacy formats, the unsaved prompt. It runs after the phase 1 checks of
/// <see cref="EditorSmoke.RunMultiEditAsync"/> and shares its report.
/// </summary>
public partial class EditorSmoke
{
    private async Task<bool> WaitFor(Func<bool> done, double seconds)
    {
        double t = 0;
        while (!done() && t < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            t += GetProcessDeltaTime();
        }

        return done();
    }

    private string Describe(ValidationResult r) =>
        string.Join("; ", r.Findings.Where(f => f.Severity == FindingSeverity.Error).Take(4).Select(f => $"{f.Kind} {f.Message}"));

    private async Task RunMultiEditPhase2Async(MultiEditView view, string root, ushort floorId)
    {
        MultiCanvas canvas = view.Canvas;
        MultiDocument doc = view.Doc;
        GeneratePanel panel = view.GeneratePanel;

        // --- the Generate panel is mounted and keeps one process --------------------------------------------------
        MeCheck("generate_tab_mounted", view.TabNames.Contains("Generate") && panel != null);
        bool styles = await WaitFor(() => panel.StylesLoaded.IsCompleted, 30);
        MeCheck("generate_styles_listed", styles && panel.StyleCount > 0, view.GenClient?.Why ?? "no styles");
        if (!styles || panel.StyleCount == 0)
        {
            return;
        }

        _meReport["generate_styles"] = panel.StyleCount;
        view.NewMulti();
        view.ShowGenerateTab();
        panel.SelectGenerator("house");
        int steps = doc.HistoryCount;
        GenResult house = await panel.NowAsync();
        MeCheck("generate_house_ok", house is { Ok: true } && house.Components.Count > 20, house?.Error);
        MeCheck("generate_preview_is_ghost", canvas.GhostCount == house.Components.Count && view.GeneratorGhostShown && doc.Parts.Count == 0 && doc.HistoryCount == steps,
            $"ghost {canvas.GhostCount} doc {doc.Parts.Count}");
        int? pid = view.GenClient.ProcessId;
        GenResult again = await panel.NowAsync();
        MeCheck("generate_one_process", pid != null && view.GenClient.ProcessId == pid && again.Ok);
        _meReport["generate_house_components"] = house.Components.Count;
        _meReport["generate_ms"] = Math.Round(house.Ms, 1);

        // Apply replaces in one undo step; undo takes it back; redo again.
        bool applied = await view.ApplyGeneratedAsync(true);
        MeCheck("generate_apply_replace_one_step", applied && doc.Parts.Count == house.Components.Count && doc.HistoryCount == steps + 1 && canvas.GhostCount == 0,
            $"{doc.Parts.Count} history {doc.HistoryCount}");
        ValidationResult v = view.ValidateNow();
        _meReport["generate_house_findings"] = string.Join(",", v.Findings.Select(f => f.Kind).Distinct().OrderBy(k => k));
        MeCheck("generate_house_validator_no_errors", !v.HasErrors, Describe(v));
        doc.Undo();
        MeCheck("generate_apply_undo", doc.Parts.Count == 0);
        doc.Redo();
        MeCheck("generate_apply_redo", doc.Parts.Count == house.Components.Count);

        // A change of a control regenerates into the preview by itself (debounced), and Apply (add) keeps what is there.
        int before = doc.Parts.Count;
        panel.SelectGenerator("house");
        panel.SelectStyle(panel.Styles[^1].Key);
        bool ghosted = await WaitFor(() => canvas.GhostCount > 0, 10);
        MeCheck("generate_control_change_regenerates", ghosted);
        bool added = await view.ApplyGeneratedAsync(false);
        MeCheck("generate_apply_add", added && doc.Parts.Count > before && doc.HistoryNames[^1].StartsWith("add house"), doc.HistoryNames[^1]);
        doc.Undo();
        MeCheck("generate_add_undo", doc.Parts.Count == before);
        view.NewMulti();
    }
}
#endif
