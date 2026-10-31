// SPDX-License-Identifier: BSD-2-Clause
// Only unrelated logging and optional mods are stubbed; packet calibration is real production code.
namespace GUO.Utility.Logging
{
    internal static class Log { internal static void Trace(string message) { } }
}
namespace GUO.Network.Mods
{
    internal sealed class PacketModRegistry
    {
        internal static PacketModRegistry Current => null;
        internal bool TryGetLength(byte id, out short length) { length = 0; return false; }
    }
}
