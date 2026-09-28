// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Log in and type administration commands into the game window.
/// </summary>
/// <remarks>
/// This is not part of the client: it is how the local dev shard is
/// administered. ModernUO takes its commands in game and not at the console,
/// so generating a world -- spawners, decorations, everything that turns an
/// empty map into somewhere to play -- means a client typing them as an owner
/// account. Doing it through the same synthesised input the probe uses keeps
/// one path in and no back door into the client's own state.
///
/// Nothing here belongs to a shard the user does not own. It sends text the
/// server may well refuse.
/// </remarks>
internal static class ShardCommands
{
    /// <summary>True when every command was answered by the server.</summary>
    public static bool Passed { get; private set; }

    public static async System.Threading.Tasks.Task Run(
        Node host,
        System.Collections.Generic.IReadOnlyList<string> commands
    )
    {
        await InputProbe.EnterTheWorld(host, 200);

        if (!Client.Game.UO.World.InGame)
        {
            GD.PrintErr("[GUO] shard command: never got into the world.");

            return;
        }

        bool all = true;

        foreach (string command in commands)
        {
            GD.Print($"[GUO] shard command: {command}");

            int before = Game.Managers.JournalManager.Entries.Count;

            await InputProbe.Say(host, command);

            bool answered = await WaitForReply(host, before, command);

            all &= answered;

            if (!answered)
            {
                GD.PrintErr($"[GUO] shard command: no reply to \"{command}\".");
            }
        }

        Passed = all;
    }

    /// <summary>
    /// Wait for the server to say something back, then for it to stop.
    /// </summary>
    /// <remarks>
    /// World generation is not quick -- it reads thousands of spawn
    /// definitions and places them -- and it reports as it goes, so the end of
    /// it is a silence rather than a particular line. A fixed wait would
    /// either cut a long command off or make a short one crawl.
    /// </remarks>
    private static async System.Threading.Tasks.Task<bool> WaitForReply(
        Node host,
        int before,
        string command
    )
    {
        const int Poll = 60;
        const int QuietPolls = 10;
        const int PollsAfterReply = 120;
        const int GivenUpAfter = 60 * 15;
        // A command that never answers at all (ServUO's [go, for one) is given
        // up on after this many polls; one that answers keeps the long wait.
        const int SilentGivenUpAfter = 30;

        int last = before;
        int quiet = 0;
        int since = 0;
        bool replied = false;

        for (int i = 0; i < GivenUpAfter; i++)
        {
            await InputProbe.Wait(host, Poll);

            var entries = Game.Managers.JournalManager.Entries;

            if (replied && ++since >= PollsAfterReply)
            {
                // A populated town is never quiet -- vendors call out, animals
                // make noise, and the journal takes all of it. Waiting for
                // silence there waits forever, so the wait after an answer is
                // bounded whether or not the journal settles.
                return true;
            }

            if (entries.Count == last)
            {
                if (replied && ++quiet >= QuietPolls)
                {
                    return true;
                }

                if (!replied && i >= SilentGivenUpAfter)
                {
                    return false;
                }

                continue;
            }

            quiet = 0;

            for (int e = last; e < entries.Count; e++)
            {
                GD.Print($"[GUO] shard says: {entries[e].Text}");

                // The client prints what was typed as soon as it is sent, and
                // a command the server does not run comes back as ordinary
                // speech as well. Neither is an answer: an unprivileged
                // account gets exactly that and nothing else, which is how
                // this quietly "passed" while generating nothing.
                replied |= entries[e].Text != command;
            }

            last = entries.Count;
        }

        return replied;
    }
}
