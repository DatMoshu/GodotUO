// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.CompilerServices;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// One unsigned 64-bit key per queued draw: the depth upstream would have
    /// written into its depth buffer, then the order the draw was queued in,
    /// so sorting the keys as plain integers gives exactly the order a
    /// depth-then-sequence comparison gives.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): the sorted world pass (RenderLists.DrawWorld)
    /// used List.Sort with a comparison delegate: an indirect call per
    /// comparison, about n log n of them, over a 24-byte struct that was
    /// moved around while sorting. Sorting an array of ulong instead is a
    /// primitive integer ordering with 8-byte moves, and the low half
    /// of each key is the index of the draw it stands for, so no second array
    /// is needed to find it again.
    ///
    /// The float is mapped to an unsigned integer that orders the same way
    /// (sign bit flipped for positives, every bit flipped for negatives), with
    /// negative zero folded into positive zero so that the two stay equal, as
    /// float.CompareTo says they are. NaN never comes from CalculateDepthZ;
    /// if encountered, its transformed sign/payload bits retain their unsigned
    /// order, which is not float.CompareTo's NaN ordering.
    /// </remarks>
    internal static class DepthKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Of(float depth, int seq)
        {
            if (depth == 0f)
            {
                depth = 0f; // -0 and +0 compare equal; give them one key.
            }

            uint bits = BitConverter.SingleToUInt32Bits(depth);
            bits = (bits & 0x8000_0000u) != 0 ? ~bits : bits | 0x8000_0000u;

            return ((ulong)bits << 32) | (uint)seq;
        }

        /// <summary>The queue index the key was made with.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Seq(ulong key) => (int)(uint)key;

        /// <summary>The order List.Sort(ByDepth) gave: depth first, sequence second.</summary>
        public static int Compare(float depthA, int seqA, float depthB, int seqB)
        {
            int order = depthA.CompareTo(depthB);

            return order != 0 ? order : seqA.CompareTo(seqB);
        }
    }
}
