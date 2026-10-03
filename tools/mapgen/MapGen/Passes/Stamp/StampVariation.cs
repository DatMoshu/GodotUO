namespace CentrED.MapGen.Passes.Stamp;

// 8-orientation stamp transform: 4 rotations × optional mirror.
// Rotations are 0/90/180/270 around the stamp anchor. Mirror flips X.
// Useful at compose time so a 300-stamp library expands to 2400 effective placements.
public enum StampVariant : byte
{
    Original   = 0,
    Rot90      = 1,
    Rot180     = 2,
    Rot270     = 3,
    Mirror     = 4,
    MirrorRot90 = 5,
    MirrorRot180 = 6,
    MirrorRot270 = 7,
}

public static class StampTransform
{
    // Returns transformed (rx, ry). Stamp anchor is implicit at (0,0).
    public static (int rx, int ry) Apply(int rx, int ry, StampVariant variant)
    {
        bool mirror = (int)variant >= 4;
        int rot = (int)variant & 3;
        if (mirror) rx = -rx;
        return rot switch
        {
            0 => (rx, ry),
            1 => (-ry, rx),    // 90 CCW
            2 => (-rx, -ry),   // 180
            _ => (ry, -rx),    // 270 CCW
        };
    }
}
