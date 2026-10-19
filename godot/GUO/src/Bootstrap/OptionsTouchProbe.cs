// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Game.Managers;
using GUO.Input.Touch;
using GUO.Input.Touch.Modern;

namespace GUO.Host;

internal static partial class TouchProbe
{
    /// <summary>UX-02's bounded desktop synthetic-touch checks; no movement or combat.</summary>
    public static async System.Threading.Tasks.Task RunOptions(Node host, string directory)
    {
        Checks.Clear();
        Platform.Android.DualScreenSettings.PanelForced ??= false;
        await Frames(host, 200);
        TouchInput.Enabled = false;
        await InputProbe.EnterTheWorld(host, 0);
        var world = GUO.Client.Game.UO.World;
        Check("Options probe reached the world", world.InGame);
        if (!world.InGame) { Finish(); return; }
        await Frames(host, 60);
        var profile = ProfileManager.CurrentProfile;
        foreach (Gump g in UIManager.Gumps.ToArray())
            if (!g.IsFromServer && g is not WorldViewportGump && g is not TopBarGump) g.Dispose();

        async System.Threading.Tasks.Task Shot(string name)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            DirAccess.MakeDirRecursiveAbsolute(directory);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            host.GetViewport().GetTexture().GetImage().SavePng(directory.PathJoin(name + ".png"));
        }

        // The same list with the touch branch off and on. Both row padding
        // and text must select the row, and an overflowing list must scroll.
        foreach (bool touch in new[] { false, true })
        {
            TouchInput.Enabled = touch;
            var fixture = new Gump(world, 0, 0) { X = 180, Y = 140 };
            var combo = new Combobox(0, 0, 180, new[] { "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Last" }, 0, 104);
            fixture.Add(combo);
            UIManager.Add(fixture);
            await Frames(host, 10);
            await Tap(host, DrawnCentre(combo));
            await Frames(host, 10);
            Gump list = UIManager.Gumps.FirstOrDefault(g => !g.IsDisposed && g.GetType().Name == "ComboboxGump");
            Check(touch ? "touch popup opens" : "mouse popup opens", list != null);
            if (list != null)
            {
                var area = list.FindControls<ScrollArea>().OfType<ScrollArea>().First();
                var rows = area.Children.Skip(1).ToArray();
                Check(touch ? "touch rows have 26px hit targets" : "mouse rows keep classic 15px spacing",
                    touch ? rows.All(r => r is HitBox && r.Height == 26) : rows[1].Y - rows[0].Y == 15,
                    $"rows {rows.Length}, spacing {rows[1].Y - rows[0].Y}");
                await Shot(touch ? "touch-dropdown" : "mouse-dropdown-before");
                var row = rows[1];
                var point = GumpPresentation.ToScreen(row, new GUO.Compat.Point(row.ScreenCoordinateX + 12,
                    row.ScreenCoordinateY + (touch ? 24 : 5)));
                await Tap(host, Client(new Vector2(point.X, point.Y)));
                await Frames(host, 5);
                Check(touch ? "touch row padding selects its own item" : "mouse selection stays classic", combo.SelectedIndex == 1 && list.IsDisposed);
                await Tap(host, DrawnCentre(combo));
                await Frames(host, 5);
                list = UIManager.Gumps.First(g => !g.IsDisposed && g.GetType().Name == "ComboboxGump");
                area = list.FindControls<ScrollArea>().OfType<ScrollArea>().First();
                for (int i = 0; i < 30; i++) area.Scroll(false);
                await Frames(host, 5);
                var last = area.Children.Last();
                // ScrollArea applies the scrollbar offset when drawing its children.
                point = GumpPresentation.ToScreen(last, new GUO.Compat.Point(last.ScreenCoordinateX + 12,
                    last.ScreenCoordinateY + last.Height / 2 - area.ScrollValue));
                await Tap(host, Client(new Vector2(point.X, point.Y)));
                await Frames(host, 5);
                Check(touch ? "touch popup scrolls to the last item" : "mouse popup scrolls to the last item", combo.SelectedIndex == 7 && list.IsDisposed,
                    $"selected {combo.SelectedIndex}");
                list.Dispose();
            }
            fixture.Dispose();
        }

        TouchInput.Enabled = true;
        profile.ModernGumpsOff = true;
        await OptionsTouchCheck(host, world);
        profile.ModernGumpsOff = false;
        await ModernOptionsCheck(host, world);
        await ModernOptionsShortCheck(host, world);

        // Reopening the cached view after a Classic edit must retain the new
        // custom path; applying an unrelated setting must not reset it to grey.
        profile.CanvasBackgroundMode = "image";
        profile.CanvasBackgroundPath = "user://ux02-unavailable-image.png";
        Game.GameActions.OpenSettings(world);
        await Frames(host, 15);
        var view = ModernGump.Current as ModernOptions;
        Check("Modern Options opens for UX-02 settings", view != null);
        if (view != null)
        {
            view.Page("Video");
            await Frames(host, 5);
            var next = view.Find("Background behind the world next");
            await ScrollTo(host, next);
            await Shot("modern-background-current");
            await TapClient(host, view.CentreOf(view.Find("Apply")));
            Check("reopened Modern Options preserves a custom background on unrelated Apply",
                profile.CanvasBackgroundMode == "image" && profile.CanvasBackgroundPath == "user://ux02-unavailable-image.png");
            await TapClient(host, view.CentreOf(next)); // current custom -> grey
            await TapClient(host, view.CentreOf(next)); // grey -> wood
            await TapClient(host, view.CentreOf(view.Find("Apply")));
            Check("Modern background choice writes the classic mode field and preserves the typed path",
                profile.CanvasBackgroundMode == "builtin-wood" && profile.CanvasBackgroundPath == "user://ux02-unavailable-image.png");
            await TapClient(host, view.CentreOf(next)); // first shipped background
            await TapClient(host, view.CentreOf(view.Find("Cancel")));
            Check("Cancel does not apply the pending background choice", profile.CanvasBackgroundMode == "builtin-wood");

            Game.GameActions.OpenSettings(world);
            await Frames(host, 10);
            view = ModernGump.Current as ModernOptions;
            view.Page("Touch");
            await Frames(host, 5);
            var inset = view.Find("Command bar arrow inset from the corner");
            await ScrollTo(host, inset);
            Check("arrow inset is present on Modern Touch", inset is HSlider);
            Check("fine second-screen scale is hidden on a single display",
                Platform.Android.DualScreen.HasSecondaryDisplay || view.Find("Fine second screen scale next") == null);
            if (inset is HSlider slider)
            {
                int before = profile.TouchChevronInset;
                slider.Value = before == 40 ? 60 : 40;
                await TapClient(host, view.CentreOf(view.Find("Okay")));
                Check("Modern inset writes the classic profile field on Okay", profile.TouchChevronInset == (before == 40 ? 60 : 40));
            }
            ModernGump.Current?.Close();
        }
        Finish();
        GD.Print($"[GUO] options touch probe: {(Passed ? "PASS" : "FAIL")}");
        await Frames(host, 300); // let a scenario observe the verdict before normal exit
    }
}
