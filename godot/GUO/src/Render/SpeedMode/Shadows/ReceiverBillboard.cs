// SPDX-License-Identifier: BSD-2-Clause
// Explicit source-oriented 2.5D approximation, not physical mesh reconstruction.
using System;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

// Supplied by native-contact1052: actual pre-view affine corners, actual sampled
// texel centres and current-frame cached opaque metadata. Token identifies the
// immutable owner/draw/frame occurrence in this ticket, not just a graphic ID.
internal readonly record struct ReceiverNativeFrame(N2 A, N2 B, N2 C,
    N2 TexelA, N2 TexelB, N2 TexelC,
    N2 OpaqueMin, N2 OpaqueMax, N2 Contact, ulong Token);

internal readonly record struct ReceiverBillboard(N3 ContactWorld, N2 ContactNative,
    N2 OpaqueMinNative, N2 OpaqueMaxNative, ulong NativeFrame, ReceiverCaster Span)
{
    // Length of native isometric XY projection of ANY unit tile-space axis:
    // sqrt((22*(x-y))^2+(22*(x+y))^2) = 22*sqrt(2) for x*x+y*y=1.
    internal static readonly float PixelsPerTile = 22 * MathF.Sqrt(2);
    internal const float PixelsPerZ = 4;

    internal static bool TrySunSource(N2 shadowDirection, float nativeLengthPerVerticalPixel, out ReceiverSource source)
    {
        source = default;
        float length = shadowDirection.Length();
        if (!Finite(shadowDirection) || !float.IsFinite(length) || length <= 0
            || !float.IsFinite(nativeLengthPerVerticalPixel) || nativeLengthPerVerticalPixel <= 0) return false;
        float z = PixelsPerTile / (PixelsPerZ * nativeLengthPerVerticalPixel);
        if (!float.IsFinite(z) || z <= 0) return false;
        source = new(ReceiverSourceKind.Sun,
            new(-shadowDirection.X / length, -shadowDirection.Y / length, z), ReceiverStructureSnapshot.SkyZ);
        return true;
    }

    internal static bool TryCalibrateFrame(N3 worldContact, ReceiverNativeFrame frame, out ReceiverBillboard model)
    {
        model = default;
        if (!Finite(frame.A) || !Finite(frame.B) || !Finite(frame.C)
            || !Finite(frame.TexelA) || !Finite(frame.TexelB) || !Finite(frame.TexelC)
            || !Finite(frame.OpaqueMin) || !Finite(frame.OpaqueMax) || !Finite(frame.Contact)
            || frame.OpaqueMin.X >= frame.OpaqueMax.X || frame.OpaqueMin.Y >= frame.OpaqueMax.Y
            || frame.Contact.X < frame.OpaqueMin.X || frame.Contact.X > frame.OpaqueMax.X
            || frame.Contact.Y != frame.OpaqueMax.Y
            || frame.A.Y != frame.B.Y || frame.A.X != frame.C.X || frame.C.Y <= frame.A.Y
            || frame.A.X == frame.B.X || frame.TexelA.Y != frame.TexelB.Y
            || frame.TexelA.X != frame.TexelC.X || frame.TexelC.Y <= frame.TexelA.Y
            || frame.TexelA.X == frame.TexelB.X
            || frame.OpaqueMin.X < MathF.Min(frame.TexelA.X, frame.TexelB.X)
            || frame.OpaqueMax.X > MathF.Max(frame.TexelA.X, frame.TexelB.X)
            || frame.OpaqueMin.Y < frame.TexelA.Y || frame.OpaqueMax.Y > frame.TexelC.Y)
            return false;
        N2 Map(N2 pixel) => frame.A
            + (frame.B - frame.A) * ((pixel.X - frame.TexelA.X) / (frame.TexelB.X - frame.TexelA.X))
            + (frame.C - frame.A) * ((pixel.Y - frame.TexelA.Y) / (frame.TexelC.Y - frame.TexelA.Y));
        N2 min = Map(frame.OpaqueMin), max = Map(frame.OpaqueMax);
        return TryCalibrate(worldContact, Map(frame.Contact), N2.Min(min, max), N2.Max(min, max), frame.Token, out model);
    }

    // Bounds/contact are native pre-view pixel CENTRES mapped through the actual
    // captured affine draw, including its animated frame/UV/deformation.
    // Producer must reject deformations whose opaque row cannot map to one
    // vertical billboard. No sprite dimension or tiledata fallback here.
    internal static bool TryCalibrate(N3 contactWorld, N2 contactNative,
        N2 opaqueMinNative, N2 opaqueMaxNative, ulong nativeFrame, out ReceiverBillboard model)
    {
        model = default;
        if (nativeFrame == 0 || !Finite(contactWorld) || !Finite(contactNative)
            || !Finite(opaqueMinNative) || !Finite(opaqueMaxNative)
            || opaqueMinNative.X >= opaqueMaxNative.X || opaqueMinNative.Y >= opaqueMaxNative.Y
            || contactNative.X < opaqueMinNative.X || contactNative.X > opaqueMaxNative.X
            || contactNative.Y != opaqueMaxNative.Y) return false;
        float top = contactWorld.Z + (contactNative.Y - opaqueMinNative.Y) / PixelsPerZ;
        if (!float.IsFinite(top)) return false;
        model = new(contactWorld, contactNative, opaqueMinNative, opaqueMaxNative,
            nativeFrame, new(contactWorld.Z, top));
        return true;
    }

    internal bool TryMap(N2 nativeSample, ReceiverSource source, ulong nativeFrame, out N3 world)
    {
        world = default;
        if (NativeFrame == 0 || nativeFrame != NativeFrame
            || !TryCalibrate(ContactWorld, ContactNative, OpaqueMinNative, OpaqueMaxNative, NativeFrame, out var canonical)
            || canonical != this || !Finite(nativeSample)
            || nativeSample.X < OpaqueMinNative.X || nativeSample.X > OpaqueMaxNative.X
            || nativeSample.Y < OpaqueMinNative.Y || nativeSample.Y > OpaqueMaxNative.Y
            || !Finite(source.Value)) return false;
        N2 toward = source.Kind switch {
            ReceiverSourceKind.Point => new(source.Value.X - ContactWorld.X, source.Value.Y - ContactWorld.Y),
            ReceiverSourceKind.Sun => new(source.Value.X, source.Value.Y),
            _ => default
        };
        float length = toward.Length();
        if (!float.IsFinite(length) || length <= 0) return false;
        N2 across = new(-toward.Y / length, toward.X / length);
        N2 xy = new N2(ContactWorld.X, ContactWorld.Y)
            + across * ((nativeSample.X - ContactNative.X) / PixelsPerTile);
        world = new(xy, ContactWorld.Z + (ContactNative.Y - nativeSample.Y) / PixelsPerZ);
        return Finite(world) && world.Z >= Span.BaseZ && world.Z <= Span.TopZ;
    }

    private static bool Finite(N2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static bool Finite(N3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
