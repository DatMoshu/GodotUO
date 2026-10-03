// GUO addition: posture transitions and touch controls reuse the controller input path.
using System;
using System.Collections.Generic;
using Godot;
using GUO.Input.Touch;
using GUO.Input.Gamepad;
using GUO.Game.Managers;

namespace GUO.Platform.Android;

internal sealed partial class AdaptiveLayout : CanvasLayer
{
    private const int VirtualPad = GamepadInput.TouchDevice;
    private static AdaptiveLayout _instance;
    private readonly FoldObserver _observer = new();
    private FoldObserver.Feature _feature;
    private DeviceLayout _pending;
    private ulong _since;
    private Surface _surface;
    private DevicePosture _mode;
    private bool _wasActive;
    private readonly Dictionary<int, string> _fingers = new();
    private bool _macros;
    private static readonly string[] Modes = Enum.GetNames<DevicePosture>();
    public static DeviceLayout Layout { get; private set; }
    public static bool Active => _instance != null && TouchInput.Enabled && DualScreen.IsPanel
        && !DualScreen.ForcedOff && DualScreenSettings.Current.PanelOn
        && (Client.Game?.UO?.World?.InGame ?? false);
    public static bool MacrosOpen => Active && _instance._macros;
    public static int Revision { get; private set; }
    public static int CompanionReserve => Active && !Layout.Companion.Empty
        ? Math.Min(Layout.Companion.Height / 4, Math.Max(24, (int)(48 * Math.Max(1, UoTheme.PixelScale) / Math.Max(1f, Client.Game.DpiScale)))) : 0;
    public static bool IsSeparating => Active && !Layout.Hinge.Empty;

    public static void Setup(Node host)
    {
        if (_instance != null) return;
        if (!OperatingSystem.IsAndroid() && Array.IndexOf(OS.GetCmdlineUserArgs(), "--adaptive-layout") < 0) return;
        _instance = new AdaptiveLayout { Layer = 85, Name = "AdaptiveDeviceLayout" };
        host.AddChild(_instance);
    }

    public override void _Ready()
    {
        _surface = new Surface { MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_surface);
        var config = new ConfigFile();
        if (config.Load("user://device-layout.cfg") == Error.Ok)
            Enum.TryParse(config.GetValue("layout", "mode", "Auto").AsString(), out _mode);
        _observer.Start();
        // On Android rotations and fold changes must be allowed to reach the viewport.
        if (OperatingSystem.IsAndroid()) DisplayServer.ScreenSetOrientation(DisplayServer.ScreenOrientation.Sensor);
        GamepadInput.KnownLayout(VirtualPad, GamepadLayout.Labels);
    }

