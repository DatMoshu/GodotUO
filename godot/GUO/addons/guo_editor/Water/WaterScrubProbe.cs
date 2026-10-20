// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

public partial class WaterWorkbenchProbe
{
    internal static Control ScrubLabel(WaterPanel panel, string key) => panel.NumberInput(key).GetParent().GetChild<Control>(0);
    internal static void ScrubButton(Control label, bool down)
    {
        using var input = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down };
        label.EmitSignal(Control.SignalName.GuiInput, input);
    }
    internal static void ScrubMove(Control label, float x, MouseButtonMask mask = MouseButtonMask.Left)
    {
        using var input = new InputEventMouseMotion { Relative = new Vector2(x, 0), ButtonMask = mask };
        label.EmitSignal(Control.SignalName.GuiInput, input);
    }
    private void CheckWaterScrub(WaterPanel owner, string data)
    {
        var panel = new WaterPanel { Visible = false, DraftFolderOverride = Path.Combine(data, "scrub-library") };
        owner.GetParent().AddChild(panel);
        try
        {
            var preset = WaterAuthoring.Presets[1];
            string saved = Path.Combine(data, "scrub-draft.json"); WaterAuthoring.Save(preset, saved);
            panel.LoadStyle(preset, saved);
            var label = ScrubLabel(panel, "Wave strength");
            int history = panel.HistoryPosition;
            ScrubButton(label, true);
            for (int i = 0; i < 201; i++) ScrubMove(label, i % 2 == 0 ? 3 : -3);
            Require(panel.HistoryPosition == history && Math.Abs(panel.Style.Wave - (preset.Wave + .01f)) < .001f && panel.Dirty,
                "201 scrub motions preview live without consuming history entries");
            ScrubButton(label, false);
            Require(panel.HistoryPosition == history + 1 && panel.DraftPath == saved, "release records one adjustment on the same document");
            panel.Undo(); Require(panel.Style.Wave == preset.Wave && !panel.Dirty, "one Undo restores the entire label drag");
            ScrubButton(label, true); ScrubMove(label, 3); ScrubMove(label, -3); ScrubButton(label, false);
            Require(panel.HistoryPosition == history && !panel.Dirty, "out-and-back drag adds no history entry");
            panel.Redo(); Require(Math.Abs(panel.Style.Wave - (preset.Wave + .01f)) < .001f, "no-op scrub preserves the existing redo branch");
            ScrubButton(label, true); ScrubMove(label, 3); ScrubButton(label, false);
            Require(panel.HistoryPosition == history + 2, "separate label drags remain separate Undo steps");
            panel.NumberInput("Wave strength").Value = .45;
            panel.Undo(); Require(Math.Abs(panel.Style.Wave - (preset.Wave + .02f)) < .001f, "a typed numeric adjustment remains separate from a label drag");
            ScrubButton(label, true); ScrubMove(label, 3); panel.Undo();
            float undone = panel.Style.Wave; ScrubMove(label, 3);
            Require(panel.Style.Wave == undone, "Undo during a drag closes its input gesture and ignores subsequent motion without a new press");
            panel.Redo(); Require(Math.Abs(panel.Style.Wave - (preset.Wave + .03f)) < .001f, "Redo restores the complete interrupted drag");
            ScrubButton(label, true); ScrubMove(label, 3); Press(panel, "Save draft");
            Require(!panel.Dirty && Math.Abs(WaterAuthoring.Read(saved).Wave - (preset.Wave + .04f)) < .001f && panel.DraftPath == saved,
                "saving during a scrub commits its final value to the correct draft baseline");
            panel.Undo(); Require(panel.Dirty && Math.Abs(panel.Style.Wave - (preset.Wave + .03f)) < .001f && panel.DraftPath == saved,
                "Undo after saving a scrub retains its save identity and detects unsaved changes");
            ScrubButton(label, true); ScrubMove(label, 3); panel.LoadStyle(WaterAuthoring.Presets[6]);
            panel.Undo(); Require(panel.DraftPath == saved && Math.Abs(panel.Style.Wave - (preset.Wave + .04f)) < .001f,
                "undoing a preset load restores the final scrub value and original draft destination");
            panel.Undo(); Require(panel.DraftPath == saved && Math.Abs(panel.Style.Wave - (preset.Wave + .03f)) < .001f,
                "preset load and preceding scrub remain distinct history actions");
            int beforeLostRelease = panel.HistoryPosition;
            ScrubButton(label, true); ScrubMove(label, 3); ScrubMove(label, 0, 0);
            Require(panel.HistoryPosition == beforeLostRelease + 1, "motion without the left button finishes a scrub when release was missed");
            panel.Undo(); ScrubButton(label, true); ScrubMove(label, 3); panel.Visible = true; panel._Process(0); panel.Visible = false;
            Require(panel.HistoryPosition == beforeLostRelease + 1, "process fallback closes a scrub after mouse release outside its label");
            panel.LoadStyle(WaterAuthoring.Presets[0]);
            var disabled = ScrubLabel(panel, "Glints"); history = panel.HistoryPosition;
            ScrubButton(disabled, true); ScrubMove(disabled, 30); ScrubButton(disabled, false);
            Require(panel.HistoryPosition == history && panel.Style.Glint == WaterAuthoring.Presets[0].Glint,
                "disabled model controls cannot begin a scrub or change stored values");
            var ordinary = new SpinBox { Step = 1 }; panel.AddChild(ordinary);
            var ordinaryLabel = new Label(); panel.AddChild(ordinaryLabel);
            Action stop = NumericScrub.Attach(ordinaryLabel, ordinary);
            ScrubButton(ordinaryLabel, true); ScrubMove(ordinaryLabel, 12); ScrubButton(ordinaryLabel, false);
            Require(ordinary.Value == 4, "shared numeric scrub without callbacks retains its ordinary step behavior");
            ScrubButton(ordinaryLabel, true); stop(); ScrubMove(ordinaryLabel, 12);
            Require(ordinary.Value == 4, "explicit scrub cancellation prevents later motion from changing its value");
            history = panel.HistoryPosition;
            ScrubButton(label, true); ScrubMove(label, 3);
            float finalWave = panel.Style.Wave;
            panel.Preview.Shutdown(); panel.Shutdown();
            Require(panel.HistoryPosition == history + 1 && panel.Style.Wave == finalWave,
                "shutdown commits a pending drag without refreshing an already disposed child preview");
        }
        finally { panel.Shutdown(); panel.Free(); }
    }
}
#endif
