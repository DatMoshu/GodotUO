// SPDX-License-Identifier: BSD-2-Clause

using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps.Login;
using GUO.Input.Touch.Pregame;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// The second screen before the world (--pregame-probe): the pre-game card
/// comes up at the login screen, its tabs and groups answer taps, a setting
/// changed there reaches settings.json and the login gump's own box, every
/// group fits the card, and each group is photographed from the second
/// screen. Needs a second display: a device's, or --dual-screen WxH on a
/// desktop. Logs no one in.
/// </summary>
internal static class PregameProbe
{
    public static bool Passed { get; private set; }

    private static int _failed, _checks;
    private static string _dir, _tag;

    private static void Check(string what, bool ok, string detail = "")
    {
        _checks++;
        GD.Print($"[GUO] pregame probe: {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " -- " + detail : "")}");

        if (!ok)
        {
            _failed++;
        }
    }

    public static async System.Threading.Tasks.Task Run(Node host, string dir, string tag)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        _tag = string.IsNullOrWhiteSpace(tag) ? "pregame" : tag;
        DirAccess.MakeDirRecursiveAbsolute(_dir);

        PregameCard card = null;

        for (int i = 0; i < 1800 && (card = PregameCard.Instance) == null; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        if (!DualScreen.HasSecondaryDisplay)
        {
            GD.Print("[GUO] pregame probe: no second display (give --dual-screen WxH on a desktop); nothing to probe");
            Passed = true;
            return;
        }

        Check("the pre-game card is up on the second screen at the login screen",
            card != null && PregameCard.Shown && UIManager.GetGump<LoginGump>() != null,
            card == null ? "not built" : card.Geometry);

        if (card == null)
        {
            Finish();
            return;
        }

        await InputProbe.Wait(host, 10);

        // The tabs.
        card.Tap(card.TabButtonFor(PregameCard.Tab.Servers));
        await InputProbe.Wait(host, 5);
        bool servers = card.Current == PregameCard.Tab.Servers;
        await Save(host, "servers");
        card.Tap(card.TabButtonFor(PregameCard.Tab.Settings));
        await InputProbe.Wait(host, 5);
        Check("a tap on a tab opens it: Servers, then Settings", servers && card.Current == PregameCard.Tab.Settings);

        PregameSettings settings = card.Settings;
        Vector2 cardSize = new(DualScreen.LogicalWidth, DualScreen.LogicalHeight);

        // Every group: a tap opens it, it says something, and it fits the card.
        string misfits = "";

        foreach (string group in PregameSettings.Groups)
        {
            card.Tap(settings.GroupButton(group));
            await InputProbe.Wait(host, 6);

            if (settings.Group != group)
            {
                misfits += $" {group}: not opened;";
                continue;
            }

            foreach (Control c in settings.Fields)
            {
                Rect2 r = c.GetGlobalRect();

                if (c.IsVisibleInTree() && (r.End.X > cardSize.X + 0.5f || r.Position.X < -0.5f))
                {
                    misfits += $" {group}: \"{(c as Label)?.Text ?? (c as Button)?.Text ?? c.GetType().Name}\" {r.Position.X:F0}..{r.End.X:F0};";
                }
            }

            await Save(host, "settings_" + group.ToLowerInvariant().Replace(' ', '_'));
        }

        Check("every settings group opens on a tap and fits the card's width", misfits.Length == 0, misfits.Length == 0 ? $"{PregameSettings.Groups.Length} groups, card {card.Geometry}" : misfits);

        // Profile-only settings are named, not shown disabled.
        card.Tap(settings.GroupButton("Controls"));
        await InputProbe.Wait(host, 5);
        string controls = settings.Text;
        Check("profile-only settings say where they are set",
            controls.Contains("Set in Options once you're in the world") && !settings.Fields.OfType<BaseButton>().Any(b => b.Disabled),
            controls.Length > 120 ? controls[..120] : controls);

        // A setting the login gump also has: through its box, into settings.json.
        card.Tap(settings.GroupButton("Sound"));
        await InputProbe.Wait(host, 5);
        bool musicWas = Settings.GlobalSettings.LoginMusic;
        CheckBox music = settings.Fields.OfType<CheckBox>().FirstOrDefault(b => b.Text == "Login music");
        bool tapped = false, followed = false;

        if (music != null)
        {
            card.Tap(music);
            await InputProbe.Wait(host, 5);
            tapped = Settings.GlobalSettings.LoginMusic != musicWas;
            followed = LoginBox("Music") == Settings.GlobalSettings.LoginMusic;
            card.Tap(music);
            await InputProbe.Wait(host, 5);
        }

        Check("login music: a tap changes the setting and the login gump's own box follows; a second tap puts it back",
            tapped && followed && Settings.GlobalSettings.LoginMusic == musicWas,
            $"box found {music != null}, changed {tapped}, login box followed {followed}, back {Settings.GlobalSettings.LoginMusic == musicWas}");

        // The second screen's own settings.
        card.Tap(settings.GroupButton("Second screen"));
        await InputProbe.Wait(host, 5);
        bool journalWas = DualScreenSettings.Current.Journal;
        CheckBox journal = settings.Fields.OfType<CheckBox>().FirstOrDefault(b => b.Text == "Journal");
        bool changed = false;

        if (journal != null)
        {
            card.Tap(journal);
            await InputProbe.Wait(host, 5);
            changed = DualScreenSettings.Current.Journal != journalWas;
            card.Tap(journal);
            await InputProbe.Wait(host, 5);
        }

        Check("second screen: a tap on Journal changes the shelf setting, and back",
            changed && DualScreenSettings.Current.Journal == journalWas, $"box found {journal != null}, changed {changed}");

        // Leave it on Settings > Your UO files, as a player first sees it.
        card.Tap(settings.GroupButton(PregameSettings.Groups[0]));
        await InputProbe.Wait(host, 5);

        Finish();
    }

    private static bool? LoginBox(string text)
    {
        LoginGump login = UIManager.GetGump<LoginGump>();

        if (login == null)
        {
            return null;
        }

        return All(login).OfType<Game.UI.Controls.Checkbox>().FirstOrDefault(c => c.Text == text)?.IsChecked;
    }

    private static System.Collections.Generic.IEnumerable<Game.UI.Controls.Control> All(Game.UI.Controls.Control c) =>
        c.Children.SelectMany(x => new[] { x }.Concat(All(x)));

    private static void Finish()
    {
        Passed = _failed == 0;
        GD.Print($"[GUO] pregame probe: {_checks - _failed}/{_checks} checks passed");
        GD.Print(Passed ? "[GUO] pregame: ok" : "[GUO] pregame: FAIL");
    }

    private static async System.Threading.Tasks.Task Save(Node host, string name)
    {
        await InputProbe.Wait(host, 6);
        string path = _dir.PathJoin($"{_tag}_{name}_second.png");

        if (DualScreen.SaveFrame(path))
        {
            GD.Print($"[GUO] pregame probe: {name} -> {ProjectSettings.GlobalizePath(path)}");
        }
    }
}
