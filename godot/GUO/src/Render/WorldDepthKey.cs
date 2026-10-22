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

    // ---- The key's meaning (GPU-01, ADR-0007 "key layout", 2026-10-22) ----
    //
    // The 28 bits reproduce RenderLists' sort: CalculateDepthZ() first, then
    // the queue order the tile walk gives equal depths. Nothing below derives
    // an order from coordinates that the sort does not already use.
    //
    //   [27..13]  depth: round(100 * CalculateDepthZ()) relative to the frame's
    //             origin diagonal (the lossless integer rescale of ADR-0001's
    //             one depth function; a mobile body's + 0.5 is + 50)
    //   [12..4]   tile: the walk order of the object's tile among the tiles on
    //             its diagonal (chunk x outer, chunk y, then x within the chunk:
    //             GameScene.FillGameObjectList and AddTileToRenderList), relative
    //             to the first chunk column that can show on that diagonal
    //   [3..0]    slot: the object's place in its tile's chain, counted against
    //             the instanced statics of that tile: an instanced static with
    //             rank r (its chain order among instanced statics) holds 2r + 1;
    //             anything queued that follows k instanced statics in the chain
    //             holds 2k; covering land holds 0 (it is queued before the tile)
    //
    // Statics carry the absolute depth and tile parts in their instance buffer;
    // the vertex shader subtracts the frame's two origins in uint arithmetic
    // (world_depth_key.gdshaderinc, the same formula as Relative below) and
    // bit-casts the result. Queued objects get the same key on the CPU. Equal
    // keys are impossible between a static and a queued object (odd against
    // even slot) and between two statics (distinct tile or rank); between two
    // queued objects they are resolved by submission order (WorldDepthOrder.Plan).
    public const int SlotBits = 4;
    public const int TileBits = 9;
    public const int DepthBits = 15;
    public const int TieBits = TileBits + SlotBits;
    public const uint DepthRange = 1u << DepthBits;
    public const uint TileRange = 1u << TileBits;
    public const int SlotCount = 1 << SlotBits;

    /// <summary>Instanced statics per tile the slot field can hold (ranks 0..6 use the odd slots 1..13; slot 14 is for what follows them).</summary>
    public const int MaxInstancedPerTile = 7;

    /// <summary>
    /// The first diagonal (x + y) from which the integer depth part orders
    /// exactly as the float CalculateDepthZ() does. Below it the float keeps
    /// a few ulps between two values of the same hundredth (the sort then
    /// separates what the key ties); tools/world_depth_tests measures the
    /// bound over the whole domain. A frame that draws anything under it
    /// does not use the key.
    /// </summary>
    public const int FirstExactDiagonal = 32;

    /// <summary>Hundredths added so that the smallest depth in the map (x + y = 0, PriorityZ -130) stays positive.</summary>
    public const int DepthShift = 1000;

    /// <summary>
    /// The absolute depth part: CalculateDepthZ() in hundredths, as an integer.
    /// Every (x + y) up to the map's size and every PriorityZ a tile can hold
    /// is checked by tools/world_depth_tests to round back to the exact
    /// hundredth and to keep the float's order.
    /// </summary>
    public static uint DepthAbs(float depthZ)
    {
        float scaled = depthZ * 100f;
        int hundredths = (int)MathF.Round(scaled) + DepthShift;
        if (hundredths < 0)
            throw new ArgumentOutOfRangeException(nameof(depthZ));
        return (uint)hundredths;
    }

    /// <summary>The origin depth part for a frame whose first visible diagonal (x + y) is <paramref name="firstDiagonal"/>.</summary>
    public static uint DepthOrigin(int firstDiagonal)
    {
        // One diagonal of slack below the first one: PriorityZ runs to -130,
        // three hundredths under the diagonal itself.
        long origin = 100L * firstDiagonal + DepthShift - 100;
        return origin < 0 ? 0u : (uint)origin;
    }

    /// <summary>
    /// The walk order of tile (x, y) among the tiles on its diagonal, as an
    /// absolute index: 8 per chunk column, dense within the column.
    /// </summary>
    /// <remarks>
    /// Along one diagonal s = x + y, the walk visits chunk columns in x order.
    /// Within a column the eight tiles split into at most two chunk rows; the
    /// lower chunk row (larger x) is walked first, and x ascends inside a row.
    /// </remarks>
    public static int TileOrderAbs(int x, int y)
    {
        int s = x + y;
        int x0 = x & ~7;
        int w = (s - x0) & 7;
        int i = x & 7;
        int index = i > w ? i - w - 1 : 7 - w + i;
        return (x0 >> 3) * 8 + index;
    }

    /// <summary>
    /// The first chunk column that can show on diagonal <paramref name="s"/>,
    /// given the smallest (x - y) the screen's left edge admits.
    /// </summary>
    public static int FirstColumn(int s, int minDiff)
    {
        int xMin = (s + minDiff) >> 1;
        return xMin < 0 ? 0 : xMin >> 3;
    }

    /// <summary>The absolute tie part: tile order and slot together.</summary>
    public static uint TieAbs(int x, int y, int slot)
    {
        if ((uint)slot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return ((uint)TileOrderAbs(x, y) << SlotBits) | (uint)slot;
    }

    /// <summary>
    /// The frame-relative key, or false when the object is outside the range
    /// the frame's origins allow (the shader collapses such an instance; a
    /// queued object makes the frame fall back). Never masks an overflow.
    /// </summary>
    public static bool Relative(uint depthAbs, uint tieAbs, int s, uint depthOrigin, int minDiff, out uint key)
    {
        key = 0;
        if (depthAbs < depthOrigin)
            return false;
        uint depth = depthAbs - depthOrigin;
        if (depth >= DepthRange)
            return false;
        uint tieOrigin = (uint)FirstColumn(s, minDiff) * 8u << SlotBits;
        if (tieAbs < tieOrigin)
            return false;
        uint tie = tieAbs - tieOrigin;
        if (tie >= (1u << TieBits))
            return false;
        key = (depth << TieBits) | tie;
        return true;
    }
}
