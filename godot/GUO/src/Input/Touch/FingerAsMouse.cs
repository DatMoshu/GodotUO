// GUO addition, not a port: upstream ClassicUO has no touch screen.

using Godot;

namespace GUO.Input.Touch;

/// <summary>
/// A finger as the left mouse button, for a Godot card whose controls take
/// the mouse only (the screen effects menu, the change-folder screen).
/// </summary>
/// <remarks>
/// The touch layer turns off Godot's own touch-to-mouse copies
/// (<see cref="TouchInput.Enable"/>), so the client sees each finger once.
/// A card of plain Godot controls then gets nothing from a finger: a tap on
/// it reached no button and fell through to the game (the Close that did
/// not close on the Thor's top screen, the owner, 2026-09-28). A finger
/// that comes down inside the card's rect is the card's until it lifts; its
/// events are pushed into the viewport as the mouse events Godot would have
/// made, on the emulation device, which <see cref="InputMode"/> counts as
/// the touch it is. While one is being pushed, <see cref="Pushing"/> is
/// true, and the card's input claim says yes to it, so GameController
/// lets it through to the controls.
/// </remarks>
internal sealed class FingerAsMouse
{
    private int _finger = -1;

    /// <summary>A copy is being pushed now: whoever claims input should claim it.</summary>
    public bool Pushing { get; private set; }

    /// <summary>Forget the finger (the card closed).</summary>
    public void Reset() => _finger = -1;

    /// <summary>
    /// A touch or a drag: pushed into <paramref name="viewport"/> as the mouse
    /// when it belongs to the card, which has <paramref name="rect"/> in window
    /// pixels. True when it did (the event is the card's).
    /// </summary>
    public bool Take(InputEvent e, Rect2 rect, Viewport viewport)
    {
        InputEventMouse mouse;

        switch (e)
        {
            case InputEventScreenTouch { Pressed: true } t when _finger < 0 && rect.HasPoint(t.Position):
                _finger = t.Index;
                Push(viewport, new InputEventMouseMotion { Position = t.Position });
                mouse = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, Position = t.Position };

                break;

            case InputEventScreenTouch { Pressed: false } t when t.Index == _finger:
                _finger = -1;
                mouse = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = t.Position };

                break;

            case InputEventScreenDrag d when d.Index == _finger:
                mouse = new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = d.Relative, Position = d.Position };

                break;

            default:
                return false;
        }

        Push(viewport, mouse);
        return true;
    }

    private void Push(Viewport viewport, InputEventMouse mouse)
    {
        mouse.Device = (int) InputEvent.DeviceIdEmulation;
        mouse.GlobalPosition = mouse.Position;
        Pushing = true;

        try
        {
            viewport.PushInput(mouse);
        }
        finally
        {
            Pushing = false;
        }
    }
}
