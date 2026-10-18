// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GUO.Network
{
    // GUO addition (no upstream counterpart), docs/data_formats.md section 41.
    // What the client learned about the server it logged in to, and the client
    // version it reported there. Filled by marked lines in the ported login and
    // packet code (ServerIdentity.Client.cs); this part has no engine or client
    // references, so tools/network_tests can check it without the engine.
    // Host is the player's address: never write it to a log or evidence.
    internal sealed partial class ServerIdentity
    {
        // Null until a login connects; replaced at each new login connection and
        // kept after a disconnect, so the failure text can use it.
        public static ServerIdentity Current { get; internal set; }

        public string Host { get; init; } = "";
        public int Port { get; init; }
        public string ShardName { get; internal set; }
        public string ClientVersion { get; init; } = "";
        public string ClientVersionSource { get; init; } = "default";
        public uint? LockedFeatures { get; internal set; }
        public uint? CharacterListFlags { get; internal set; }
        public string Backend { get; init; }

        // True once the login server sent its server list (0xA8): a drop before it
        // is a refusal of the login itself, often of the client version or encryption.
        public bool ReachedServerList { get; internal set; }

        // ModernUO's ClientVerification ("This server doesn't support clients older
        // than X."), RunUO's and ServUO's ("This server requires your client version
        // be at least X."), and the same said of newer clients.
        private static readonly Regex Refusal = new(
            @"(doesn'?t|does not) support clients (older|newer) than|requires your client version",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // How many of the newest journal lines are searched for the server's refusal.
        public const int RefusalLinesSearched = 20;

        // Dotted form of upstream's packed ClientVersion value (major.minor.build.extra).
        public static string Format(uint packed) =>
            $"{(packed >> 24) & 0xFF}.{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

        // One log line for runs and scenarios; never the address (Host, Port).
        public string Describe() =>
            $"[GUO] server identity: \"{ShardName ?? "?"}\", client {ClientVersion} ({ClientVersionSource}), backend {Backend ?? "unknown"}, "
            + $"features 0x{LockedFeatures ?? 0:X8}{(LockedFeatures == null ? " (not sent)" : "")}, "
            + $"character list 0x{CharacterListFlags ?? 0:X8}{(CharacterListFlags == null ? " (not sent)" : "")}";

        // The newest line saying the client version is refused, searched newest first, or null.
        public static string RefusalLine(IEnumerable<string> newestFirst)
        {
            int seen = 0;

            foreach (string line in newestFirst)
            {
                if (++seen > RefusalLinesSearched)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line) && Refusal.IsMatch(line))
                {
                    return line.Trim();
                }
            }

            return null;
        }

        // The disconnect message with a plain sentence added when the drop looks like a
        // version refusal: the server said so, or it closed the connection during the
        // login before any server list. Otherwise the message is returned unchanged.
        public static string ExplainDisconnect(string message, ServerIdentity id, string refusal)
        {
            if (id == null)
            {
                return message;
            }

            string version = string.IsNullOrEmpty(id.ClientVersion) ? "an unknown version" : id.ClientVersion;

            if (refusal != null)
            {
                return message + $"\n\nThe server refused this client version. It said: \"{refusal}\" GUO reported itself as "
                    + $"client {version}. Play a shard that needs another version with its own client files (Servers tab).";
            }

            if (!id.ReachedServerList)
            {
                return message + $"\n\nThe server closed the connection during the login. GUO reported itself as client "
                    + $"{version}. If this server needs another client version or encryption, ask its staff "
                    + "which one, and play it with its own client files (Servers tab).";
            }

            return message;
        }
    }
}
