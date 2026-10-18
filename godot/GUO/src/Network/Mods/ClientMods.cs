// SPDX-License-Identifier: BSD-2-Clause
// The client's side of packet mods (ADR-0038): loads the installed mods at
// start when "Load mods" is on, and gives each one its IModContext.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GUO.Configuration;
using GUO.Game;
using GUO.ModApi;
using GUO.Utility;
using GUO.Utility.Logging;

namespace GUO.Network.Mods
{
    internal static class ClientMods
    {
        /// <summary>A mod is a managed assembly loaded from a file; Android and web builds do not
        /// load assemblies from the user's files, so the option is not offered there.</summary>
        public static bool Supported => !OperatingSystem.IsAndroid() && !OperatingSystem.IsBrowser() && !OperatingSystem.IsIOS();

        /// <summary>Where installed mods live: <c>Data/Mods/&lt;mod&gt;/mod.json</c>, beside
        /// <c>Data/Plugins</c>.</summary>
        public static string Folder => Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Mods");

        public static IReadOnlyList<ModLoadResult> Results { get; private set; } = Array.Empty<ModLoadResult>();

        /// <summary>Set by ModsProbe only: system messages go here instead of the world.</summary>
        internal static List<(string Text, ushort Hue)> ProbeMessages;

        /// <summary>One line for Options: what loaded on this start.</summary>
        public static string Status
        {
            get
            {
                if (!Supported)
                    return "Mods are not available on this device.";
                if (!Settings.GlobalSettings.LoadMods && PacketModRegistry.Current == null)
                    return "Off. Turn on and restart to load mods from Data/Mods.";
                if (Results.Count == 0)
                    return "No mods installed in Data/Mods.";
                int loaded = Results.Count(r => r.Loaded);
                int refused = Results.Count - loaded;
                return refused == 0 ? $"{loaded} mod(s) loaded." : $"{loaded} mod(s) loaded, {refused} refused (the log says why).";
            }
        }

        /// <summary>Called once at start, after the plugins load. With the option off this sets
        /// nothing, and every seam sees a null registry.</summary>
        public static void Load(ClientVersion version) => Load(version, Folder, Settings.GlobalSettings.LoadMods);

        internal static void Load(ClientVersion version, string folder, bool enabled)
        {
            PacketModRegistry.Current = null;
            Results = Array.Empty<ModLoadResult>();

            if (!enabled || !Supported)
                return;

            var registry = new PacketModRegistry((uint)version, PacketHandlers.Handler.HasHandler, Log.Error)
            {
                ShardHost = Shard.Host,
                ShardName = Shard.Name
            };
            Results = ModLoader.LoadAll(folder, registry, m => new Context(m.Id, (uint)version));

            foreach (ModLoadResult r in Results)
            {
                if (r.Loaded)
                    Log.Info("[mods] loaded " + r.Message);
                else
                    Log.Error("[mods] refused " + r.Message);
            }

            if (registry.Mods.Count > 0)
                PacketModRegistry.Current = registry;
        }

        /// <summary>Which shard the client is on, for the mods' <c>shards</c> guard. Today the login
        /// address the user chose and the server name picked from its list; NET-02's
        /// ServerIdentity.Current (Host, ShardName) replaces both here when it lands, and only here.</summary>
        private static class Shard
        {
            public static string Host() => Settings.GlobalSettings.IP;
            public static string Name() => Settings.GlobalSettings.LastServerName;
        }

        private sealed class Context : IModContext
        {
            private readonly string _id;

            public Context(string id, uint clientVersion)
            {
                _id = id;
                ClientVersion = clientVersion;
            }

            public uint ClientVersion { get; }

            public void SystemMessage(string text, ushort hue = 946)
            {
                if (ProbeMessages != null)
                {
                    ProbeMessages.Add((text, hue));
                    return;
                }

                World world = Client.Game?.UO?.World;
                if (world != null && text != null)
                    GameActions.Print(world, text, hue, GUO.Game.Data.MessageType.System);
            }

            public void Send(ReadOnlySpan<byte> packet)
            {
                if (!packet.IsEmpty)
                    NetClient.Socket.Send(packet.ToArray());
            }

            public void Log(string message) => Utility.Logging.Log.Info($"[mods] {_id}: {message}");
        }
    }
}
