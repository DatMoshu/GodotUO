// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace GUO.Network
{
    /// <summary>
    /// A stand-in for upstream's <c>Network/Plugin.cs</c>, so the client
    /// compiles and runs without plugin support.
    /// </summary>
    /// <remarks>
    /// PORT GAP (GUO). This is NOT a port of Plugin.cs, and the file is named
    /// so that the audit does not count it as one: the audit matches upstream
    /// filenames, and <c>src/Network/Plugin.cs</c> is deliberately absent.
    ///
    /// Two separate things block the real port.
    ///
    /// The first is that it cannot be compiled. Plugin.cs opens with
    /// <c>using CUO_API;</c> -- the plugin ABI, which declares the delegates
    /// and the structs a plugin DLL is handed. That lives in
    /// <c>external/cuoapi</c>, which is empty in the upstream checkout under
    /// <c>sources/</c> and is not one of the declared submodules. Writing
    /// those declarations from the call sites would produce an ABI that looks
    /// right and is not, and every existing plugin would corrupt memory
    /// against it. Guessing an ABI is worse than not having one.
    ///
    /// The second is that half of Plugin.cs is a renderer. ProcessDrawCmdList
    /// and HandleCmdList are ~700 lines interpreting a command stream from the
    /// plugin -- vertex and index buffers, BasicEffect, render targets, blend,
    /// depth and rasterizer state -- straight onto a GraphicsDevice. That is
    /// rewrite tier against ADR-0002, and a large one.
    ///
    /// So: every method here does what the client does when no plugin is
    /// loaded. ProcessRecvPacket and ProcessSendPacket return true, which
    /// upstream means as "nothing swallowed this packet, carry on"; the rest
    /// do nothing. Nothing here silently drops work a plugin would have done,
    /// because no plugin can be loaded in the first place -- Create says so
    /// out loud, once per plugin listed in the settings.
    ///
    /// To lift this gap: fetch the cuoapi sources, port Plugin.cs properly,
    /// and delete this file.
    /// </remarks>
    internal static class Plugin
    {
        private static bool _warned;

        public static void Create(string path)
        {
            if (!_warned)
            {
                _warned = true;

                Utility.Logging.Log.Warn(
                    "plugins are not supported in this build; see src/Network/PluginGap.cs"
                );
            }

            Utility.Logging.Log.Warn($"ignoring plugin: {path}");
        }

        /// <summary>
        /// True means "no plugin took this key, the client should handle it".
        /// With no plugins there is nothing to take one.
        /// </summary>
        internal static bool ProcessHotkeys(int key, int mod, bool pressed) => true;

        internal static void ProcessMouse(int button, int wheel)
        {
        }

        internal static void OnFocusGained()
        {
        }

        internal static void OnFocusLost()
        {
        }

        internal static bool RequestMove(int dir, bool run) => false;

        internal static bool GetPlayerPosition(out int x, out int y, out int z)
        {
            x = y = z = 0;

            return false;
        }

        internal static void Tick()
        {
        }

        /// <summary>
        /// Upstream returns false when a plugin has swallowed the packet. With
        /// no plugins, nothing swallows anything.
        /// </summary>
        internal static bool ProcessRecvPacket(byte[] data, ref int length) => true;

        /// <inheritdoc cref="ProcessRecvPacket"/>
        internal static bool ProcessSendPacket(ref Span<byte> message) => true;

        internal static void OnClosing()
        {
        }

        internal static void OnConnected()
        {
        }

        internal static void OnDisconnected()
        {
        }

        internal static void ProcessDrawCmdList()
        {
        }

        internal static void UpdatePlayerPosition(int x, int y, int z)
        {
        }

        internal static bool OnPluginRecv(ref byte[] data, ref int length) => true;

        internal static bool OnPluginSend(ref byte[] data, ref int length) => true;

        internal static bool OnPluginRecv_new(IntPtr buffer, ref int length) => true;

        internal static bool OnPluginSend_new(IntPtr buffer, ref int length) => true;
    }
}
