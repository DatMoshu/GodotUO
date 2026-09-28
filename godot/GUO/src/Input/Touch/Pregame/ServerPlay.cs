// GUO addition, not a port: upstream ClassicUO plays on the one address it
// was started with.

using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps.Login;
using GUO.Network;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// Play on a server from the pre-game card (docs/ui/second_screen_pregame.md,
/// "Play: what happens when you tap it"): the address is swapped in memory
/// and in settings.json, and the client goes back to its login step with it.
/// A server that needs other client files, or allows only its own client,
/// says so instead.
/// </summary>
internal static class ServerPlay
{
    public enum Verdict { Ready, NotAllowed, NeedsOwnData }

    /// <summary>Whether this client can play on <paramref name="e"/> as it runs now, and why not.</summary>
    public static Verdict Check(ServerEntry e, out string reason)
    {
        reason = null;

        if (!e.ThirdPartyClients)
        {
            reason = "This shard only allows its own client.";
            return Verdict.NotAllowed;
        }

        Settings s = Settings.GlobalSettings;
        bool version = !string.IsNullOrWhiteSpace(e.ClientVersion) && !string.IsNullOrWhiteSpace(s.ClientVersion)
            && e.ClientVersion.Trim() != s.ClientVersion.Trim();
        bool encryption = e.Encryption != null && e.Encryption.Value != s.Encryption;

        if (e.NeedsCustomData || version || encryption)
        {
            reason = $"{e.Name} needs its own client files"
                + (version ? $" (client {e.ClientVersion}; GUO runs {s.ClientVersion})" : encryption ? " (another encryption)" : "")
                + ". Set GUO up with them, then restart it.";
            return Verdict.NeedsOwnData;
        }

        return Verdict.Ready;
    }

    /// <summary>Whether Play has to log out first.</summary>
    public static bool InWorld => Client.Game?.UO?.World?.InGame ?? false;

    /// <summary>What happened, for the card's status line and the probe.</summary>
    public static string LastOutcome { get; private set; } = "";

    /// <summary>
    /// Play on <paramref name="e"/>: the address goes into the settings; from
    /// the world the player is logged out first (the card has asked); at the
    /// login steps the client goes back to its first one, and when the login
    /// gump holds an account name it logs in as its own arrow would.
    /// </summary>
    public static void Play(ServerEntry e)
    {
        if (Check(e, out string reason) != Verdict.Ready)
        {
            LastOutcome = reason;
            return;
        }

        Settings s = Settings.GlobalSettings;
        bool same = e.Same(s.IP, s.Port);
        s.IP = e.Host.Trim();
        s.Port = (ushort) e.Port;
        s.Save();
        GD.Print($"[GUO] servers: play on \"{e.Name}\"{(same ? " (the server already in use)" : "")}");

        Game.World world = Client.Game.UO.World;

        if (InWorld)
        {
            // As the paperdoll's log out does after its question, without
            // asking again: the card asked.
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

            LastOutcome = $"Logged out. Log in to {e.Name} on the top screen.";
            return;
        }

        LoginScene login = Client.Game.GetScene<LoginScene>();

        if (login == null)
        {
            LastOutcome = $"{e.Name} is set; it is used at the next login.";
            return;
        }

        if (login.CurrentLoginStep != LoginSteps.Main)
        {
            // Mid-login on another server: start the login over, on this one.
            NetClient.Socket.Disconnect();
            Client.Game.SetScene(new LoginScene(world));
            LastOutcome = $"Log in to {e.Name} on the top screen.";
            return;
        }

        LoginGump gump = UIManager.GetGump<LoginGump>();
        string account = gump == null ? null : All(gump).OfType<Game.UI.Controls.StbTextBox>().FirstOrDefault()?.Text;

        if (gump != null && !string.IsNullOrWhiteSpace(account))
        {
            gump.OnButtonClick(0); // the login gump's own arrow (Buttons.NextArrow)
            LastOutcome = $"Logging in to {e.Name}.";
            return;
        }

        LastOutcome = $"Type your account and password on the top screen to log in to {e.Name}.";
    }

    private static System.Collections.Generic.IEnumerable<Game.UI.Controls.Control> All(Game.UI.Controls.Control c) =>
        c.Children.SelectMany(x => new[] { x }.Concat(All(x)));
}