    public override void _ExitTree()
    {
        ReleaseControls();
        _observer.Dispose();
        if (_instance == this) _instance = null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused) ReleaseControls();
    }

    // Called by DualScreen before it resolves the companion geometry.
    public static void UpdateLayout()
    {
        var self = _instance;
        if (self == null) return;
        if (self._observer.Poll(out var feature)) self._feature = feature;
        bool active = Active;
        self.Visible = active && !ScreenSaver.Active;
        if (ScreenSaver.Active || UIManager.IsModalOpen || GUO.Input.Touch.Modern.ModernGump.IsOpen)
            self.ReleaseControls();
        if (!active)
        {
            if (self._wasActive) { self.ReleaseControls(); Revision++; }
            self._wasActive = false;
            return;
        }
        float scale = Math.Max(0.1f, Client.Game.DpiScale);
        var bounds = Client.Game.ClientBounds;
        var f = self._feature.Bounds;
        var hinge = new LayoutRect((int)(f.X / scale), (int)(f.Y / scale), (int)(f.Width / scale), (int)(f.Height / scale));
        float density = Math.Max(1f, DisplayServer.ScreenGetDpi() / 160f);
        bool physicalPad = GamepadInput.Enabled && Godot.Input.GetConnectedJoypads().Count > 0;
        var wanted = DeviceLayout.Resolve(bounds.Width, bounds.Height, scale / density,
            self._mode, hinge, self._feature.Separating || self._feature.HalfOpen, !physicalPad);
        ulong now = Godot.Time.GetTicksMsec();
        bool resized = Layout.World.Right > bounds.Width || Layout.Companion.Right > bounds.Width
            || Layout.Companion.Bottom > bounds.Height;
        if (wanted != self._pending)
        {
            self._pending = wanted;
            self._since = now;
            self.ReleaseControls();
            GamepadInput.ReleaseAll();
            TouchInput.CancelGesture();
        }
        // Hysteresis for hinge chatter; shrinking windows must never keep invalid hit regions.
        if ((!self._wasActive || resized || now - self._since >= 180) && Layout != wanted)
        {
            Layout = wanted;
            Revision++;
            self._macros = false;
            GD.Print($"[GUO] adaptive layout: {wanted.Posture}, world {wanted.World}, companion {wanted.Companion}");
        }
        self._wasActive = true;
        self._surface.QueueRedraw();
    }

    public static void ToggleMacros() { if (Active) _instance._macros = !_instance._macros; }
    private static Rect2 Physical(LayoutRect r)
    {
        float s = Client.Game?.DpiScale ?? 1;
        return new Rect2(r.X * s, r.Y * s, r.Width * s, r.Height * s);
    }

    private Rect2 ModeButton()
    {
        Rect2 world = Physical(Layout.World);
        float h = Math.Max(36, 20 * UoTheme.PixelScale);
        return new Rect2(Math.Max(world.Position.X, world.End.X - h * 3), world.Position.Y + h, Math.Min(world.Size.X, h * 3), h);
    }

    private void NextMode()
    {
        ReleaseControls();
        _mode = (DevicePosture)(((int)_mode + 1) % Modes.Length);
        var config = new ConfigFile();
        config.SetValue("layout", "mode", _mode.ToString());
        config.Save("user://device-layout.cfg");
        _wasActive = false;
    }

    private static Rect2 CompanionTab(int index)
    {
        var r = Layout.Companion;
        return Physical(new LayoutRect(r.X + index * r.Width / 3, r.Bottom - CompanionReserve,
            (index + 1) * r.Width / 3 - index * r.Width / 3, CompanionReserve));
    }

    public static bool HandleInput(InputEvent e)
    {
        if (!Active || ScreenSaver.Active) return false;
        return _instance.Take(e);
    }

    private bool Take(InputEvent e)
    {
        int id;
        Vector2 at;
        bool down = false, up = false;
        switch (e)
        {
            case InputEventScreenTouch t: id = t.Index; at = t.Position; down = t.Pressed && !t.Canceled; up = !down; break;
            case InputEventScreenDrag d: id = d.Index; at = d.Position; break;
            case InputEventMouseButton m when m.ButtonIndex == MouseButton.Left:
                id = -10; at = m.Position; down = m.Pressed; up = !down; break;
            case InputEventMouseMotion m: id = -10; at = m.Position; break;
            default: return false;
        }
        if (up && _fingers.Remove(id, out string released)) { Release(released); return true; }
        if (_fingers.TryGetValue(id, out string held)) { Move(held, at); return true; }
        if (down && ModeButton().HasPoint(at)) { NextMode(); _fingers[id] = "mode"; return true; }
        if (IsSeparating && Physical(Layout.Hinge).HasPoint(at)) return true;
        // A modal owns the session; held controls have already been released above.
        if (UIManager.IsModalOpen || GUO.Input.Touch.Modern.ModernGump.IsOpen) { ReleaseControls(); return false; }
        if (down && CompanionReserve > 0)
        {
            for (int i = 0; i < 3; i++)
                if (CompanionTab(i).HasPoint(at))
                {
                    TouchInput.Bar?.Invoke(new[] { "paperdoll", "backpack", "journal" }[i]);
                    _fingers[id] = "tab";
                    return true;
                }
        }
        Rect2 left = Physical(Layout.Movement), right = Physical(Layout.Actions);
        if (!down) return false;
        string action;
        if (!Layout.Movement.Empty && left.HasPoint(at)) action = "move";
        else if (!Layout.Actions.Empty && right.HasPoint(at))
        {
            Vector2 local = (at - right.Position) / right.Size;
            action = local.Y >= 0.55f ? "pointer" : local.Y < 0.275f ? (local.X < 0.5f ? "X" : "Y") : (local.X < 0.5f ? "A" : "B");
        }
        else return false;
        if (WindowMenu.IsOpen && action == "move") return true;
        if (_fingers.ContainsValue(action)) return true;
        _fingers[id] = action;
        if (action == "Y") ToggleMacros();
        else if (Enum.TryParse<JoyButton>(action, out var button)) Button(button, true);
        Move(action, at);
        return true;
    }

    private static void Button(JoyButton button, bool down) =>
        GamepadInput.Handle(new InputEventJoypadButton { Device = VirtualPad, ButtonIndex = button, Pressed = down });
    private static void Axis(JoyAxis axis, float value) =>
        GamepadInput.Handle(new InputEventJoypadMotion { Device = VirtualPad, Axis = axis, AxisValue = value });
    private static void Move(string action, Vector2 at)
    {
        if (action != "move" && action != "pointer") return;
        Rect2 r = Physical(action == "move" ? Layout.Movement : Layout.Actions);
        if (action == "pointer") { r.Position += new Vector2(0, r.Size.Y * 0.55f); r.Size *= new Vector2(1, 0.45f); }
        Vector2 v = (at - r.GetCenter()) / (r.Size * 0.42f);
        Axis(action == "move" ? JoyAxis.LeftX : JoyAxis.RightX, Mathf.Clamp(v.X, -1, 1));
        Axis(action == "move" ? JoyAxis.LeftY : JoyAxis.RightY, Mathf.Clamp(v.Y, -1, 1));
    }
    private static void Release(string action)
    {
        if (action == "move") { Axis(JoyAxis.LeftX, 0); Axis(JoyAxis.LeftY, 0); }
        else if (action == "pointer") { Axis(JoyAxis.RightX, 0); Axis(JoyAxis.RightY, 0); }
        else if (action != "Y" && Enum.TryParse<JoyButton>(action, out var button)) Button(button, false);
    }
    private void ReleaseControls()
    {
        foreach (string action in _fingers.Values) Release(action);
        _fingers.Clear();
    }

    private sealed partial class Surface : Control
    {
        public override void _Draw()
        {
            if (!Active || !UoTheme.Ready) return;
            var self = _instance;
            if (!Layout.Hinge.Empty) DrawRect(Physical(Layout.Hinge), Colors.Black);
            if (!Layout.Movement.Empty || !Layout.Actions.Empty)
            {
                if (!Layout.Movement.Empty) Plate(Physical(Layout.Movement), "Move");
                if (!Layout.Actions.Empty)
                {
                    Rect2 r = Physical(Layout.Actions);
                    foreach (var (label, x, y) in new[] { ("X", 0f, 0f), ("Y", .5f, 0f), ("A", 0f, .275f), ("B", .5f, .275f) })
                        Plate(new Rect2(r.Position + r.Size * new Vector2(x, y), r.Size * new Vector2(.5f, .275f)), label);
                    Plate(new Rect2(r.Position + new Vector2(0, r.Size.Y * .55f), r.Size * new Vector2(1, .45f)), "Pointer");
                }
            }
            Plate(self.ModeButton(), self._mode == DevicePosture.Auto ? "Auto: " + Layout.Posture : self._mode.ToString());
            if (CompanionReserve > 0)
                for (int i = 0; i < 3; i++) Plate(CompanionTab(i), new[] { "Character", "Bag", "Journal" }[i]);
        }

        private void Plate(Rect2 rect, string label)
        {
            DrawStyleBox(UoTheme.Frame(UoTheme.StoneFrame, 0), rect);
            Font font = UoTheme.Theme.GetFont("font", "Label");
            if (font == null) return;
            int size = UoTheme.FontSize * Math.Max(1, UoTheme.PixelScale);
            while (size > UoTheme.FontSize && font.GetStringSize(label, HorizontalAlignment.Left, -1, size).X > rect.Size.X - 8)
                size -= UoTheme.FontSize;
            Vector2 measured = font.GetStringSize(label, HorizontalAlignment.Left, -1, size);
            DrawString(font, rect.GetCenter() + new Vector2(-measured.X / 2, size / 3f), label, HorizontalAlignment.Left, -1, size, UoTheme.Ink);
        }
    }
}
