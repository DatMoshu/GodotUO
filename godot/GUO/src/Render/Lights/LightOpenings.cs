// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: which window-flagged statics actually let lamp light through.
// Idea from MeesaJarJar's GodotUO lighting snapshot 495f0775 (BSD-2-Clause);
// GUO keys on verified graphic ids instead of a name match.
using GUO.Assets;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// Light transmission of window-flagged statics, shared by the reach field
    /// and the polar wall-shadow grid. Tiledata flags castle arrow loops as
    /// Wall|Window (the same as an open window), but their art is solid
    /// masonry with a hairline slit: a lamp must not pass a whole pool of
    /// light through one. Window glow (the light offer a window makes) is a
    /// separate rule and is unaffected.
    /// </summary>
    internal static class LightOpenings
    {
        // Verified 2026-11-21 against the client's tiledata and art: each is
        // named "arrow loop", flagged Wall|Window|Impassable|LightSource, height 20,
        // and drawn as solid stone with a one-pixel slit. Listed by id so a shard
        // or tiledata rename cannot silently change the classification. Each pair
        // is one wall set (cut stone, rough stone), one piece per wall direction.
        public const ushort ArrowLoopStone1 = 0x005B;
        public const ushort ArrowLoopStone2 = 0x005C;
        public const ushort ArrowLoopRough1 = 0x00CA;
        public const ushort ArrowLoopRough2 = 0x00CB;

        /// <summary>
        /// Probe A/B only (<c>--layer-openings</c>): restore the old behaviour, a slit
        /// transmitting like a window, to photograph before and after at one tick.
        /// The client never sets it; it is part of the reach-grid hash so a flip rebuilds.
        /// </summary>
        internal static bool ProbeSlitsTransmit;

        /// <summary>A castle arrow slit: blocks lamp light like the wall around it.</summary>
        public static bool IsArrowSlit(ushort graphic)
            => graphic is ArrowLoopStone1 or ArrowLoopStone2 or ArrowLoopRough1 or ArrowLoopRough2;

        /// <summary>The Window flag as light transmission sees it: off for an arrow slit.</summary>
        public static bool TransmitsLight(ushort graphic, bool isWindow) => isWindow && (ProbeSlitsTransmit || !IsArrowSlit(graphic));

        /// <summary>The tile's flags with Window cleared for an arrow slit, for flag-based blocker filters.</summary>
        public static TileFlag TransmissionFlags(ushort graphic, TileFlag flags)
            => !ProbeSlitsTransmit && IsArrowSlit(graphic) ? flags & ~TileFlag.Window : flags;
    }
}
