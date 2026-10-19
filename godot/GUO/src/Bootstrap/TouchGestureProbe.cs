// SPDX-License-Identifier: BSD-2-Clause
// Touch scrolling at the login screen; no shard is required.
using System;
using System.Linq;
using System.Text;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;

namespace GUO.Host;

/// <summary>
/// The touch layer's scrolling, with synthetic fingers through Godot's own
/// input (as TouchProbe does), on a scratch gump at the login screen: a
/// one-finger swipe in a ScrollArea and in a text with its own scroll bar,
/// and a two-finger pan over a gump. Long press on someone in the world needs
/// a world and is in --touch-probe's territory, not here.
/// </summary>
internal static class TouchGestureProbe
{
    private static int _failed, _checks;

    private static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) _failed++;
        GD.Print($"[GUO] touch gesture probe: {(ok ? "ok" : "FAIL")} {name}{(detail.Length > 0 ? " -- " + detail : "")}");
    }

    private static Vector2 Window(int x, int y) => new Vector2(x, y) * Client.Game.DpiScale;

    private static void Touch(Vector2 at, bool down) =>
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = down });

    private static void Drag(Vector2 at, Vector2 relative) =>
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = at, Relative = relative });

    /// <summary>A finger down at <paramref name="from"/>, moved up by <paramref name="up"/> window pixels in steps, then lifted.</summary>
    private static async System.Threading.Tasks.Task Swipe(Node host, Vector2 from, float up)
    {
        Touch(from, true);
        await InputProbe.Wait(host, 2);
        Vector2 at = from;

        for (int i = 0; i < 10; i++)
        {
            Vector2 next = from - new Vector2(0, up * (i + 1) / 10f);
            Drag(next, next - at);
            at = next;
            await InputProbe.Wait(host, 1);
        }

        Touch(at, false);
        await InputProbe.Wait(host, 4);
    }

    private static ScrollBarBase Bar(GUO.Game.UI.Controls.Control c) => c.Children.OfType<ScrollBarBase>().FirstOrDefault();

    public static async System.Threading.Tasks.Task<bool> Run(Node host)
    {
        _failed = _checks = 0;

        try
        {
            return await Gestures(host);
        }
        catch (Exception e)
        {
            Check($"unexpected exception {e}", false);
            return Finish();
        }
    }

    private static async System.Threading.Tasks.Task<bool> Gestures(Node host)
    {
        // The art loads with the first scene; a scroll bar is built from it.
        for (int i = 0; i < 600 && !(Client.Game?.Scene is Game.Scenes.LoginScene); i++)
        {
            await InputProbe.Wait(host, 1);
        }

        await InputProbe.Wait(host, 10);
        Check("the login screen is up", Client.Game?.Scene is Game.Scenes.LoginScene);
        Check("the touch layer is on", TouchInput.Enabled);

        var gump = new Gump(Client.Game.UO.World, 0, 0) { X = 40, Y = 60, Width = 560, Height = 220, CanMove = true };
        var area = new ScrollArea(10, 10, 260, 200, true);

        for (int i = 0; i < 40; i++)
        {
            area.Add(new GUO.Game.UI.Controls.Label($"Row {i}", true, 0x0481, font: 1) { X = 0, Y = i * 20 });
        }

        var text = new StringBuilder();

        for (int i = 0; i < 60; i++)
        {
            text.Append("A line of text that keeps the page long enough to scroll. ");
        }

        var html = new HtmlControl(290, 10, 260, 200, true, true, text: text.ToString());
        gump.Add(area);
        gump.Add(html);
        UIManager.Add(gump);

        try
        {
            await InputProbe.Wait(host, 10);
            ScrollBarBase areaBar = area.Children.Count > 0 ? area.Children[0] as ScrollBarBase : null;
            ScrollBarBase htmlBar = Bar(html);
            Check("the scratch lists can scroll", areaBar != null && areaBar.MaxValue > areaBar.MinValue && htmlBar != null && htmlBar.MaxValue > htmlBar.MinValue,
                $"area {areaBar?.MaxValue}, text {htmlBar?.MaxValue}");

            if (areaBar == null || htmlBar == null)
            {
                return Finish();
            }

            // One finger in a ScrollArea (the Options pages; already so).
            int before = areaBar.Value;
            await Swipe(host, Window(gump.X + 140, gump.Y + 160), 80 * Client.Game.DpiScale);
            Check("a swipe up in a ScrollArea scrolls it", areaBar.Value > before && gump.X == 40 && gump.Y == 60,
                $"{before} -> {areaBar.Value}, gump at {gump.X},{gump.Y}");

            // One finger in a text with its own scroll bar (a book, a quest page).
            before = htmlBar.Value;
            await Swipe(host, Window(gump.X + 410, gump.Y + 160), 80 * Client.Game.DpiScale);
            Check("a swipe up in a scrolling text scrolls it", htmlBar.Value > before && gump.X == 40 && gump.Y == 60,
                $"{before} -> {htmlBar.Value}, gump at {gump.X},{gump.Y}");

            // Two fingers: the wheel over the list under them.
            areaBar.Value = areaBar.MinValue;
            await InputProbe.Wait(host, 2);
            Vector2 over = Window(gump.X + 140, gump.Y + 100);
            Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = over });
            await InputProbe.Wait(host, 3);

            for (int i = 0; i < 6; i++)
            {
                Godot.Input.ParseInputEvent(new InputEventPanGesture { Position = over, Delta = new Vector2(0, 20) });
                await InputProbe.Wait(host, 1);
            }

            await InputProbe.Wait(host, 3);
            Check("a two-finger pan over a list scrolls it", areaBar.Value > areaBar.MinValue,
                $"{areaBar.MinValue} -> {areaBar.Value} | {string.Join(" | ", TouchInput.Trace.TakeLast(4))}");
        }
        finally
        {
            gump.Dispose();
        }

        return Finish();
    }

    private static bool Finish()
    {
        GD.Print($"[GUO] touch gesture probe: {(_failed == 0 ? "PASS" : "FAIL")} ({_checks - _failed}/{_checks})");
        return _failed == 0;
    }
}
