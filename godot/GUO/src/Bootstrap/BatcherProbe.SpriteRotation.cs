// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifySpriteRotation()
    {
        // Exact bits matter here: Sin(-0) is -0, while epsilon must not be
        // rounded into the zero fast path. Non-finite inputs retain Math behavior.
        int[] bits = { 0, int.MinValue, 1, unchecked((int)0x80000001),
            0x007fffff, 0x00800000, 0x3eaaaaab, unchecked((int)0xbeaaaaab),
            0x3fc90fdb, 0x40490fdb, 0x7f7fffff, unchecked((int)0xff7fffff),
            0x7f800000, unchecked((int)0xff800000), 0x7fc12345,
            unchecked((int)0xffc12345), 0x7f812345 };
        foreach (int value in bits)
        {
            float rotation = BitConverter.Int32BitsToSingle(value);
            int expectedSin = BitConverter.SingleToInt32Bits((float)Math.Sin(rotation));
            int expectedCos = BitConverter.SingleToInt32Bits((float)Math.Cos(rotation));
            CheckThat($"sprite rotation bits {value:X8}",
                BitConverter.SingleToInt32Bits(UltimaBatcher2D.RotationSine(rotation)) == expectedSin
                && BitConverter.SingleToInt32Bits(UltimaBatcher2D.RotationCosine(rotation)) == expectedCos,
                "matches original double trigonometry converted to float, including signed zero");
        }
    }
}
