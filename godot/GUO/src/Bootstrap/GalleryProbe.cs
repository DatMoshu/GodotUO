// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// Open each of GUO's own mobile UIs in turn and save a picture of each: the
/// command bar, the window menu, the minimised-window chips, the companion
/// tabs, the Options touch section, and the command bar's hold popup and slot
/// editor when they exist. For before/after comparisons of the UO style pass
/// (docs/ui/uo_godot_style.md); it checks nothing.
/// </summary>
/// <remarks>
/// Run with <c>--ui-gallery --screenshot-dir D --screenshot-name TAG</c>, and
/// <c>--dual-screen 1240x1080 --window-size 1920,1080</c> for the Thor's
/// shape. Each picture is TAG_NAME.png, with TAG_NAME_second.png for the
/// second screen. Needs the dev shard.
/// </remarks>
internal static class GalleryProbe
{
    public static bool Passed { get; private set; }

    private static string _dir;
    private static string _tag;

    public static async System.Threading.Tasks.Task Run(Node host, string dir, string tag)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        _tag = string.IsNullOrWhiteSpace(tag) ? "gallery" : tag;
        DirAccess.MakeDirRecursiveAbsolute(_dir);

        await InputProbe.Wait(host, 200);

        bool touch = TouchInput.Enabled;
        TouchInput.Enabled = false;
        await InputProbe.EnterTheWorld(host, 0);
        TouchInput.Enabled = touch;

        Game.World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] gallery: FAIL never got into the world -- is the dev shard running?");
            return;
        }

        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;
        profile.TouchMacroRow = true;
        await InputProbe.Wait(host, 120);

        // The last target, so the bar shows its header.
        Game.GameObjects.Mobile nearest = null;

        foreach (Game.GameObjects.Mobile m in world.Mobiles.Values)
        {
            if (m != world.Player && (nearest == null || m.Distance < nearest.Distance))
            {
                nearest = m;
            }
        }

        if (nearest != null)
        {
            world.TargetManager.LastTargetInfo.SetEntity(nearest.Serial);
        }

        Game.GameActions.OpenPaperdoll(world, world.Player);
        await InputProbe.Wait(host, 60);
        PaperDollGump paperdoll = UIManager.GetGump<PaperDollGump>(world.Player.Serial);

        // The bar, three rows open.
        TouchGumpBar bar = TouchInput.Bar;

        if (bar != null)
        {
            bar.ResetSession();
            bar.TapHandle();
            await InputProbe.Wait(host, 30);
            bar.TapHandle();
            await InputProbe.Wait(host, 5);
            bar.TapHandle();
            await InputProbe.Wait(host, 30);
            await Save(host, "bar");

            // The hold popup and the slot editor, where this build has them.
            if (await HoldPopup(host, bar))
            {
                await Save(host, "popup");
            }
        }

        // The window menu, for the paperdoll.
        if (paperdoll != null)
        {
            WindowMenu.Open(paperdoll);
            await InputProbe.Wait(host, 30);
            await Save(host, "window_menu");
            WindowMenu.Close();
            await InputProbe.Wait(host, 5);

            // Minimised: the chips (the tray on one screen, the lower screen on two).
            GumpMinimise.Minimise(paperdoll);
            Game.GameActions.OpenStatusBar(world);
            await InputProbe.Wait(host, 30);
            StatusGumpBase status = StatusGumpBase.GetStatusGump();
            GumpMinimise.Minimise(status);
            await InputProbe.Wait(host, 30);
            await Save(host, "chips");
            GumpMinimise.Restore(paperdoll);
            GumpMinimise.Restore(status);
            await InputProbe.Wait(host, 10);
        }

        // The companion tabs, where there is a second screen.
        if (DualScreen.HasSecondaryDisplay)
        {
            bool tabs = profile.CompanionTabs;
            profile.CompanionTabs = true;
            await InputProbe.Wait(host, 60);
            await Save(host, "companion");
            profile.CompanionTabs = tabs;
            await InputProbe.Wait(host, 30);
        }

        // The slot editor, where this build has one.
        if (await OpenEditor(host))
        {
            await Save(host, "editor");
        }

        // Options, as a touch player gets it (C11).
        Game.GameActions.OpenSettings(world);
        await InputProbe.Wait(host, 60);
        await Save(host, "options");
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await InputProbe.Wait(host, 10);

        Passed = true;
        GD.Print("[GUO] gallery: ok");
    }

    private static int _heldSlot = -1;

    /// <summary>
    /// Hold a finger on a bar button (Attack Last) until its popup opens and
    /// slide onto the first alternate; the picture is taken with the finger
    /// still down. The finger then lets go off the popup: nothing runs.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> HoldPopup(Node host, TouchGumpBar bar)
    {
        Rect2 attack = bar.ButtonRect("attack");

        if (attack.Size == Vector2.Zero)
        {
            return false;
        }

        Vector2 at = attack.GetCenter();
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = true });
        ulong until = Godot.Time.GetTicksMsec() + TouchInput.BarPopupMs + 150;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await InputProbe.Wait(host, 1);
        }

        _heldSlot = bar.PopupSlot;
        Vector2 alt = bar.PopupRect(1).GetCenter();
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = alt, Relative = alt - at });
        await InputProbe.Wait(host, 5);
        bool open = bar.PopupSlot >= 0;

        if (open)
        {
            await Save(host, "popup");
        }

        Vector2 away = new(at.X, 40);
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = away, Relative = away - alt });
        await InputProbe.Wait(host, 2);
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = away, Pressed = false });
        await InputProbe.Wait(host, 10);

        return false; // pictured already, with the finger down
    }

    /// <summary>Open the slot editor for that slot, on its alternates' picker.</summary>
    private static async System.Threading.Tasks.Task<bool> OpenEditor(Node host)
    {
        BarEditor.Open(_heldSlot >= 0 ? _heldSlot : 7);
        await InputProbe.Wait(host, 20);

        if (!BarEditor.IsOpen)
        {
            return false;
        }

        await Save(host, "editor");
        BarEditor.Close();
        await InputProbe.Wait(host, 5);

        return false;
    }

    private static async System.Threading.Tasks.Task Save(Node host, string name)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        Image frame = host.GetViewport().GetTexture().GetImage();
        string path = _dir.PathJoin($"{_tag}_{name}.png");
        frame.SavePng(path);
        GD.Print($"[GUO] gallery: {name} -> {ProjectSettings.GlobalizePath(path)}");

        if (DualScreen.Active)
        {
            await InputProbe.Wait(host, 3);
            string second = _dir.PathJoin($"{_tag}_{name}_second.png");

            if (DualScreen.SaveFrame(second))
            {
                GD.Print($"[GUO] gallery: {name} (second screen) -> {ProjectSettings.GlobalizePath(second)}");
            }
        }
    }
}
