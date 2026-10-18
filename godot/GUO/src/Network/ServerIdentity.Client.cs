// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Input.Touch.Pregame;
using GUO.Utility;

namespace GUO.Network
{
    // GUO addition: the client side of ServerIdentity (docs/data_formats.md section 41).
    // Each method is called by one line marked PORT DEVIATION (GUO) in the ported
    // login and packet code, and only records what that code already received.
    internal sealed partial class ServerIdentity
    {
        // Where the configured client version came from (section 41), set by the
        // bootstrap before the client starts: default, env, cli or shard_session.
        internal static string VersionSource { get; set; } = "default";

        // LoginScene.Connect: a new login connection; the earlier record is replaced.
        internal static void Connecting(string host, int port)
        {
            bool configured = ClientVersionHelper.IsClientVersionValid(Settings.GlobalSettings.ClientVersion, out _);

            Current = new ServerIdentity
            {
                Host = host ?? "",
                Port = port,
                ClientVersion = Format((uint) Client.Game.UO.Version),
                ClientVersionSource = configured ? VersionSource : "client_exe",
                Backend = BackendOf(host, port),
            };
        }

        // LoginScene.ServerListReceived (0xA8).
        internal static void ServerListReceived()
        {
            if (Current != null)
            {
                Current.ReachedServerList = true;
            }
        }

        // LoginScene.SelectServer: the game server's name from the list.
        internal static void ServerChosen(string name)
        {
            if (Current != null)
            {
                Current.ShardName = name;
            }
        }

        // PacketHandlers.EnableLockedFeatures (0xB9).
        internal static void LockedFeaturesReceived(uint flags)
        {
            if (Current != null)
            {
                Current.LockedFeatures = flags;
            }
        }

        // LoginScene.ReceiveCharacterList (0xA9).
        internal static void CharacterListReceived(uint flags)
        {
            if (Current != null)
            {
                Current.CharacterListFlags = flags;
                Godot.GD.Print(Current.Describe());
            }
        }

        // LoginScene and GameScene disconnects: the message shown, with the version
        // sentence when the drop looks like a version refusal.
        internal static string OnDisconnect(string message)
        {
            string refusal = RefusalLine(NewestJournalLines());
            string shown = ExplainDisconnect(message, Current, refusal);

            if (shown != message)
            {
                Godot.GD.Print($"[GUO] server identity: disconnect explained ({(refusal != null ? "version refused" : "closed during login")}), "
                    + $"client {Current?.ClientVersion}");
            }

            return shown;
        }

        private static IEnumerable<string> NewestJournalLines()
        {
            var entries = JournalManager.Entries;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                yield return entries[i]?.Text;
            }
        }

        // The server software from the player's entry or the community catalogue, when known.
        private static string BackendOf(string host, int port)
        {
            ServerEntry entry = ServerBook.Find(host, port) ?? ServerCatalogue.Find(host, port);

            return string.IsNullOrWhiteSpace(entry?.Emulator) ? null : entry.Emulator.Trim();
        }
    }
}
