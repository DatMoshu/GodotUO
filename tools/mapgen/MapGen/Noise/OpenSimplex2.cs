// Minimal 2D OpenSimplex2 (Smooth) noise. Returns values in roughly [-1, 1].
// Adapted to produce deterministic output keyed by a 64-bit seed via an internal permutation.
// This is a single-file implementation kept short; for production-grade noise consider
// vendoring KdotJPG/OpenSimplex2.

namespace CentrED.MapGen.Noise;

public sealed class OpenSimplex2
{
    private readonly short[] _perm = new short[256];

    public OpenSimplex2(long seed)
    {
        var source = new short[256];
        for (short i = 0; i < 256; i++) source[i] = i;
        // Splitmix-style scrambling
        ulong s = (ulong)seed;
        for (int i = 255; i >= 0; i--)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            int r = (int)((s >> 17) % (ulong)(i + 1));
            _perm[i] = source[r];
            source[r] = source[i];
        }
    }

    private const double Stretch2 = -0.211324865405187;  // (1/sqrt(2+1) - 1) / 2
    private const double Squish2  =  0.366025403784439;  // (sqrt(2+1) - 1) / 2
    private const double Norm2    =  47.0;

    private static readonly sbyte[] Gradients2 =
    {
         5,  2,    2,  5,
        -5,  2,   -2,  5,
         5, -2,    2, -5,
        -5, -2,   -2, -5,
    };

    public double Eval(double x, double y)
    {
        double stretchOffset = (x + y) * Stretch2;
        double xs = x + stretchOffset;
        double ys = y + stretchOffset;

        int xsb = FastFloor(xs);
        int ysb = FastFloor(ys);

        double squishOffset = (xsb + ysb) * Squish2;
        double dx0 = x - (xsb + squishOffset);
        double dy0 = y - (ysb + squishOffset);

        double xins = xs - xsb;
        double yins = ys - ysb;
        double inSum = xins + yins;

        double value = 0.0;

        // Contribution (1, 0)
        double dx1 = dx0 - 1 - Squish2;
        double dy1 = dy0 - 0 - Squish2;
        double attn1 = 2.0 - dx1 * dx1 - dy1 * dy1;
        if (attn1 > 0)
        {
            attn1 *= attn1;
            value += attn1 * attn1 * Extrapolate(xsb + 1, ysb + 0, dx1, dy1);
        }

        // Contribution (0, 1)
        double dx2 = dx0 - 0 - Squish2;
        double dy2 = dy0 - 1 - Squish2;
        double attn2 = 2.0 - dx2 * dx2 - dy2 * dy2;
        if (attn2 > 0)
        {
            attn2 *= attn2;
            value += attn2 * attn2 * Extrapolate(xsb + 0, ysb + 1, dx2, dy2);
        }

        double dx_ext, dy_ext;
        int xsv_ext, ysv_ext;
        if (inSum <= 1)
        {
            double zins = 1 - inSum;
            if (zins > xins || zins > yins)
            {
                if (xins > yins)
                {
                    xsv_ext = xsb + 1; ysv_ext = ysb - 1;
                    dx_ext = dx0 - 1; dy_ext = dy0 + 1;
                }
                else
                {
                    xsv_ext = xsb - 1; ysv_ext = ysb + 1;
                    dx_ext = dx0 + 1; dy_ext = dy0 - 1;
                }
            }
            else
            {
                xsv_ext = xsb + 1; ysv_ext = ysb + 1;
                dx_ext = dx0 - 1 - 2 * Squish2; dy_ext = dy0 - 1 - 2 * Squish2;
            }
        }
        else
        {
            double zins = 2 - inSum;
            if (zins < xins || zins < yins)
            {
                if (xins > yins)
                {
                    xsv_ext = xsb + 2; ysv_ext = ysb + 0;
                    dx_ext = dx0 - 2 - 2 * Squish2; dy_ext = dy0 + 0 - 2 * Squish2;
                }
                else
                {
                    xsv_ext = xsb + 0; ysv_ext = ysb + 2;
                    dx_ext = dx0 + 0 - 2 * Squish2; dy_ext = dy0 - 2 - 2 * Squish2;
                }
            }
            else
            {
                xsv_ext = xsb; ysv_ext = ysb;
                dx_ext = dx0; dy_ext = dy0;
            }
            xsb += 1; ysb += 1;
            dx0 = dx0 - 1 - 2 * Squish2;
            dy0 = dy0 - 1 - 2 * Squish2;
        }

        // Contribution (0,0) or (1,1)
        double attn0 = 2.0 - dx0 * dx0 - dy0 * dy0;
        if (attn0 > 0)
        {
            attn0 *= attn0;
            value += attn0 * attn0 * Extrapolate(xsb, ysb, dx0, dy0);
        }

        // Extra contribution
        double attnExt = 2.0 - dx_ext * dx_ext - dy_ext * dy_ext;
        if (attnExt > 0)
        {
            attnExt *= attnExt;
            value += attnExt * attnExt * Extrapolate(xsv_ext, ysv_ext, dx_ext, dy_ext);
        }

        return value / Norm2;
    }

    private double Extrapolate(int xsb, int ysb, double dx, double dy)
    {
        int idx = (_perm[(xsb & 0xFF)] + ysb) & 0xFF;
        int g = (_perm[idx] & 0x0E);
        return Gradients2[g] * dx + Gradients2[g + 1] * dy;
    }

    private static int FastFloor(double x)
    {
        int xi = (int)x;
        return x < xi ? xi - 1 : xi;
    }
}
