// SPDX-License-Identifier: BSD-2-Clause

using System.Linq;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// The one-screen panel in the world (--one-screen-probe): the drawer on its
/// edge, closed at first; its tab opens it by a tap and by a drag; the shelf
/// gumps are in it; a tap inside works on them and leaves it open; a tap
/// outside closes it; the pad's Back button toggles it; it moves to the
/// other edge when the setting says so; and on a near-square window the
/// split. Each state is photographed. The settings are put back.
/// </summary>
/// <remarks>
/// Pointer events go in through Input.ParseInputEvent, so they take the
/// same path a finger or the mouse does: a touch with the touch layer on,
/// the left button without it.
/// </remarks>
internal static class OneScreenProbe
{
    public static bool Passed { get; private set; }

    private static int _failed, _checks;
    private static string _dir, _tag;

    private static void Check(string what, bool ok, string detail = "")
    {
        _checks++;
        GD.Print($"[GUO] one-screen probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    public static async System.Threading.Tasks.Task Run(Node host, string dir, string tag)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        _tag = string.IsNullOrWhiteSpace(tag) ? "onescreen" : tag;
        DirAccess.MakeDirRecursiveAbsolute(_dir);

        for (int i = 0; i < 1200 && Client.Game?.UO?.World == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        // The login screen: the dock where the window has room for it,
        // else the Servers button's modal (docs/ui/one_screen_panel.md).
        for (int i = 0; i < 600 && UIManager.GetGump<Game.UI.Gumps.Login.LoginGump>() == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        await InputProbe.Wait(host, 60);
        {
            var login = UIManager.GetGump<Game.UI.Gumps.Login.LoginGump>();
            var b0 = Client.Game.ClientBounds;
            bool room = b0.Width - 640 - 16 >= 280;
            bool docked = DualScreen.PanelShape == "dock";
            Check(room
                    ? "at the login screen with room beside it, the card is docked on the left and the login gump sits whole to its right"
                    : "at the login screen without room, no dock: the Servers button opens the card",
                room
                    ? docked && login != null && login.X >= DualScreen.PanelRect.End.X && login.X + 640 <= b0.Width
                        && GUO.Input.Touch.Pregame.PregameCard.ShownOnSecond && !GUO.Input.Touch.Pregame.PregameCard.ServersButtonShown
                    : !docked && GUO.Input.Touch.Pregame.PregameCard.ServersButtonShown,
                $"client {b0.Width}x{b0.Height}, shape {DualScreen.PanelShape}, dock {DualScreen.PanelRect}, login at {login?.X},{login?.Y}");
            await Shot(host, "login");
        }

        // The probe logs in with the touch layer off (below); the panel's
        // default follows the touch layer, so it is held as it is now.
        DualScreenSettings.PanelForced ??= DualScreenSettings.Current.PanelOn;

        if (!Client.Game.UO.World.InGame)
        {
            // As GamepadProbe logs in: the touch layer would swallow the
            // probe's clicks, which aim in client pixels.
            bool touchOn = GUO.Input.Touch.TouchInput.Enabled;
            GUO.Input.Touch.TouchInput.Enabled = false;
            InputProbe.PointerScale = Client.Game.DpiScale;
            await InputProbe.EnterTheWorld(host, 200);
            InputProbe.PointerScale = 1f;
            GUO.Input.Touch.TouchInput.Enabled = touchOn;
        }

        if (!Client.Game.UO.World.InGame)
        {
            Check("got into the world", false);
            Finish();
            return;
        }

        await InputProbe.Wait(host, 60);

        var world = Client.Game.UO.World;
        DualScreenSettings.Values kept = DualScreenSettings.Current;
        DualScreenSettings.Edit(v => { v.DrawerRight = false; v.SquareSplit = false; });
        await InputProbe.Wait(host, 10);

        var bounds = Client.Game.ClientBounds;
        Check("in the world on one screen the second screen is a drawer, closed, with its tab on the left edge",
            DualScreen.PanelShape == "drawer" && !DualScreen.DrawerOpen && DualScreen.TabRect is Rect2 t0 && t0.Position.X < 4,
            $"shape {DualScreen.PanelShape}, rect {DualScreen.PanelRect}, tab {DualScreen.TabRect}, client {bounds.Width}x{bounds.Height} at dpi {Client.Game.DpiScale:0.##}");

        Game.GameActions.OpenPaperdoll(world, world.Player);
        Game.GameActions.OpenBackpack(world);
        Game.GameActions.OpenStatusBar(world);
        Game.GameActions.OpenJournal(world);
        await InputProbe.Wait(host, 60);

        PaperDollGump doll = UIManager.Gumps.OfType<PaperDollGump>().FirstOrDefault(g => g.LocalSerial == world.Player.Serial);
        Check("the shelf gumps open on the virtual second screen, out of the world's way",
            DualScreen.ShelfOn && doll != null && doll.X >= DualScreen.MainWidth && DualScreen.ShelfCount >= 3,
            $"shelf on {DualScreen.ShelfOn}, paperdoll at {doll?.X},{doll?.Y}, main width {DualScreen.MainWidth}, on the shelf {DualScreen.ShelfCount}");
        await Shot(host, "closed");

        // A tap on the tab opens it.
        await Tap(host, DualScreen.TabRect.Value.GetCenter());
        await Settle(host);
        Check("a tap on the tab opens the drawer, and the tab moves to its inner edge",
            DualScreen.DrawerOpen && DualScreen.TabRect is Rect2 t1 && t1.Position.X > DualScreen.PanelRect.Size.X * Client.Game.DpiScale - 8,
            $"open {DualScreen.DrawerOpen}, tab {DualScreen.TabRect}");
        await Shot(host, "open");

        // A tap on the paperdoll inside the drawer reaches the client there.
        if (doll != null)
        {
            float dpi = Client.Game.DpiScale;
            Vector2 onDoll = new Vector2(doll.X - DualScreen.MainWidth + doll.Width / 2f, doll.Y + 20) * dpi;
            await Tap(host, onDoll);
            await InputProbe.Wait(host, 10);
            Check("a tap inside the open drawer lands on the virtual second screen and leaves the drawer open",
                DualScreen.DrawerOpen && GUO.Input.Mouse.Position.X >= DualScreen.MainWidth,
                $"pointer at {GUO.Input.Mouse.Position.X},{GUO.Input.Mouse.Position.Y}, open {DualScreen.DrawerOpen}");
        }

        // A tap on the world beside it closes it, and does nothing else.
        Vector2 world1 = new Vector2(Client.Game.ClientBounds.Width - 20, Client.Game.ClientBounds.Height / 2f) * Client.Game.DpiScale;
        await Tap(host, world1);
        await Settle(host);
        Check("a tap outside the drawer closes it", !DualScreen.DrawerOpen, $"open {DualScreen.DrawerOpen}");

        // The pad's Back button, twice.
        await Button(host, JoyButton.Back);
        await Settle(host);
        bool padOpened = DualScreen.DrawerOpen;
        bool glyph = Input.Glyphs.InputGlyphs.LastShown.TryGetValue(Input.PadAction.Back, out string shown) && shown == "pad_back";
        Check("with the pad in use, the tab shows the Back glyph", glyph, $"shown {shown ?? "nothing"}, input {Input.InputMode.Current}");
        await Shot(host, "open_pad");
        await Button(host, JoyButton.Back);
        await Settle(host);
        Check("the pad's Back button opens the drawer, and closes it again", padOpened && !DualScreen.DrawerOpen, $"opened {padOpened}, now {DualScreen.DrawerOpen}");

        // A drag on the tab pulls it open; one back pushes it closed.
        Vector2 tab = DualScreen.TabRect.Value.GetCenter();
        float width = DualScreen.PanelRect.Size.X * Client.Game.DpiScale;
        await Drag(host, tab, tab + new Vector2(width * 0.7f, 0));
        await Settle(host);
        bool dragged = DualScreen.DrawerOpen;
        tab = DualScreen.TabRect.Value.GetCenter();
        await Drag(host, tab, tab - new Vector2(width * 0.7f, 0));
        await Settle(host);
        Check("a drag on the tab pulls the drawer open, and pushes it closed", dragged && !DualScreen.DrawerOpen, $"after the pull {dragged}, after the push {DualScreen.DrawerOpen}");

        // The other edge.
        DualScreenSettings.Edit(v => v.DrawerRight = true);
        await InputProbe.Wait(host, 20);
        Rect2I right = DualScreen.PanelRect;
        Check("with the setting on the right, the drawer and its tab are on the right edge",
            DualScreen.PanelShape == "drawer" && right.End.X == Client.Game.ClientBounds.Width && DualScreen.TabRect is Rect2 t2
                && t2.End.X > Client.Game.ClientBounds.Width * Client.Game.DpiScale - 4,
            $"rect {right}, tab {DualScreen.TabRect}");
        await Tap(host, DualScreen.TabRect.Value.GetCenter());
        await Settle(host);
        Check("the right-hand drawer opens from its tab", DualScreen.DrawerOpen);
        await Shot(host, "open_right");
        DualScreenSettings.Edit(v => v.DrawerRight = false);
        await InputProbe.Wait(host, 20);

        // The split, where the window is near square.
        float ratio = (float) System.Math.Max(bounds.Width, bounds.Height) / System.Math.Min(bounds.Width, bounds.Height);

        if (ratio <= 1.34f)
        {
            DualScreenSettings.Edit(v => v.SquareSplit = true);
            await InputProbe.Wait(host, 30);
            Rect2I split = DualScreen.PanelRect;
            Check("near square, with the split chosen, the panel is the bottom half and the tab is gone",
                DualScreen.PanelShape == "split" && split.Position.X == 0 && split.End.Y == Client.Game.ClientBounds.Height
                    && split.Position.Y > 0 && DualScreen.TabRect == null,
                $"shape {DualScreen.PanelShape}, rect {split}");
            await Shot(host, "split");
            DualScreenSettings.Edit(v => v.SquareSplit = false);
            await InputProbe.Wait(host, 30);
        }
        else
        {
            GD.Print($"[GUO] one-screen probe: the split is not offered at {bounds.Width}x{bounds.Height} (ratio {ratio:0.00})");
        }

        DualScreenSettings.Set(kept);
        Finish();
    }

    private static void Finish()
    {
        Passed = _failed == 0 && _checks > 0;
        GD.Print($"[GUO] one-screen probe: {_checks - _failed}/{_checks} checks passed");
    }

    private static async System.Threading.Tasks.Task Settle(Node host)
    {
        for (int i = 0; i < 60 && !DualScreen.DrawerSettled; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        await InputProbe.Wait(host, 2);
    }

    private static async System.Threading.Tasks.Task Tap(Node host, Vector2 at)
    {
        Press(at, true);
        await InputProbe.Wait(host, 3);
        Press(at, false);
        await InputProbe.Wait(host, 3);
    }

    private static void Press(Vector2 at, bool down)
    {
        if (GUO.Input.Touch.TouchInput.Enabled)
        {
            Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = down });
        }
        else
        {
            Godot.Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Position = at, GlobalPosition = at, Pressed = down,
                ButtonMask = down ? MouseButtonMask.Left : 0,
            });
        }
    }

    private static async System.Threading.Tasks.Task Drag(Node host, Vector2 from, Vector2 to)
    {
        Press(from, true);
        await InputProbe.Wait(host, 2);

        for (int i = 1; i <= 8; i++)
        {
            Vector2 at = from.Lerp(to, i / 8f);

            if (GUO.Input.Touch.TouchInput.Enabled)
            {
                Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = at });
            }
            else
            {
                Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
            }

            await InputProbe.Wait(host, 1);
        }

        Press(to, false);
        await InputProbe.Wait(host, 3);
    }

    private static async System.Threading.Tasks.Task Button(Node host, JoyButton button)
    {
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = true });
        await InputProbe.Wait(host, 2);
        Godot.Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = false });
        await InputProbe.Wait(host, 4);
    }

    private static async System.Threading.Tasks.Task Shot(Node host, string name)
    {
        await InputProbe.Wait(host, 6);
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        string path = _dir.PathJoin($"{_tag}_{name}.png");
        host.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[GUO] one-screen probe: {name} -> {ProjectSettings.GlobalizePath(path)}");
    }
}
