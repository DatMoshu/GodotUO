#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

public partial class EditorSmoke
{
    private void CheckHueStage()
    {
        void Check(string name, bool ok) { _assetReport["hue_stage_" + name] = ok; if (!ok) AssetFail("Hue stage: " + name); }
        string root = Path.Combine(_out, "hue-stage-guards" + Suffix);
        byte[] source = Enumerable.Range(0, 88).Select(i => (byte)(i * 7)).ToArray(); Array.Clear(source, 68, 20);
        var doc = HueDocument.Open(Path.Combine(root, "project"), Path.Combine(root, "install"), 8, () => source);
        string token = doc.ReviewToken;
        JsonNode captured = JsonNode.Parse(doc.NativeStageInput(token));
        Check("exact_original_record_capture", (string)captured["domain"] == "hue" && (int)captured["records"][0]["id"] == 8
            && Convert.FromBase64String((string)captured["records"][0]["record_base64"]).AsSpan().SequenceEqual(source)
            && Convert.FromBase64String((string)captured["records"][0]["source_record_base64"]).AsSpan().SequenceEqual(source));
        doc.DraftColor = "0xFFFF";
        Check("pending_fields_refused", HueRefused(() => doc.NativeStageInput(token))); doc.CommitDraft();
        Check("changed_working_state_refused", HueRefused(() => doc.NativeStageInput(token)));
        token = doc.ReviewToken; captured = JsonNode.Parse(doc.NativeStageInput(token));
        byte[] changed = Convert.FromBase64String((string)captured["records"][0]["record_base64"]);
        Check("full_width_color_snapshot", changed[0] == 255 && changed[1] == 255 && changed.AsSpan(2).SequenceEqual(source.AsSpan(2)));
        source[0] ^= 1; Check("changed_source_refused", HueRefused(() => doc.NativeStageInput(token))); source[0] ^= 1;
        doc.RecoveryError = "stale recovery"; Check("stale_recovery_refused", HueRefused(() => doc.NativeStageInput(token))); doc.RecoveryError = null;
        var panel = _assets.Panel<HuePanel>(); _assets.ShowPanel(panel); panel.Search(panel.SmokeQuery);
        var active = panel.WorkingDocument(33);
        Check("inspector_action_registered", _inspector.Current.Actions.Any(a => a.Id == "export-hue-stage"));
        var dialog = panel.OpenStageReview(active);
        Check("initial_review_requires_prepare", !dialog.Prepared && !dialog.Applied && !dialog.Busy && !dialog.GetOkButton().Disabled
            && dialog.GetOkButton().Text == "Prepare verified copy" && dialog.Destination.Text.EndsWith("staged-hues"));
        dialog.Free();
    }
}
#endif
