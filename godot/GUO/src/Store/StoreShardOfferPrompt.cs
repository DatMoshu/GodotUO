// SPDX-License-Identifier: BSD-2-Clause
// A shard's pack offer met in game (ADR-0026 section 4a): ask, and on yes install, verify and restart into a shard
// session, as the Servers screen does for a server entry's descriptor. Nothing is fetched before yes.
using System;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Game.UI.Controls;
using GUO.Host;
using GUO.Input.Touch.Pregame;

namespace GUO.Store;

internal static class StoreShardOfferPrompt
{
    public const string AnswerFlag = "--shard-offer-answer";

    /// <summary>A scripted run's answer (yes or no) in place of the question; null asks the player.</summary>
    public static bool? Scripted { get; set; }

    private static bool _busy;
    private static OfferGump _prompt;

    private static void Log(string text) => GD.Print("[GUO] shard offer: " + text);

    /// <summary>From the 0xBF handler: the bytes after the subcommand. Never throws; a bad offer is one log line.</summary>
    public static void Receive(World world, byte[] payload)
    {
        StoreShardOffer offer;
        try { offer = StoreShardOffer.Decode(payload); }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or System.Text.Json.JsonException or InvalidOperationException
                                       or System.Collections.Generic.KeyNotFoundException or FormatException or ArgumentException)
        {
            Log("ignored: " + ex.Message);
            return;
        }

        if (world?.InGame != true) return;
        offer.OfferFeatures(Log);
        Settings s = Settings.GlobalSettings;
        string server = StoreShardOffer.ServerKey(s.IP, s.Port, world?.ServerName);
        string mounted = ShardSession.Active && string.Equals(ShardSession.Current.Host, s.IP?.Trim(), StringComparison.OrdinalIgnoreCase)
            && ShardSession.Current.Port == s.Port ? ShardSession.Current.ContentIdentity : null;
        var answers = new StoreShardOfferAnswers(StoreOptions.Root);
        var decision = offer.Decide(server, mounted, answers);
        Log($"{offer.ShardName}: {decision}" + (offer.Content != null ? " " + offer.Content.IdentityHash[..16] : "") + $", {offer.Features.Count} feature block(s)");
        if (decision != StoreShardOffer.Decision.Ask || _busy || _prompt?.IsDisposed == false) return;
        string host = s.IP;
        ushort port = s.Port;
        bool StillHere() => world.InGame && string.Equals(s.IP, host, StringComparison.OrdinalIgnoreCase) && s.Port == port;

        void Answer(bool yes)
        {
            if (!StillHere()) return;
            if (!yes)
            {
                try { answers.Decline(server, offer.Content.IdentityHash); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    Log("could not remember decline: " + ex.Message);
                }
                GameActions.Print(world, "Not installed. You won't be asked again until this shard's packs change.");
                Log("declined");
                return;
            }
            _ = Install(world, offer, host, port);
        }

        if (Scripted is bool scripted)
        {
            Log("scripted answer: " + (scripted ? "yes" : "no"));
            Answer(scripted);
            return;
        }
        UIManager.Add(_prompt = new OfferGump(world, offer.Question(), Answer));
    }

    private static async Task Install(World world, StoreShardOffer offer, string host, ushort port)
    {
        if (_busy) return;
        _busy = true;
        void Say(string text) => Callable.From(() => { GameActions.Print(world, text); Log(text); }).CallDeferred();
        try
        {
            Say($"Installing {offer.ShardName}'s packs…");
            string lockPath = await offer.Content.Prepare(StoreOptions.Root, StoreOptions.Trust, PlatformDefaults.CurrentVersion, Say).ConfigureAwait(false);
            Callable.From(() =>
            {
                try
                {
                    var settings = Settings.GlobalSettings;
                    if (!world.InGame || !string.Equals(settings.IP, host, StringComparison.OrdinalIgnoreCase) || settings.Port != port)
                    {
                        Log("installed; restart skipped because the connection changed");
                        return;
                    }
                    var entry = ServerBook.Find(host, port) ?? new ServerEntry { Name = offer.ShardName, Host = host, Port = port };
                    ShardSession.Start(entry, offer.Content, lockPath);
                }
                catch (Exception ex) { Log("could not restart: " + ex.Message); }
                finally { _busy = false; }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            Say($"Couldn't install {offer.ShardName}'s packs: {ex.Message}");
            _busy = false;
        }
    }

    // The upstream question gump has a fixed two-line body. Offers can name many packs and keys,
    // so keep every line readable in a scroll area with explicit consent buttons.
    private sealed class OfferGump : Gump
    {
        private readonly Action<bool> _answer;

        public OfferGump(World world, string question, Action<bool> answer) : base(world, 0, 0)
        {
            _answer = answer;
            Width = Math.Min(560, Client.Game.ClientBounds.Width);
            Height = Math.Min(400, Client.Game.ClientBounds.Height);
            IsModal = true;
            CanMove = true;
            CanCloseWithEsc = CanCloseWithRightClick = false;
            WantUpdateSize = false;
            Add(new ResizePic(0x0A28) { Width = Width, Height = Height });
            var scroll = new ScrollArea(24, 24, Width - 48, Height - 90, true);
            scroll.Add(new GUO.Game.UI.Controls.Label(question, true, 0x0386, Width - 72, 1));
            Add(scroll);
            Add(new NiceButton(24, Height - 54, (Width - 56) / 2, 36, ButtonAction.Activate, "Not now")
                { ButtonParameter = 0, IsSelectable = false });
            Add(new NiceButton(Width / 2 + 4, Height - 54, (Width - 56) / 2, 36, ButtonAction.Activate, "Install and restart")
                { ButtonParameter = 1, IsSelectable = false });
            X = (Client.Game.ClientBounds.Width - Width) / 2;
            Y = (Client.Game.ClientBounds.Height - Height) / 2;
        }

        public override void OnButtonClick(int buttonID)
        {
            if (IsDisposed || buttonID is not (0 or 1)) return;
            Dispose();
            _answer(buttonID == 1);
        }
    }
}
