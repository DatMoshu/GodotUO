// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Renderer;

/// <summary>Exact, monotonic reverse-Z encoding of the ADR-0007 28-bit integer key.</summary>
public static class WorldDepthKey
{
    public const uint Count = 1u << 28;
    public const uint FirstFloatBits = 0x2f800000u;

    // Positive normal binary32 values have the same order as their bit patterns.
    // This interval has exactly 2^28 members, from 2^-32 to the float below 1.
    // Do not divide the integer by 2^28: that collapses high keys before upload.
    public static float Encode(uint key)
    {
        if (key >= Count)
            throw new ArgumentOutOfRangeException(nameof(key));
        return BitConverter.UInt32BitsToSingle(FirstFloatBits + key);
    }
}
