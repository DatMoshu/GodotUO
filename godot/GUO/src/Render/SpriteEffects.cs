// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace GUO.Renderer
{
    /// <summary>
    /// Which axes a sprite is mirrored on when it is drawn.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream uses XNA's
    /// <c>Microsoft.Xna.Framework.Graphics.SpriteEffects</c>. The values and
    /// the name are kept, so call sites and the <c>&amp; (SpriteEffects)0x03</c>
    /// masking upstream does both port unchanged. Only the namespace moves.
    /// </remarks>
    [Flags]
    public enum SpriteEffects : byte
    {
        None = 0,
        FlipHorizontally = 1,
        FlipVertically = 2
    }
}
