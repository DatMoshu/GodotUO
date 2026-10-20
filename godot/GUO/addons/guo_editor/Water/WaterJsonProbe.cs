// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Godot;

public partial class WaterWorkbenchProbe
{
    private static string JsonPreset(GUO.Store.StoreWater style) => Encoding.UTF8.GetString(WaterAuthoring.Serialize(style));
    private void CheckWaterJson(WaterPanel owner, string data)
    {
        var panel = new WaterPanel { Visible = false, DraftFolderOverride = Path.Combine(data, "json-library") };
        owner.GetParent().AddChild(panel);
        try
        {
            string saved = Path.Combine(data, "json-original.json");
            WaterAuthoring.Save(WaterAuthoring.Presets[1], saved);
            byte[] original = File.ReadAllBytes(saved);
            panel.LoadStyle(WaterAuthoring.Presets[1], saved);
            panel.NumberInput("Wave strength").Value = .77;
            byte[] working = WaterAuthoring.Serialize(panel.Style);
            int history = panel.HistoryPosition;
            panel.OpenJson();
            Require(WaterAuthoring.Serialize(WaterAuthoring.ParseText(panel.JsonText.Text)).AsSpan().SequenceEqual(working)
                && panel.HistoryPosition == history,
                "sharing opens the current validated draft text without an extra history action");
            panel.JsonText.Text = JsonPreset(WaterAuthoring.Presets[6]);
            Require(panel.ApplyJson(true) && panel.JsonDialog == null && panel.Dirty && panel.DraftPath == saved
                && panel.HistoryPosition == history && panel.Preview.ReferenceStyle.Name == WaterAuthoring.Presets[6].Name
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working),
                "JSON reference keeps dirty working values, document path and history");
            panel.OpenJson();
            try { WaterAuthoring.ParseText(new string((char)0xD800, 1)); Require(false, "invalid Unicode must fail"); }
            catch (ArgumentException) { Require(panel.HistoryPosition == history, "text parser rejects invalid Unicode without changing the draft"); }
            foreach (string invalid in new[] { "{broken", "{}", new string('界', 6000),
                JsonPreset(WaterAuthoring.Presets[1]).Replace("\"pixel\"", "\"shader-code\"") })
            {
                panel.JsonText.Text = invalid;
                string buffer = panel.JsonText.Text;
                Require(!panel.ApplyJson(false) && !panel.ApplyJson(true) && panel.JsonDialog != null
                    && panel.JsonText.Text == buffer && panel.JsonError.StartsWith("Could not load this JSON:")
                    && panel.HistoryPosition == history && panel.Dirty && panel.DraftPath == saved
                    && panel.Preview.ReferenceStyle.Name == WaterAuthoring.Presets[6].Name
                    && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working),
                    "invalid text stays editable and preserves the working draft and reference");
                if (invalid.Length == 6000) Require(panel.JsonError.Contains("16 KB"), "UTF-8 byte limit rejects oversized multibyte text before parsing");
            }
            var imported = WaterAuthoring.Presets[2] with { Name = "Shared 川 water" };
            panel.JsonText.Text = JsonPreset(imported);
            Require(panel.ApplyJson(false) && panel.JsonDialog == null && panel.DraftPath == null && !panel.Dirty
                && panel.Style.Name == imported.Name && panel.Preview.ReferenceStyle.Name == WaterAuthoring.Presets[6].Name,
                "valid JSON recovers after errors and loads a detached new draft without replacing the reference");
            panel.Undo();
            Require(panel.HistoryPosition == history && panel.Dirty && panel.DraftPath == saved
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working),
                "Undo JSON load restores working changes and their original destination");
            panel.Redo(); panel.SaveWorkingDraft();
            Require(panel.DraftPath != saved && File.Exists(panel.DraftPath)
                && File.ReadAllBytes(saved).AsSpan().SequenceEqual(original),
                "saving imported JSON creates a separate file and preserves the original saved draft");
            panel.OpenJson(); panel.JsonText.Text = JsonPreset(WaterAuthoring.Presets[4]); panel.CloseJson();
            Require(panel.Style.Name == imported.Name && panel.JsonDialog == null,
                "closing edited JSON text does not load it");
            panel.OpenJson(); var stale = panel.JsonDialog;
            panel.OpenJson(); var current = panel.JsonDialog;
            panel.JsonText.Text = JsonPreset(WaterAuthoring.Presets[4]);
            stale.EmitSignal(AcceptDialog.SignalName.Confirmed); stale.EmitSignal(AcceptDialog.SignalName.Canceled);
            Require(ReferenceEquals(panel.JsonDialog, current) && panel.Style.Name == imported.Name,
                "callbacks from a replaced JSON dialog cannot load or close its replacement");
            panel.Shutdown();
            Require(panel.JsonDialog == null && panel.JsonText == null && File.ReadAllBytes(saved).AsSpan().SequenceEqual(original),
                "shutdown releases JSON dialog state without applying text or writing a draft");
        }
        finally { panel.Shutdown(); panel.Free(); }
    }

    private async Task CheckJsonRendering(WaterPanel panel, string stills, string renderer)
    {
        var reference = panel.Preview.ReferenceStyle;
        bool compare = panel.CompareToggle.ButtonPressed;
        try
        {
            panel.OpenJson();
            for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var image = panel.JsonDialog.GetTexture().GetImage())
                Require(image.GetWidth() >= 640 && image.GetHeight() >= 300 && image.GetHeight() <= 700
                    && image.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_json-share-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
                    "capture native JSON sharing text and explicit draft/reference actions");
            panel.JsonText.Text = "{broken"; panel.ApplyJson(false);
            for (int i = 0; i < 2; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using (var image = panel.JsonDialog.GetTexture().GetImage())
                Require(image.GetHeight() <= 700 && image.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_json-error-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
                    "capture readable JSON validation feedback without closing the text editor");
        }
        finally { panel.CloseJson(); panel.SetReference(reference); panel.CompareToggle.ButtonPressed = compare; }
    }
}
#endif
