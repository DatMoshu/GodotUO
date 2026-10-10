// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Network
{
    // GUO addition (no upstream counterpart), used by PacketHandlers.ParsePackets.
    // A dynamic packet carries its own length after the id: id, hi, lo. A length
    // under 3 cannot even cover that header. Upstream dequeues `length` bytes and
    // moves on: with 0 nothing is dequeued and ParsePackets spins forever on the
    // same bytes; with 1 or 2 the handler is pointed past the end of its data.
    // Kept in its own file so the check can be tested without the engine.
    internal static class PacketLengthGuard
    {
        public const int DynamicHeaderLength = 3;

        // True when the packet at the head of the stream declares a dynamic
        // length too short for its own header. Its header bytes are then
        // consumed, so the parse loop moves on to whatever follows.
        public static bool RejectShortDynamic(CircularBuffer stream, int packetOffset, int packetLength)
        {
            if (packetOffset != DynamicHeaderLength || packetLength >= DynamicHeaderLength)
            {
                return false;
            }

            System.Span<byte> header = stackalloc byte[DynamicHeaderLength];
            stream.Dequeue(header, 0, DynamicHeaderLength);

            return true;
        }
    }
}
