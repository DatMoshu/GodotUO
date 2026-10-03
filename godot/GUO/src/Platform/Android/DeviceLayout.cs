// GUO addition: deterministic layout policy, independent of Android/Godot.
using System;

namespace GUO.Platform.Android;

internal enum DevicePosture { Auto, Phone, Flat, Tabletop, Book, Tent, Tablet }
internal readonly record struct LayoutRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Empty => Width <= 0 || Height <= 0;
}
internal readonly record struct DeviceLayout(DevicePosture Posture, LayoutRect World,
    LayoutRect Companion, LayoutRect Movement, LayoutRect Actions, LayoutRect Hinge)
{
    // Geometry is in client pixels. DpPerPixel converts those pixels to Android dp.
    public static DeviceLayout Resolve(int width, int height, float dpPerPixel,
        DevicePosture requested, LayoutRect hinge, bool separating, bool controls)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        float dp = Math.Max(0.1f, dpPerPixel);
        bool vertical = hinge.Height > hinge.Width;
        // Ignore stale/out-of-window features after a rotation. Never infer a hinge from aspect ratio.
        bool validHinge = hinge.X >= 0 && hinge.Y >= 0 && hinge.Right <= w && hinge.Bottom <= h
            && (vertical ? hinge.X > 0 && hinge.Right < w : hinge.Y > 0 && hinge.Bottom < h);
        DevicePosture mode = requested;
        if (validHinge && separating) mode = vertical ? DevicePosture.Book : DevicePosture.Tabletop;
        else if (mode == DevicePosture.Auto && validHinge) mode = DevicePosture.Flat;
        else if (mode == DevicePosture.Auto)
            mode = Math.Min(w, h) * dp >= 600 ? DevicePosture.Tablet
                : (float)Math.Max(w, h) / Math.Min(w, h) <= 1.34f ? DevicePosture.Flat : DevicePosture.Phone;
        int gap = Math.Max(1, (int)Math.Ceiling(8 / dp));
        int rail = controls ? Math.Min(w / 4, Math.Max(1, (int)Math.Ceiling(104 / dp))) : 0;
        if (mode == DevicePosture.Book)
        {
            int left = validHinge && vertical ? Math.Max(1, hinge.X - gap) : Math.Max(1, w / 2 - gap);
            int right = validHinge && vertical ? Math.Min(w - 1, hinge.Right + gap) : Math.Min(w - 1, w / 2 + gap);
            int pad = controls ? Math.Min(h / 3, Math.Max(1, (int)Math.Ceiling(160 / dp))) : 0;
            return new(mode, new(0, 0, left, h - pad), new(right, 0, w - right, h - pad),
                new(0, h - pad, left, pad), new(right, h - pad, w - right, pad),
                new(left, 0, right - left, h));
        }
        if (mode == DevicePosture.Tent && !controls)
            return new(mode, new(0, 0, w, h), default, default, default, default);
        int top = mode is DevicePosture.Flat or DevicePosture.Tabletop ? h / 2 : h * 2 / 3;
        int bottom = top;
        LayoutRect exclusion = default;
        if (mode == DevicePosture.Tabletop)
        {
            top = validHinge && !vertical ? Math.Max(1, hinge.Y - gap) : Math.Max(1, h / 2 - gap);
            bottom = validHinge && !vertical ? Math.Min(h - 1, hinge.Bottom + gap) : Math.Min(h - 1, h / 2 + gap);
            exclusion = new(0, top, w, bottom - top);
        }
        top = Math.Max(1, top);
        bottom = Math.Min(h, Math.Max(top, bottom));
        return new(mode, new(0, 0, w, top), new(rail, bottom, w - 2 * rail, h - bottom),
            new(0, bottom, rail, h - bottom), new(w - rail, bottom, rail, h - bottom), exclusion);
    }
}
