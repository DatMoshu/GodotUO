// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

internal partial class WaterPanel
{
    private VBoxContainer _replayControls;
    private VBoxContainer _replaySettings;
    private Button _replayOpen;
    private OptionButton _replayTest;
    private SpinBox _replayTime;
    private Button _replayRun, _replayStep;
    internal OptionButton ReplayTest => _replayTest;
    internal SpinBox ReplayTime => _replayTime;

    private void BuildReplayControls(Node body)
    {
        _replayControls = new VBoxContainer(); body.AddChild(_replayControls);
        _replayOpen = new Button { Text = "Repeatable synthetic test", ToggleMode = true,
            TooltipText = "Open fixed splash and hull inputs for a reproducible frozen preview. Available with Preview in World off." };
        _replayControls.AddChild(_replayOpen);
        _replaySettings = new VBoxContainer { Visible = false }; _replayControls.AddChild(_replaySettings);
        _replayOpen.Toggled += on =>
        {
            _replaySettings.Visible = on;
            if (on) Callable.From(() => { if (_replayControls.Visible && _replayOpen.ButtonPressed) ScrollVertical = (int)_replayControls.Position.Y; }).CallDeferred();
        };
        var row = new HBoxContainer(); _replaySettings.AddChild(row);
        _replayTest = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (string test in new[] { "Splash", "Hull pass", "Splash + hull" }) _replayTest.AddItem(test);
        row.AddChild(_replayTest);
        _replayTime = new SpinBox { MinValue = 0, MaxValue = 5, Step = .05, Value = 1.5,
            Suffix = "s", CustomMinimumSize = new Vector2(110, 0), TooltipText = "Time since the synthetic test began, from 0 to 5 seconds in 0.05-second steps." };
        row.AddChild(_replayTime); _replayTime.ValueChanged += _ => UpdateReplayControls();
        var actions = new HFlowContainer(); _replaySettings.AddChild(actions);
        _replayRun = AddButton(actions, "Replay at time", ReplayPreview);
        _replayStep = AddButton(actions, "Step 0.05 s", () =>
        { if (_apply?.ButtonPressed != true) { _replayTime.Value = Math.Min(5, _replayTime.Value + .05); ReplayPreview(); } });
        _replayRun.TooltipText = _replayStep.TooltipText = "Reset the synthetic clock and both ripple fields, apply the selected inputs at fixed steps, then Freeze. Keeps preset settings, scene, light and reference.";
        _replaySettings.AddChild(new Label { Text = "Same inputs from t=0; pauses at the chosen time.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        UpdateReplayControls();
    }

    internal void ReplayPreview()
    {
        if (_apply.ButtonPressed)
        { _status.Text = "Turn off Preview in World to replay a synthetic test. Native actors and World waves are separate."; return; }
        _freeze.ButtonPressed = true;
        _preview.Replay(_replayTest.Selected, (float)_replayTime.Value);
        _replayTime.SetValueNoSignal(_preview.Seconds);
        UpdateReplayControls();
    }

    private void UpdateReplayControls()
    {
        if (_replayRun == null) return;
        bool world = _apply?.ButtonPressed == true;
        _replayControls.Visible = !world;
        _replayRun.Disabled = world;
        _replayStep.Disabled = world || _replayTime.Value >= 5;
    }

    internal bool ReplayExpanded => _replayOpen.ButtonPressed;
    internal void ShowReplayControls(bool visible = true) => _replayOpen.ButtonPressed = visible;
}
#endif
