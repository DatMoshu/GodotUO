#if TOOLS
namespace GUO.Editor;

using Godot;
using System;

/// <summary>Drag a number's label horizontally; Shift gives fine adjustment.</summary>
internal static class NumericScrub
{
    public static Action Attach(Control label, SpinBox value, Action started = null, Action finished = null)
    {
        label.MouseFilter = Control.MouseFilterEnum.Stop;
        label.MouseDefaultCursorShape = Control.CursorShape.Hsize;
        label.TooltipText = "Drag left/right to adjust. Hold Shift for fine adjustment.";
        bool dragging = false;
        double accumulated = 0;
        void Stop()
        {
            if (!dragging) return;
            dragging = false; accumulated = 0; finished?.Invoke();
        }
        label.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                if (button.Pressed)
                { Stop(); dragging = true; accumulated = 0; started?.Invoke(); }
                else Stop();
                label.AcceptEvent();
            }
            else if (e is InputEventMouseMotion motion && dragging)
            {
                if ((motion.ButtonMask & MouseButtonMask.Left) == 0) { Stop(); return; }
                accumulated += motion.Relative.X / (motion.ShiftPressed ? 12.0 : 3.0);
                int steps = (int)accumulated;
                if (steps != 0) { value.Value += steps * value.Step; accumulated -= steps; }
                label.AcceptEvent();
            }
        };
        return Stop;
    }
}
#endif
