#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>Drag a number's label horizontally; Shift gives fine adjustment.</summary>
internal static class NumericScrub
{
    public static void Attach(Control label, SpinBox value)
    {
        label.MouseFilter = Control.MouseFilterEnum.Stop;
        label.MouseDefaultCursorShape = Control.CursorShape.Hsize;
        label.TooltipText = "Drag left/right to adjust. Hold Shift for fine adjustment.";
        bool dragging = false;
        double accumulated = 0;
        label.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                dragging = button.Pressed; accumulated = 0; label.AcceptEvent();
            }
            else if (e is InputEventMouseMotion motion && dragging)
            {
                if ((motion.ButtonMask & MouseButtonMask.Left) == 0) { dragging = false; return; }
                accumulated += motion.Relative.X / (motion.ShiftPressed ? 12.0 : 3.0);
                int steps = (int)accumulated;
                if (steps != 0) { value.Value += steps * value.Step; accumulated -= steps; }
                label.AcceptEvent();
            }
        };
    }
}
#endif
