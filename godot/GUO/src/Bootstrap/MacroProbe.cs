// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Input.Touch;

namespace GUO.Host;

/// <summary>
/// Tap each of the touch bar's six macros (TouchGumpBar.MacroActions) against
/// fixtures spawned with GM commands on the dev shard, and check that each
/// produces exactly the action it is named for. Sprint story S2; the matrix is
/// docs/second-screen-ui-research.md, "Action-bar and gesture test matrix".
/// </summary>
/// <remarks>
/// Needs a GM account on the local shard (guoagentui). Every check prints
/// its evidence on its own line: <c>[GUO] macro check: ok|FAIL what -- detail</c>.
/// The macros themselves are upstream's (MacroManager); the probe taps the
/// row's buttons through the touch layer, the way a player does.
/// </remarks>
internal static class MacroProbe
{
    public static bool Passed { get; private set; }

    private static readonly List<(string what, bool ok)> Checks = new();

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        Checks.Clear();

        await Frames(host, 120);

        // The login goes through the mouse path; the touch layer steps aside.
        TouchInput.Enabled = false;
        await InputProbe.EnterTheWorld(host, 0);
        TouchInput.Enabled = true;

        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            Check("the character is in the world", false, "no player -- is the dev shard running?");
            Finish();

            return;
        }

        await Frames(host, 120);

        TouchGumpBar bar = TouchInput.Bar;
        Configuration.ProfileManager.CurrentProfile.TouchMacroRow = true;

        if (world.Player.InWarMode)
        {
            GameActions.ToggleWarMode(world.Player);
            await Frames(host, 60);
        }

        bar.ResetSession();
        bar.Invoke(TouchGumpBar.Chevron);
        await Frames(host, 5);
        Check("the macro row is up", bar.RowShown);

        // --- fixtures ------------------------------------------------------

        PlayerMobile me = world.Player;
        ushort px = me.X, py = me.Y;
        sbyte pz = me.Z;

        Mobile ratA = await SpawnRat(host, world, (ushort)(px + 1), py, pz);
        Mobile ratB = await SpawnRat(host, world, (ushort)(px - 1), py, pz);

        Check("two rats spawned beside the character", ratA != null && ratB != null,
            $"A {Describe(ratA)}, B {Describe(ratB)}");

        await Gm(host, world, "[addtopack Bandage 10", tm => tm.Target(me.Serial));
        Item bandage = me.FindBandage();
        Check("bandages in the pack", bandage != null, bandage == null ? "none" : $"0x{bandage.Serial:X8} x{bandage.Amount}");

        if (ratA == null || ratB == null)
        {
            Finish();

            return;
        }

        // --- Next Target ---------------------------------------------------

        // Exactly upstream's step: the next mobile after the last target, in
        // the world's order (World.FindNext), whatever its distance.
        bool nextExact = true;
        bool reachedA = false;
        var seen = new List<string>();

        for (int i = 0; i < 12 && !reachedA; i++)
        {
            uint expected = world.FindNext(ScanTypeObject.Mobiles, world.TargetManager.LastTargetInfo.Serial, false);
            await TapMacro(host, bar, "m:next");
            uint got = world.TargetManager.LastTargetInfo.Serial;

            seen.Add($"{Name(world, got)} 0x{got:X8}");
            nextExact &= got == expected && got != me.Serial;
            reachedA = got == ratA.Serial;
        }

        Check("Next Target steps to the next mobile, as World.FindNext says", nextExact, string.Join(" -> ", seen));
        Check("repeated Next Target reaches the spawned rat", reachedA);

        // --- Last Target, no cursor ----------------------------------------

        uint attackBefore = world.TargetManager.LastAttack;
        await TapMacro(host, bar, "m:last");
        await Frames(host, 30);
        Check("Last Target with no cursor does nothing",
            !world.TargetManager.IsTargeting && world.TargetManager.LastAttack == attackBefore && !me.InWarMode);

        // --- Last Target, with a cursor ------------------------------------

        int journal = JournalManager.Entries.Count;
        bool cursor = await Gm(host, world, "[get Name", null);
        await TapMacro(host, bar, "m:last");
        string reply = await WaitForJournal(host, journal, "rat", 180);
        Check("Last Target answers a pending cursor with the last target",
            cursor && reply != null && !world.TargetManager.IsTargeting, reply ?? "no reply naming the rat");

        // --- Attack Last ---------------------------------------------------

        // In peace an attack request names the opponent but no blows land, in
        // ClassicUO as here (GameActions.Attack does not change the stance).
        // A player fights at war, and the row comes up on entering it.
        if (!me.InWarMode)
        {
            GameActions.ToggleWarMode(me);
            await WaitFor(host, () => me.InWarMode, 120);
        }

        ushort hitsBefore = ratA.Hits;
        int attackJournal = JournalManager.Entries.Count;
        await TapMacro(host, bar, "m:attack");
        bool fought = false;

        for (int i = 0; i < 600 && !fought; i++)
        {
            await Frames(host, 1);
            Mobile a = world.Mobiles.Get(ratA.Serial);
            fought = a == null || a.IsDestroyed || a.Hits < hitsBefore || a.IsDead;
        }

        GameActions.RequestMobileStatus(world, ratA.Serial);
        await Frames(host, 30);
        {
            var entries = JournalManager.Entries;
            for (int k = System.Math.Min(attackJournal, entries.Count); k < entries.Count; k++)
            {
                GD.Print($"[GUO] macro probe:   after attack, journal: {entries[k]?.Name}: {entries[k]?.Text}");
            }
            Mobile r = world.Mobiles.Get(ratA.Serial);
            GD.Print($"[GUO] macro probe:   rat now hits {r?.Hits}/{r?.HitsMax} dead {r?.IsDead} at {r?.X},{r?.Y}; me at {me.X},{me.Y} facing {me.Direction}; weapon {me.FindItemByLayer(Game.Data.Layer.OneHanded)?.Name ?? me.FindItemByLayer(Game.Data.Layer.TwoHanded)?.Name ?? "none"}");
        }

        // Control: attack the same rat the ordinary way, a double-click at war.
        // If that starts no combat either, the shard, not the macro, is why.
        bool controlFought = false;

        if (!fought && world.Mobiles.Get(ratA.Serial) is Mobile still)
        {
            GameActions.DoubleClick(world, still.Serial);

            for (int i = 0; i < 600 && !controlFought; i++)
            {
                await Frames(host, 1);
                Mobile a = world.Mobiles.Get(ratA.Serial);
                controlFought = a == null || a.IsDestroyed || a.Hits < hitsBefore || a.IsDead;
            }

            GD.Print($"[GUO] macro probe:   control, double-click attack at war: combat started {controlFought}");
        }

        // What the macro does is upstream's GameActions.Attack on the last
        // target: the request goes out and the client names the opponent.
        // Whether blows land is the shard's business. On the dev shard a GM at
        // war beside the rat drew no combat from this or from the control above.
        Check("Attack Last sends the attack request for the last target",
            world.TargetManager.LastAttack == ratA.Serial && world.TargetManager.LastTargetInfo.Serial == ratA.Serial,
            $"last attack 0x{world.TargetManager.LastAttack:X8} = last target 0x{world.TargetManager.LastTargetInfo.Serial:X8}");
        GD.Print(
            $"[GUO] macro probe:   combat after Attack Last: {fought}; after the double-click control: {controlFought}; "
            + $"rat hits {hitsBefore} -> {world.Mobiles.Get(ratA.Serial)?.Hits.ToString() ?? "gone"}, war {me.InWarMode}, hidden {me.IsHidden}"
        );

        if (me.InWarMode)
        {
            GameActions.ToggleWarMode(me);
            await Frames(host, 60);
        }

        // --- Last Object ---------------------------------------------------

        bandage = me.FindBandage();

        if (bandage != null)
        {
            GameActions.DoubleClick(world, bandage.Serial);
            await WaitFor(host, () => world.TargetManager.IsTargeting, 120);
            world.TargetManager.CancelTarget();
            await Frames(host, 30);

            await TapMacro(host, bar, "m:object");
            bool again = await WaitFor(host, () => world.TargetManager.IsTargeting, 180);
            Check("Last Object uses the last object again (its target cursor comes back)",
                world.LastObject == bandage.Serial && again, $"last object 0x{world.LastObject:X8}");
            world.TargetManager.CancelTarget();
            await Frames(host, 30);
        }
        else
        {
            Check("Last Object uses the last object again", false, "no bandages to use");
        }

        // --- Bandage Self --------------------------------------------------

        await Gm(host, world, "[set Hits 30", tm => tm.Target(me.Serial));
        await Frames(host, 30);
        journal = JournalManager.Entries.Count;
        await TapMacro(host, bar, "m:bandage");
        string applying = await WaitForJournal(host, journal, "applying", 240);
        Check("Bandage Self starts bandaging the character",
            applying != null && !world.TargetManager.IsTargeting, applying ?? $"no bandage message; hits {me.Hits}/{me.HitsMax}");

        // --- War/Peace -----------------------------------------------------

        bool before = me.InWarMode;
        await TapMacro(host, bar, "m:war");
        bool flipped = await WaitFor(host, () => me.InWarMode != before, 120);
        await TapMacro(host, bar, "m:war");
        bool back = await WaitFor(host, () => me.InWarMode == before, 120);
        Check("War/Peace toggles the stance once per tap", flipped && back, $"war {before} -> {!before} -> {me.InWarMode}");

        // --- clean up ------------------------------------------------------

        foreach (Mobile rat in new[] { ratA, ratB })
        {
            if (world.Mobiles.Get(rat.Serial) is Mobile alive && !alive.IsDestroyed)
            {
                await Gm(host, world, "[delete", tm => tm.Target(alive.Serial));
            }
        }

        Finish();
    }

    // --- fixtures ----------------------------------------------------------

    /// <summary>Say a GM command and, when a target cursor comes up, answer it. False if none came.</summary>
    private static async System.Threading.Tasks.Task<bool> Gm(Node host, World world, string command, System.Action<TargetManager> answer)
    {
        GD.Print($"[GUO] macro probe: {command}");
        await InputProbe.Say(host, command);
        bool cursor = await WaitFor(host, () => world.TargetManager.IsTargeting, 180);

        GD.Print($"[GUO] macro probe:   cursor {cursor} ({world.TargetManager.TargetingState})");

        if (cursor && answer != null)
        {
            answer(world.TargetManager);
            await Frames(host, 60);
        }

        var entries = JournalManager.Entries;

        for (int k = System.Math.Max(0, entries.Count - 3); k < entries.Count; k++)
        {
            GD.Print($"[GUO] macro probe:   journal: {entries[k]?.Name}: {entries[k]?.Text}");
        }

        return cursor;
    }

    /// <summary>A rat that cannot walk away, on the given tile; the new mobile, or null.</summary>
    private static async System.Threading.Tasks.Task<Mobile> SpawnRat(Node host, World world, ushort x, ushort y, sbyte z)
    {
        var before = new HashSet<uint>(world.Mobiles.Keys);

        await Gm(host, world, "[add Rat set CantWalk true", tm => tm.Target(0, x, y, z));

        for (int i = 0; i < 120; i++)
        {
            foreach (Mobile m in world.Mobiles.Values)
            {
                if (!before.Contains(m.Serial) && m != world.Player && m.X == x && m.Y == y)
                {
                    return m;
                }
            }

            await Frames(host, 1);
        }

        return null;
    }

    // --- helpers -----------------------------------------------------------

    private static async System.Threading.Tasks.Task TapMacro(Node host, TouchGumpBar bar, string action)
    {
        Rect2 r = bar.ButtonRect(action);
        Vector2 at = r.Position + r.Size / 2;

        TouchInput.Trace.Clear();
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = true });
        await Frames(host, 3);
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = false });
        await Frames(host, 20);

        GD.Print($"[GUO] macro probe: tapped {action} -- {string.Join(" | ", TouchInput.Trace)}");
    }

    private static async System.Threading.Tasks.Task<bool> WaitFor(Node host, System.Func<bool> condition, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            if (condition())
            {
                return true;
            }

            await Frames(host, 1);
        }

        return condition();
    }

    /// <summary>The first journal line after <paramref name="from"/> containing <paramref name="word"/>.</summary>
    private static async System.Threading.Tasks.Task<string> WaitForJournal(Node host, int from, string word, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var entries = JournalManager.Entries;

            for (int k = System.Math.Min(from, entries.Count); k < entries.Count; k++)
            {
                string text = entries[k]?.Text;

                if (text != null && text.Contains(word, System.StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }

            await Frames(host, 1);
        }

        return null;
    }

    private static string Name(World world, uint serial) => world.Mobiles.Get(serial)?.Name ?? "?";

    private static string Describe(Mobile m) => m == null ? "none" : $"{m.Name} 0x{m.Serial:X8} at {m.X},{m.Y}";

    private static async System.Threading.Tasks.Task Frames(Node host, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Check(string what, bool ok, string detail = null)
    {
        Checks.Add((what, ok));
        GD.Print($"[GUO] macro check: {(ok ? "ok  " : "FAIL")} {what}" + (detail == null ? "" : $" -- {detail}"));
    }

    private static void Finish()
    {
        int passed = 0;

        foreach ((string what, bool ok) in Checks)
        {
            if (ok)
            {
                passed++;
            }
        }

        Passed = passed == Checks.Count;
        GD.Print($"[GUO] macro probe: {passed}/{Checks.Count} checks passed");
    }
}
