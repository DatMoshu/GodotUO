// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Game.Data
{
    internal enum GraphicEffectType
    {
        Moving = 0x00,
        Lightning = 0x01,
        FixedXYZ = 0x02,
        FixedFrom = 0x03,
        ScreenFade = 0x04,

        DragEffect = 0x05, // custom

        Nothing = 0xFF
    }
}