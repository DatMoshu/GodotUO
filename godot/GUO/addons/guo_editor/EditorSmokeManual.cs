#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _manual = new();
    private bool _manualStarted, _manualBuilt;

    // A bounded acceptance mode: existing rail controls and viewport input only.
    // Edits persist through WorldEditor's normal atomic auto-save path.
    private void StepManualTwoBlock()
    {
        if (_stage == 10) return;
        if (_elapsed > 180) { _failures.Add("Manual two-block workflow timed out"); Finish(); return; }
        try
        {
            if (!_data.IsLoaded) return;
            if (!_manualStarted) { _manualStarted = true; StartWorld(); _frames = 0; return; }
            if (!_world.IsBooted || _frames < 30) return;
            if (!_manualBuilt) { BuildManualTwoBlock(); _manualBuilt = true; _frames = 0; return; }
            if (_step >= _steps.Count)
            {
                _world.ForcedMouse = null;
                _report["manual_two_block"] = _manual;
                Finish();
                return;
            }
            _steps[_step].Run();
            _step++;
            _frames = 0;
        }
        catch (Exception ex)
        {
            _failures.Add($"Manual step {_step}: {ex.Message}");
            _report["manual_two_block"] = _manual;
            Finish();
        }
    }

    private void ManualCheck(string name, bool ok)
    {
        _manual[name] = ok;
        if (!ok) throw new InvalidOperationException(name);
    }

    private void ManualKey(Key key, bool control = false) => _world.TourInput(
        new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = control });

    private void ManualClick() => _world.TourInput(new InputEventMouseButton
    { ButtonIndex = MouseButton.Left, Pressed = true, Position = _world.CanvasSize / 2 });

    private void BuildManualTwoBlock()
    {
        string root = Path.Combine(_out, "project");
        _manual["project"] = root;
        _manual["path"] = "World rail + TourInput viewport clicks; Ctrl+Z/Y; atomic auto-save; reopen";
        _steps.Add((30, () =>
        {
            ManualCheck("fresh_project", !Directory.Exists(root));
            _world.OpenProject(root);
            _world.TourCheck("Lock terrain").ButtonPressed = false;
            _world.TourCheck("Fixed placement plane").ButtonPressed = false;
            _data.CurrentArt = EditorData.LandCount + 0x0CE3;
        }));
        foreach (int targetX in new[] { 1166, 1170 })
        {
            int bx = targetX >> 3, by = 208;
            string name = $"{bx}_{by}";
            WorldBlock original = null;
            string stamped = null, raised = null;
            int x = 0, y = 0;
            _steps.Add((30, () =>
            {
                _world.GoTo(0, targetX, 1668);
                _world.ForcedMouse = _world.CanvasSize / 2;
                _world.ToolButton(WorldTool.Stamp).EmitSignal(Button.SignalName.Pressed);
            }));
            _steps.Add((30, () =>
            {
                var picked = _world.Host.Picked as GUO.Game.GameObjects.GameObject;
                ManualCheck(name + "_picked_block", picked != null && (picked.X >> 3) == bx && (picked.Y >> 3) == by);
                x = picked.X; y = picked.Y;
                _manual[name + "_picked_cell"] = new[] { x, y };
                original = WorldProject.Capture(Client.Game.UO.FileManager.Maps, 0, bx, by);
                ManualClick();
                var project = _world.Host.Project;
                stamped = project.BlockText(0, bx, by);
                WorldBlock b = WorldProject.ReadBlock(project.BlockPath(0, bx, by));
                ManualCheck(name + "_stamp_saved", stamped != null && b.Statics.Count == original.Statics.Count + 1);
            }));
            _steps.Add((30, () =>
            {
                _world.ToolButton(WorldTool.Raise).EmitSignal(Button.SignalName.Pressed);
                ManualClick();
                var project = _world.Host.Project;
                raised = project.BlockText(0, bx, by);
                WorldBlock b = WorldProject.ReadBlock(project.BlockPath(0, bx, by));
                ManualCheck(name + "_raise_saved", b.LandZ.Zip(original.LandZ, (a, z) => (int)a - z).Sum() == 1);
                ManualCheck(name + "_both_saved", raised != stamped && b.Statics.Count == original.Statics.Count + 1);
            }));
            _steps.Add((30, () =>
            {
                ManualKey(Key.Z, true);
                ManualCheck(name + "_undo_raise", _world.Host.Project.BlockText(0, bx, by) == stamped);
                ManualKey(Key.Z, true);
                ManualCheck(name + "_undo_stamp_to_install", _world.Host.Project.BlockText(0, bx, by) == null);
            }));
            _steps.Add((30, () =>
            {
                ManualKey(Key.Y, true);
                ManualKey(Key.Y, true);
                ManualCheck(name + "_redo_both", _world.Host.Project.BlockText(0, bx, by) == raised);
            }));
            _steps.Add((30, () =>
            {
                using Image frame = _world.Capture();
                ManualCheck(name + "_frame", frame != null && !frame.IsEmpty());
                string png = Path.Combine(_out, name + ".png");
                frame.SavePng(png);
                _manual[name + "_png"] = png;
            }));
        }
        _steps.Add((30, () =>
        {
            var saved = _world.Host.Project.Blocks(0).ToDictionary(b => (b.Bx, b.By), b => _world.Host.Project.BlockText(0, b.Bx, b.By));
            ManualCheck("exactly_two_blocks", saved.Count == 2);
            _world.OpenProject(root);
            ManualCheck("reopened_same_saved_blocks", saved.All(p => _world.Host.Project.BlockText(0, p.Key.Bx, p.Key.By) == p.Value));
            ManualCheck("reopen_clears_undo", _world.Editor.UndoCount == 0);
        }));
    }
}
#endif
