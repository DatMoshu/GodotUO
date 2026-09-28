// GUO addition, not a port: a dev build's one-click logins to the dev shard.

using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps.Login;
using GUO.Network;

namespace GUO.Input.Touch.Pregame.Accounts;

/// <summary>
/// One click logs in to the dev shard as one of the player's dev accounts:
/// the address, the account and its password (from the keystore, never
/// typed again), the shard's one server, and the account's character when
/// it has one (the character list when it has several). From the world the
/// same click logs out first, so switching between an admin and a player
/// account is one action.
/// </summary>
/// <remarks>
/// A dev build only, gated like the dev shard's favourite
/// (<see cref="ServerBook.SetDevShard"/>): a release build has no dev shard,
/// so it has no dev logins. The accounts are the player's own, added once
/// on the dev shard's page with "Dev account" ticked; nothing here names or
/// makes an account.
/// </remarks>
internal static class DevLogin
{
    private enum Phase { Idle, LoggingOut, AtLogin, Connecting }

    private const ulong GiveUpMs = 60000;

    private static Phase _phase;
    private static SavedAccount _target;
    private static ulong _since;

    /// <summary>Whether this build has dev logins (a dev build with a dev shard).</summary>
    public static bool Available => ServerBook.DevEntry != null;

    /// <summary>The dev accounts with a saved password, the last used first.</summary>
    public static IReadOnlyList<SavedAccount> Accounts =>
        Available ? AccountBook.For(ServerBook.DevEntry).Where(a => a.Dev && a.HasPassword).ToList() : new List<SavedAccount>();

    /// <summary>What happened last, for the row and the probe.</summary>
    public static string Status { get; private set; } = "";

    public static bool Busy => _phase != Phase.Idle;

    /// <summary>The account being logged in as, while busy.</summary>
    public static SavedAccount Target => _target;

    /// <summary>The dev account in use now (the world's, or the last one logged in as), if any.</summary>
    public static SavedAccount InUse
    {
        get
        {
            string name = LoginScene.Account;

            if (string.IsNullOrEmpty(name))
            {
                name = _lastName;
            }

            return Accounts.FirstOrDefault(a => a.Name == name);
        }
    }

    private static string _lastName;
    private static string _where;

    /// <summary>Logs in as <paramref name="a"/>, from the login screen or from the world.</summary>
    public static void Start(SavedAccount a)
    {
        if (!Available || a == null)
        {
            return;
        }

        _target = a;
        _since = Godot.Time.GetTicksMsec();
        _phase = ServerPlay.InWorld ? Phase.LoggingOut : Phase.AtLogin;
        Status = ServerPlay.InWorld ? $"Logging out, then in as {a.Name}." : $"Logging in as {a.Name}.";
        GD.Print($"[GUO] dev login: {(ServerPlay.InWorld ? "switching" : "logging in")} to a dev account");

        if (_phase == Phase.LoggingOut)
        {
            LogOut();
        }
    }

    /// <summary>The next dev account after the one in use: the switch.</summary>
    public static void SwitchToNext()
    {
        IReadOnlyList<SavedAccount> all = Accounts.OrderBy(a => a.Name, System.StringComparer.OrdinalIgnoreCase).ToList();

        if (all.Count == 0)
        {
            Status = "No dev accounts saved. Add one on the dev shard's page with Dev account ticked.";
            return;
        }

        SavedAccount now = InUse;
        int i = now == null ? -1 : all.ToList().IndexOf(now);
        Start(all[(i + 1) % all.Count]);
    }

    /// <summary>Once a frame (the pre-game card): the login, a step at a time.</summary>
    public static void Update()
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        if (Godot.Time.GetTicksMsec() - _since > GiveUpMs)
        {
            Stop($"The login as {_target.Name} didn't finish in a minute. Is the dev shard running?");
            return;
        }

        LoginScene login = Client.Game?.GetScene<LoginScene>();
        string where = $"{_phase} {Client.Game?.Scene?.GetType().Name} {login?.CurrentLoginStep}";

        if (where != _where)
        {
            _where = where;
            GD.Print($"[GUO] dev login: {where}");
        }

        switch (_phase)
        {
            case Phase.LoggingOut:
                if (login != null)
                {
                    _phase = Phase.AtLogin;
                }

                break;

            case Phase.AtLogin:
                if (login == null || login.CurrentLoginStep != LoginSteps.Main || UIManager.GetGump<LoginGump>() == null)
                {
                    break;
                }

                ServerPlay.Play(ServerBook.DevEntry, _target);

                if (login.CurrentLoginStep == LoginSteps.Main && !ServerPlay.LastOutcome.StartsWith("Logging in"))
                {
                    Stop(ServerPlay.LastOutcome);
                    return;
                }

                _lastName = _target.Name;
                _phase = Phase.Connecting;
                break;

            case Phase.Connecting:
                if (Client.Game?.UO?.World?.InGame ?? false)
                {
                    Stop($"In the world as {_target.Name}.");
                    return;
                }

                if (login == null)
                {
                    break;
                }

                switch (login.CurrentLoginStep)
                {
                    // The dev shard lists one server; the first is it.
                    case LoginSteps.ServerSelection when login.Servers is { Length: > 0 }:
                        login.SelectServer((byte) login.Servers[0].Index);
                        break;

                    case LoginSteps.CharacterSelection:
                    {
                        var slots = Enumerable.Range(0, login.Characters?.Length ?? 0).Where(i => !string.IsNullOrEmpty(login.Characters[i])).ToList();

                        if (slots.Count == 1)
                        {
                            login.SelectCharacter((uint) slots[0]);
                        }
                        else
                        {
                            Stop(slots.Count == 0 ? $"{_target.Name} has no character yet: make one." : $"{_target.Name} has {slots.Count} characters: choose one.");
                            return;
                        }

                        break;
                    }

                    // No character: upstream goes straight to making one.
                    case LoginSteps.CharacterCreation:
                        Stop($"{_target.Name} has no character yet: make one.");
                        return;

                    case LoginSteps.PopUpMessage:
                        Stop($"The shard refused the login as {_target.Name}. Check the password on the dev shard's page.");
                        return;
                }

                break;
        }
    }

    private static void Stop(string status)
    {
        Status = status;
        _phase = Phase.Idle;
        GD.Print($"[GUO] dev login: {status.Replace(_target?.Name ?? "\u0000", "the dev account")}");
        _target = null;
    }

    /// <summary>As the card's "Log out and play" does, without asking: the click was the question.</summary>
    private static void LogOut()
    {
        Game.World world = Client.Game.UO.World;
        GameScene game = Client.Game.GetScene<GameScene>();

        if (game != null && (world.ClientFeatures.Flags & Game.Data.CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON) != 0)
        {
            game.DisconnectionRequested = true;
            NetClient.Socket.Send_LogoutNotification();
        }
        else
        {
            NetClient.Socket.Disconnect();
            Client.Game.SetScene(new LoginScene(world));
        }
    }
}
