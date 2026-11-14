#if TOOLS
namespace GUO.Editor;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private async Task CheckLayeredTavern(MultiEditView view, string root)
    {
        string fixture = System.Environment.GetEnvironmentVariable("GUO_LAYERED_MULTI_FIXTURE");
        if (string.IsNullOrEmpty(fixture)) return;
        // Exercise renderer teardown too: an existing World tab survives alongside Multis.
        if (DisplayServer.GetName() != "headless")
        {
            StartWorld();
            await Frames(15);
            MeCheck("layered_world_booted", _world.IsBooted);
        }
        EditorInterface.Singleton.SetMainScreenEditor(MultiEditView.TabName);
        view.Visible = true;
        MeCheck("layered_tavern_open", view.OpenDescription(fixture));
        var doc = view.Doc;
        MeCheck("layered_tavern_layers", doc.Layers.Count >= 6 && doc.Parts.Any(p => p.Z == 27));
        string before = MultiLayerCodec.Encode(doc, MultiStore.ToJson(doc.Name, doc.Source, doc.Parts));
        int roof = doc.Layers.Single(l => l.Name == "Roof").Id;
        var roofPart = doc.Parts.First(p => p.LayerId == roof && p.Shown);
        doc.EditLayer(doc.Revision, "visible", roof, value: false);
        MeCheck("layered_hide_canvas", !view.Canvas.Display(roofPart).Visible);
        MeCheck("layered_hide_keeps_native", doc.Parts.First(p => p.Uid == roofPart.Uid).Shown);
        doc.Undo();
        doc.EditLayer(doc.Revision, "lock", roof, value: true);
        MeCheck("layered_manual_lock", !doc.Move(new[] { roofPart.Uid }, 1, 0, 0));
        doc.Undo();

        // Actual manual Layers panel button, using its normal signal path.
        var layersTab = view.FindChildren("*", "VBoxContainer", true, false).First(n => n.Name == "Layers");
        var button = layersTab.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "Show / hide");
        bool initial = doc.Layers[0].Visible;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        MeCheck("layered_manual_panel", doc.Layers[0].Visible != initial);
        doc.Undo();

        var host = Ai?.Hub?.Tools;
        MeCheck("layered_ai_host_present", host != null);
        if (host != null)
        {
            var prior = host.Approve;
            try
            {
                host.Approve = _ => Task.FromResult(true); // Isolated smoke fixture only.
                long revision = doc.Revision;
                var args = new JsonObject { ["expected_revision"] = revision, ["action"] = "move", ["layer_id"] = roof, ["dx"] = 1 };
                string preview = await host.RunAsync("multi_layer_preview", args, CancellationToken.None);
                MeCheck("layered_ai_preview", !preview.StartsWith("error:") && doc.Revision == revision, preview);
                string edit = await host.RunAsync("multi_layer_edit", args, CancellationToken.None);
                MeCheck("layered_ai_edit", !edit.StartsWith("error:") && doc.Parts.First(p => p.Uid == roofPart.Uid).X == roofPart.X + 1, edit);
                string stale = await host.RunAsync("multi_layer_edit", args, CancellationToken.None);
                MeCheck("layered_ai_stale", stale.StartsWith("error:"), stale);
                doc.Undo();
                var assign = new JsonObject { ["expected_revision"] = doc.Revision, ["action"] = "assign", ["layer_id"] = roof };
                string missingSelection = await host.RunAsync("multi_layer_preview", assign, CancellationToken.None);
                MeCheck("layered_ai_selection_guard", missingSelection.StartsWith("error:"), missingSelection);
            }
            finally { host.Approve = prior; }
        }
        string after = MultiLayerCodec.Encode(doc, MultiStore.ToJson(doc.Name, doc.Source, doc.Parts));
        MeCheck("layered_all_edits_undo_exact", before == after);
        await view.TransformAsync("rotate", 1);
        MeCheck("layered_rotate_retains_membership", doc.Parts.Count == System.Text.Json.Nodes.JsonNode.Parse(before)["components"].AsArray().Count
            && doc.Layers.All(l => doc.Parts.Count(p => p.LayerId == l.Id) == System.Text.Json.Nodes.JsonNode.Parse(before)["editor_layers"]["membership"].AsArray().Count(n => (int)n == l.Id)));
        doc.Undo();
        MeCheck("layered_rotate_undo_exact", before == MultiLayerCodec.Encode(doc, MultiStore.ToJson(doc.Name, doc.Source, doc.Parts)));
        string saved = view.SaveDescription();
        MeCheck("layered_saved_not_dirty", !view.Modified);
        view.OpenDescription(saved);
        MeCheck("layered_reopen_exact", before == MultiLayerCodec.Encode(doc, MultiStore.ToJson(doc.Name, doc.Source, doc.Parts)));
        File.Copy(saved, Path.Combine(root, "layered-tavern.multi.json"), true);
        view.ValidateNow();
        MeCheck("layered_no_validation_errors", !view.Result.HasErrors);
        _meReport["layered_findings"] = view.Result.Findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
        view.Canvas.CutAbove = false;
        view.Canvas.ShowProblems = false;
        view.Canvas.ShowWalkable = false;
        view.Canvas.ShowHidden = false;
        view.Canvas.ShowVirtualFloor = false;
        Array.Fill(view.Canvas.Vision, StoryVision.Normal);
        view.Canvas.FitView();
        await Frames(15);
        if (DisplayServer.GetName() != "headless")
        {
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(root, "layered-tavern-editor.png"));
            doc.EditLayer(doc.Revision, "visible", roof, value: false);
            await Frames(15);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(root, "layered-tavern-roof-hidden.png"));
            doc.Undo();
        }
    }
}
#endif
