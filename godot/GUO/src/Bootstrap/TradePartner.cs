// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// A second client, logged in as somebody else, for the probe to trade with.
/// </summary>
/// <remarks>
/// Trade is the one thing a lone client cannot exercise: the secure trade
/// window is opened by the server for two players at once, and the shard will
/// not open one with a shopkeeper. So the probe starts a second copy of the
/// client in this mode, which logs in on its own account, stands where it
/// arrives, and accepts whatever is offered to it.
///
/// It accepts by setting the trade window's own flag and sending the same
/// response the checkbox sends, rather than by clicking: this side is the
/// other player, not the thing under test, and a second window of synthesised
/// input to aim at buys nothing.
/// </remarks>
internal static class TradePartner
{
    /// <summary>
    /// The account the second client logs in with. Auto account creation
    /// makes it on first use; like the probe's own, it is a name on a local
    /// dev shard and not a credential.
    /// </summary>
    private const string PartnerAccount = "guomate";

    private const string PartnerPassword = "guomate";

    public const string PartnerCharacter = "Guomate";

    /// <summary>
    /// How long to stand there. The probe kills this process when it is done
    /// with it; this is only so a probe that dies first does not leave a
    /// client logged in forever.
    /// </summary>
    private const int Patience = 360;

    /// <summary>
    /// Where the second client leaves the tile it arrived on, for the probe
    /// to read.
    /// </summary>
    /// <remarks>
    /// Two characters made on the same shard do not start on the same tile --
    /// these two came up forty tiles apart, which is further than either can
    /// see -- and neither client can be told anything by the other except
    /// through the server, which neither is close enough to be heard by. So
    /// the second one writes down where it is, and the probe, which owns the
    /// shard, walks the short way: "[go x y z".
    /// </remarks>
    public const string WhereIAm = "user://trade_partner.txt";

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        await InputProbe.EnterTheWorld(
            host,
            200,
            PartnerAccount,
            PartnerPassword,
            PartnerCharacter
        );

        if (!Client.Game.UO.World.InGame)
        {
            GD.PrintErr("[GUO] trade partner: never got into the world.");

            return;
        }

        GD.Print(
            $"[GUO] trade partner: {Client.Game.UO.World.Player.Name} is standing at "
            + $"{Client.Game.UO.World.Player.X},{Client.Game.UO.World.Player.Y}"
        );

        using (FileAccess note = FileAccess.Open(WhereIAm, FileAccess.ModeFlags.Write))
        {
            note?.StoreLine(
                $"{Client.Game.UO.World.Player.X} {Client.Game.UO.World.Player.Y} "
                    + $"{Client.Game.UO.World.Player.Z}"
            );
        }

        for (int tick = 0; tick < Patience; tick++)
        {
            await InputProbe.Wait(host, 30);

            Game.UI.Gumps.TradingGump trade =
                Game.Managers.UIManager.GetGump<Game.UI.Gumps.TradingGump>();

            if (trade == null || trade.ImAccepting)
            {
                continue;
            }

            GD.Print("[GUO] trade partner: something was offered; accepting it");

            trade.ImAccepting = true;

            Game.GameActions.AcceptTrade(trade.ID1, true);
        }

        GD.Print("[GUO] trade partner: nothing more to wait for");
    }
}
