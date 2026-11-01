// SPDX-License-Identifier: BSD-2-Clause
// Proposed task1053 contract: pure reference geometry, deliberately not wired.
using System;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal enum ReceiverPlaneKind { Unknown, Floor, Roof }
internal enum ReceiverSourceKind { Unknown, Point, Sun }

// XY is in tiles; Z is in native UO elevation units, NOT tiles or pixels.
// Layer identifies a known physical plane, never a visual draw/cutaway group.
internal readonly record struct ReceiverPatch(N2 Min, N2 Max, float Z,
    uint Layer, ReceiverPlaneKind Kind);
internal readonly record struct ReceiverCaster(float BaseZ, float TopZ);
internal readonly record struct ReceiverCover(N3 Min, N3 Max);

// Point: Value is world XYZ. Sun: Value points TOWARD the sun in the mixed
// tile/tile/UO-Z basis; SkyZ is above every cover in the supported corridor.
internal readonly record struct ReceiverSource(ReceiverSourceKind Kind, N3 Value, float SkyZ = 0);

internal static class ReceiverGeometry
{
    private const float Epsilon = 0.00001f;

    // One opaque silhouette sample -> one explicitly bounded horizontal plane.
    // This is NOT a whole-stamp gate: different alpha samples have different Z.
    // completeCover means the entire receiver-to-source corridor was captured;
    // an empty, complete span means exposed, not missing metadata.
    internal static bool TryProject(ReceiverCaster caster, N3 sample,
        ReceiverPatch receiver, ReceiverSource source, ReadOnlySpan<ReceiverCover> covers,
        bool completeCover, out N3 projected)
    {
        projected = default;
        if (!completeCover || !Finite(sample) || !float.IsFinite(caster.BaseZ)
            || !float.IsFinite(caster.TopZ) || caster.TopZ < caster.BaseZ
            || sample.Z < caster.BaseZ || sample.Z > caster.TopZ
            || receiver.Layer == 0 || receiver.Kind is not (ReceiverPlaneKind.Floor or ReceiverPlaneKind.Roof)
            || !Finite(receiver.Min) || !Finite(receiver.Max) || !float.IsFinite(receiver.Z)
            || receiver.Min.X >= receiver.Max.X || receiver.Min.Y >= receiver.Max.Y
            || sample.Z < receiver.Z || !Finite(source.Value)) return false;

        N3 toward;
        if (source.Kind == ReceiverSourceKind.Point) toward = source.Value - sample;
        else if (source.Kind == ReceiverSourceKind.Sun && float.IsFinite(source.SkyZ)
            && source.SkyZ > sample.Z) toward = source.Value;
        else return false;
        // Initial domain: descending light-to-sample-to-plane rays only.
        // Horizontal/upward rays need another receiver contract, not a clamp.
        if (!Finite(toward) || toward.Z <= Epsilon) return false;
        float t = (sample.Z - receiver.Z) / toward.Z;
        N3 hit = sample - toward * t;
        hit.Z = receiver.Z;
        if (!Finite(hit) || hit.X < receiver.Min.X || hit.Y < receiver.Min.Y
            || hit.X >= receiver.Max.X || hit.Y >= receiver.Max.Y) return false;

        if (!TrySourceEnd(hit, source, out var end)) return false;
        foreach (var cover in covers)
        {
            if (!ValidCover(cover) || (source.Kind == ReceiverSourceKind.Sun && cover.Max.Z >= source.SkyZ)) return false;
            if (Intersects(hit, end, cover)) return false;
        }
        projected = hit;
        return true;
    }

    internal static bool TrySourceEnd(N3 start, ReceiverSource source, out N3 end)
    {
        end = default;
        if (!Finite(start) || !Finite(source.Value)) return false;
        if (source.Kind == ReceiverSourceKind.Point) end = source.Value;
        else if (source.Kind == ReceiverSourceKind.Sun && source.Value.Z > Epsilon
            && float.IsFinite(source.SkyZ) && source.SkyZ > start.Z)
            end = start + source.Value * ((source.SkyZ - start.Z) / source.Value.Z);
        else return false;
        return Finite(end) && Finite(end - start) && end.Z > start.Z;
    }

    // Shared shelter primitive for a known world point. No AllowedToDraw,
    // max visible Z or cutaway parameter exists. Unknown cover fails closed.
    internal static bool TrySheltered(N3 point, float skyZ,
        ReadOnlySpan<ReceiverCover> covers, bool completeCover, out bool sheltered)
    {
        sheltered = false;
        if (!completeCover || !Finite(point) || !float.IsFinite(skyZ) || skyZ <= point.Z) return false;
        N3 end = new(point.X, point.Y, skyZ);
        if (!Finite(end - point)) return false;
        bool found = false;
        foreach (var cover in covers)
        {
            if (!ValidCover(cover) || cover.Max.Z >= skyZ) return false;
            found |= Intersects(point, end, cover);
        }
        sheltered = found;
        return true;
    }

    internal static bool ValidCover(ReceiverCover b) => Finite(b.Min) && Finite(b.Max)
        && b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z
        // Horizontal slabs and axis-aligned vertical faces; not lines/points.
        && ((b.Min.X < b.Max.X && b.Min.Y < b.Max.Y)
            || (b.Min.X < b.Max.X && b.Min.Z < b.Max.Z)
            || (b.Min.Y < b.Max.Y && b.Min.Z < b.Max.Z));

    // Closed physical boxes, open ray endpoints. A plane can be zero thickness.
    // A receiver's own slab ending at its top does not shelter that top.
    private static bool Intersects(N3 start, N3 end, ReceiverCover box)
    {
        N3 d = end - start;
        float lo = 0, hi = 1;
        return Axis(start.X, d.X, box.Min.X, box.Max.X, ref lo, ref hi)
            && Axis(start.Y, d.Y, box.Min.Y, box.Max.Y, ref lo, ref hi)
            && Axis(start.Z, d.Z, box.Min.Z, box.Max.Z, ref lo, ref hi)
            && hi > 0 && lo < 1;
    }

    private static bool Axis(float p, float d, float min, float max, ref float lo, ref float hi)
    {
        if (d == 0) return p >= min && p <= max;
        float a = (min - p) / d, b = (max - p) / d;
        if (a > b) (a, b) = (b, a);
        lo = MathF.Max(lo, a); hi = MathF.Min(hi, b);
        return lo <= hi;
    }

    private static bool Finite(N2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    private static bool Finite(N3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
