// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;

namespace GUO.Host;

/// <summary>
/// On a desktop with default settings (no touch, no second screen, the dev
/// toggle "Mobile window controls" off), gump presentation must be absent:
/// a gump that carries a scale saved on mobile draws and hit-tests exactly as
/// the same gump at 100%, which is the path the client took before the feature
/// existed, and no window handle is drawn even with "Show window handles" on.
/// <c>--presentation-parity</c>; each check prints <c>[GUO] parity check: ok|FAIL</c>.
/// </summary>
internal static class PresentationParityProbe
{
    public static bool Passed { get; private set; }

    private static readonly List<(string what, bool ok)> Checks = new();

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        Checks.Clear();
        await InputProbe.EnterTheWorld(host, 0);
        Game.World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            Check("the character is in the world", false, "is the dev shard running?");
            Finish();
            return;
        }

        var profile = Configuration.ProfileManager.CurrentProfile;
        (bool mobile, bool handles) kept = (profile.MobileWindowControls, profile.ShowWindowHandles);
        profile.MobileWindowControls = false;

        try
        {
            Check("defaults: presentation is off on a desktop without touch",
                !GumpPresentation.Active && !TouchInput.Enabled, $"touch {TouchInput.Enabled}");

            UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
            await InputProbe.Wait(host, 5);
            Game.GameActions.OpenPaperdoll(world, world.Player);
            await InputProbe.Wait(host, 40);
            PaperDollGump g = UIManager.GetGump<PaperDollGump>(world.Player.Serial);

            if (g == null)
            {
                Check("a paperdoll to compare", false);
                return;
            }

            g.X = 100; g.Y = 100;
            g.BringOnTop();

            // At 100%: the path the client always took.
            g.PresentationScale = 1f;
            profile.ShowWindowHandles = false;
            Image before = await Capture(host, g);
            string hitsBefore = HitMap(g);

            // As if a mobile profile had saved 150% and the handle on.
            g.PresentationScale = 1.5f;
            profile.ShowWindowHandles = true;
            Image after = await Capture(host, g);
            string hitsAfter = HitMap(g);

            bool same = before.GetData().AsSpan().SequenceEqual(after.GetData());
            Check("a gump carrying a mobile 150% scale draws byte-identical to 100%", same,
                $"{before.GetWidth()}x{before.GetHeight()} px compared");
            Check("and hit-tests identically", hitsBefore == hitsAfter, $"{hitsBefore.Length / 2} points");
            Check("no window handle, even with \"Show window handles\" on", GumpPresentation.GemAlpha(g) == 0f);

            // The comparison can tell: with the dev toggle on, the same scale draws differently.
            profile.MobileWindowControls = true;
            Image mobile = await Capture(host, g);
            Check("control: with \"Mobile window controls\" on, the 150% gump does draw larger",
                !before.GetData().AsSpan().SequenceEqual(mobile.GetData()));

            g.PresentationScale = 1f;
        }
        finally
        {
            (profile.MobileWindowControls, profile.ShowWindowHandles) = kept;
            Finish();
        }
    }

    /// <summary>The frame's pixels over the gump's 100% bounds plus a margin (for a handle).</summary>
    private static async System.Threading.Tasks.Task<Image> Capture(Node host, Gump g)
    {
        await InputProbe.Wait(host, 10);
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        float dpi = Client.Game.DpiScale;
        using Image frame = host.GetViewport().GetTexture().GetImage();
        var r = new Rect2I((int)((g.X - 40) * dpi), (int)((g.Y - 40) * dpi), (int)((g.Width + 80) * dpi), (int)((g.Height + 80) * dpi));
        r = r.Intersection(new Rect2I(0, 0, frame.GetWidth(), frame.GetHeight()));
        return frame.GetRegion(r);
    }

    /// <summary>What the gump's hit test finds on a grid over its 100% bounds, as one string.</summary>
    private static string HitMap(Gump g)
    {
        var sb = new System.Text.StringBuilder();

        for (int y = g.Y; y < g.Y + g.Height; y += 8)
        {
            for (int x = g.X; x < g.X + g.Width; x += 8)
            {
                Game.UI.Controls.Control hit = null;
                g.HitTest(x, y, ref hit);
                sb.Append(hit == null ? '.' : (char)('A' + (uint)hit.GetHashCode() % 26)).Append(',');
            }
        }

        return sb.ToString();
    }

    private static void Check(string what, bool ok, string detail = null)
    {
        Checks.Add((what, ok));
        GD.Print($"[GUO] parity check: {(ok ? "ok  " : "FAIL")} {what}" + (detail == null ? "" : $" -- {detail}"));
    }

    private static void Finish()
    {
        int passed = 0;
        foreach ((string _, bool ok) in Checks) if (ok) passed++;
        Passed = passed == Checks.Count && Checks.Count > 0;
        GD.Print($"[GUO] presentation parity: {passed}/{Checks.Count} checks passed");
    }
}
