// SPDX-License-Identifier: BSD-2-Clause
// Source-only proposal regressions. Invoke only in an admitted pure harness.
using System;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal static class ReceiverStructureProbe
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Receiver structure: " + name);
            checks++;
        }
        ReceiverFrameKey key = new(1, 2, 3, 10, 20, 1280, 720, Matrix3x2.Identity);
        ReceiverPhysicalLayer[] layers = {
            new(1, 0, ReceiverPlaneKind.Floor), new(2, 30, ReceiverPlaneKind.Roof),
            new(3, 60, ReceiverPlaneKind.Floor), new(4, 90, ReceiverPlaneKind.Roof)
        };
        ReceiverCover[] roof = { new(new(.1f, .1f, 30), new(.9f, .9f, 32)) };
        var snapshot = new ReceiverStructureSnapshot(key, 0, 0, 2, 2);
        Check(snapshot.PutCell(0, 0, new(3, 3, 4, 4, false), layers, roof), "four stacked layers fit");
        // Source spans are copied; changing builder arrays cannot change snapshot.
        layers[0] = new(99, -10, ReceiverPlaneKind.Floor);
        roof[0] = default;
        Check(!snapshot.TryLayer(key, 0, 0, 1, 0, out _), "unsealed data inaccessible");
        snapshot.Seal();
        Check(snapshot.TryLayer(key, 0, 0, 1, 0, out var ground) && ground.Layer != 0, "copied owner and base layer");
        Check(snapshot.TryLayer(key, 0, 0, 3, 60, out var upper) && upper.Layer != ground.Layer, "stacked layer IDs distinct");
        Check(!snapshot.TryLayer(key, 0, 0, 1, 60, out _), "owner-Z association required");
        Check(!snapshot.PutCell(1, 1, new(3, 3, 4, 4, false), layers, Array.Empty<ReceiverCover>()), "sealed snapshot immutable");
        ReceiverCover[] output = new ReceiverCover[ReceiverStructureSnapshot.CorridorCovers];
        N3 start = new(.5f, .5f, 0), end = new(.5f, .5f, ReceiverStructureSnapshot.SkyZ);
        Check(snapshot.TryCopyCorridor(key, start, end, output, out int count) && count == 1
            && output[0].Min.Z == 30, "copied vertical corridor");
        Check(ReceiverGeometry.TrySheltered(start, end.Z, output.AsSpan(0, count), true, out bool sheltered) && sheltered,
            "weather uses same structural records");
        Check(snapshot.TryCopyCorridor(key, new(.5f, .5f, 32), end, output, out count)
            && ReceiverGeometry.TrySheltered(new(.5f, .5f, 32), end.Z, output.AsSpan(0, count), true, out sheltered)
            && !sheltered, "roof top is exposed");
        Check(!snapshot.TryCopyCorridor(key with { Frame = 4 }, start, end, output, out _), "stale frame");
        Check(!snapshot.TryCopyCorridor(key with { Scene = 4 }, start, end, output, out _), "new scene");
        Check(!snapshot.TryCopyCorridor(key with { Topology = 4 }, start, end, output, out _), "changed physical topology");
        Check(!snapshot.TryCopyCorridor(key with { SurfaceX = 11 }, start, end, output, out _), "surface origin mismatch");
        Check(!snapshot.TryCopyCorridor(key with { Width = 1279 }, start, end, output, out _), "resize mismatch");
        Check(!snapshot.TryCopyCorridor(key with { View = Matrix3x2.CreateTranslation(1, 0) }, start, end, output, out _), "view mismatch");
        var sentinel = new ReceiverCover(new(100, 100, 100), new(101, 101, 101));
        output[0] = sentinel;
        Check(!snapshot.TryCopyCorridor(key, start, new(1.5f, .5f, 40), output, out count)
            && count == 0 && output[0] == sentinel, "unknown corridor cell rejects without partial output");
        Check(!snapshot.TryCopyCorridor(key, start, end, Span<ReceiverCover>.Empty, out count) && count == 0, "output capacity rejects");
        Check(!snapshot.TryCopyCorridor(key, new(0, .5f, 0), new(0, .5f, 512), output, out _), "tangent boundary needs adjacent cell");
        Check(snapshot.TrySheltered(key, start, output, out sheltered) && sheltered, "bound weather wrapper derives its own vertical corridor");
        Check(!snapshot.TrySheltered(key, new(1.5f, .5f, 0), output, out _), "bound weather wrapper rejects unknown cell");

        var invalid = new ReceiverStructureSnapshot(key, 0, 0, 2, 2);
        var five = new ReceiverPhysicalLayer[5];
        Check(!invalid.PutCell(0, 0, new(3, 3, 5, 5, false), five, Array.Empty<ReceiverCover>()), "layer overflow");
        var nine = new ReceiverCover[9];
        Check(!invalid.PutCell(1, 0, new(3, 3, 9, 9, false), Array.Empty<ReceiverPhysicalLayer>(), nine), "cover overflow");
        Check(!invalid.PutCell(0, 1, new(3, 4, 0, 0, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>()), "topology changed during enumeration");
        Check(!invalid.PutCell(1, 1, new(3, 3, 0, 1, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>()), "incomplete enumeration");
        invalid.Seal();
        Check(!invalid.TryCopyCorridor(key, start, end, output, out _), "overflow cannot publish a truncated cell");
        Check(!invalid.TryCopyCorridor(key, new(1.5f,.5f,0), new(1.5f,.5f,512), output, out count)
            && count == 0, "ninth cover invalidates its own affected query");
        var nodeOverflow = new ReceiverStructureSnapshot(key, 0, 0, 1, 1);
        Check(!nodeOverflow.PutCell(0, 0, new(3, 3, 65, 65, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>()), "native enumeration node budget");
        var unsupported = new ReceiverStructureSnapshot(key, 0, 0, 1, 1);
        Check(!unsupported.PutCell(0, 0, new(3, 3, 1, 1, true), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>()), "unsupported physical slope cannot certify exposed");
        var eight = new ReceiverCover[8];
        for (int i = 0; i < eight.Length; i++) eight[i] = new(new(.1f, .1f, i + 1), new(.9f, .9f, i + 1));
        var capacity = new ReceiverStructureSnapshot(key, 0, 0, 1, 1);
        Check(capacity.PutCell(0, 0, new(3, 3, 4, 4, false), Array.Empty<ReceiverPhysicalLayer>(), eight), "eight structural planes fit");
        capacity.Seal();
        Check(capacity.TryCopyCorridor(key, start, end, output, out count) && count == 8, "full cover capacity retained without MAX collapse");
        var duplicate = new ReceiverStructureSnapshot(key, 0, 0, 1, 1);
        Check(duplicate.PutCell(0, 0, new(3, 3, 0, 0, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>())
            && !duplicate.PutCell(0, 0, new(3, 3, 0, 0, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>()), "duplicate cell poisons admission");
        duplicate.Seal();
        Check(!duplicate.TryCopyCorridor(key, start, end, output, out _), "duplicate cell stays unknown");
        var full = new ReceiverStructureSnapshot(key, 0, 0, 32, 32);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            full.PutCell(x, y, new(3, 3, 0, 0, false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>());
        full.Seal();
        Check(full.TryCopyCorridor(key, start, new(7.5f, 7.5f, 512), output, out count) && count == 0, "64-cell corridor fits");
        Check(!full.TryCopyCorridor(key, start, new(8.5f, 7.5f, 512), output, out _), "65-plus-cell corridor rejects");

        var corners = new ReceiverStructureSnapshot(key, 3, 1, 2, 2);
        for (int y = 1; y <= 2; y++) for (int x = 3; x <= 4; x++)
        {
            ReceiverCover[] one = { new(new(x,y,10+x+y), new(x+1,y+1,10+x+y)) };
            Check(corners.PutCell(x, y, new(3,3,1,1,false), Array.Empty<ReceiverPhysicalLayer>(), one), "complete corner cell");
        }
        corners.Seal();
        Check(corners.TryCopyCorridor(key, new(4,2,0), new(4,2,512), output, out count)
            && count == 4, "integer XY gathers all four touching cells");
        Check(corners.TrySheltered(key, new(4,2,0), output, out sheltered) && sheltered, "four-cell closed roof corner protected");
        Check(!corners.TryCopyCorridor(key, new(3,1,0), new(3,1,512), output, out _), "snapshot outside tangent cells unknown");
        Check(!corners.TryCopyCorridor(key, new(4,2,0), new(5.125f,2,100), output, out _), "corridor outside snapshot unknown");
        var missingCorner = new ReceiverStructureSnapshot(key, 3, 1, 2, 2);
        for (int y = 1; y <= 2; y++) for (int x = 3; x <= 4; x++)
            if (x != 4 || y != 2)
                missingCorner.PutCell(x, y, new(3,3,0,0,false), Array.Empty<ReceiverPhysicalLayer>(), Array.Empty<ReceiverCover>());
        missingCorner.Seal();
        output[0] = sentinel;
        Check(!missingCorner.TryCopyCorridor(key, new(4,2,0), new(4,2,512), output, out count)
            && count == 0 && output[0] == sentinel, "one missing touching cell rejects without partial copy");

        var edge = new ReceiverStructureSnapshot(key, 3, 1, 3, 3);
        for (int y = 1; y <= 3; y++) for (int x = 3; x <= 5; x++)
        {
            bool ownsFace = x == 3 && y == 1;
            ReceiverCover[] faces = ownsFace
                ? new ReceiverCover[] { new(new(4,1,0), new(4,2,40)) } : Array.Empty<ReceiverCover>();
            edge.PutCell(x, y, new(3,3,ownsFace ? 1 : 0,ownsFace ? 1 : 0,false), Array.Empty<ReceiverPhysicalLayer>(), faces);
        }
        edge.Seal();
        Check(edge.TrySheltered(key, new(4,1.5f,20), output, out sheltered) && sheltered, "neighbour-owned exact wall face found");
        Check(edge.TrySheltered(key, new(4,2,20), output, out sheltered) && sheltered, "neighbour-owned wall corner found");
        Check(edge.TrySheltered(key, new(3.875f,1.5f,20), output, out sheltered) && !sheltered, "inside-nearby face-free paving positive");
        Check(edge.TrySheltered(key, new(4.125f,1.5f,20), output, out sheltered) && !sheltered, "outside-nearby face-free paving positive");
        Check(edge.TrySheltered(key, new(4,2.125f,20), output, out sheltered) && !sheltered, "beyond finite wall end positive");

        Check(ReceiverBillboard.TryCalibrate(new(10, 20, 5), new(100, 200), new(90, 120), new(110, 200), 7, out var model)
            && model.Span == new ReceiverCaster(5, 25), "80 native pixels calibrate20 UO-Z independently of tiledata height");
        ReceiverSource sun = new(ReceiverSourceKind.Sun, new(1, 0, 10), 512);
        Check(model.TryMap(new(100, 200), sun, 7, out var point) && Near(point, new(10, 20, 5)), "opaque contact maps to world base");
        Check(model.TryMap(new(100, 120), sun, 7, out point) && Near(point, new(10, 20, 25)), "opaque top maps to calibrated topZ");
        Check(ReceiverBillboard.TryCalibrate(N3.Zero, N2.Zero,
            new(-ReceiverBillboard.PixelsPerTile, -4), new(ReceiverBillboard.PixelsPerTile, 0), 8, out var width)
            && width.TryMap(new(ReceiverBillboard.PixelsPerTile, -4), sun, 8, out point)
            && Near(point, new(0, 1, 1)), "native isometric unit-length width calibration");
        Check(!model.TryMap(new(100, 120), sun, 8, out _), "wrong native frame rejected");
        Check(!model.TryMap(new(111, 120), sun, 7, out _), "outside opaque bounds rejected");
        Check(!ReceiverBillboard.TryCalibrate(N3.Zero, new(0, 1), new(-1, -4), new(1, 0), 7, out _), "contact must be actual lowest opaque row");
        Check(!(model with { Span = new(0, 999) }).TryMap(new(100, 120), sun, 7, out _), "forged calibration rejected");
        ReceiverNativeFrame native = new(new(10, 20), new(50, 20), new(10, 100),
            new(.5f, .5f), new(39.5f, .5f), new(.5f, 79.5f),
            new(.5f, .5f), new(39.5f, 79.5f), new(20, 79.5f), 9);
        Check(ReceiverBillboard.TryCalibrateFrame(N3.Zero, native, out var captured)
            && captured.Span == new ReceiverCaster(0, 20), "actual affine native height calibration");
        Check(ReceiverBillboard.TryCalibrateFrame(N3.Zero,
            native with { TexelA = new(39.5f, .5f), TexelB = new(.5f, .5f), TexelC = new(39.5f, 79.5f) }, out var flipped)
            && flipped.Span == captured.Span, "flipped UVs preserve actual vertical calibration");
        Check(!ReceiverBillboard.TryCalibrateFrame(N3.Zero, native with { C = new(12, 100) }, out _), "unsupported sitting/shear rejects");
        var sampleCaster = new ReceiverCaster(0, 20);
        var vertical = new ReceiverSource(ReceiverSourceKind.Sun, new(0, 0, 10), 512);
        Check(!snapshot.TryProject(key, sampleCaster, new(.5f, .5f, 20), 0, 0, 1, 0, vertical, output, out _), "bound shadow query rejects physical roof");
        var exposed = new ReceiverStructureSnapshot(key, 0, 0, 1, 1);
        Check(exposed.PutCell(0, 0, new(3, 3, 1, 1, false), new ReceiverPhysicalLayer[] { new(1, 0, ReceiverPlaneKind.Floor) }, Array.Empty<ReceiverCover>()), "known exposed producer cell");
        exposed.Seal();
        Check(exposed.TryProject(key, sampleCaster, new(.5f, .5f, 20), 0, 0, 1, 0, vertical, output, out point)
            && Near(point, new(.5f, .5f, 0)), "bound legal shadow keeps positive control");
        Check(ReceiverBillboard.TrySunSource(new(1, 0), ReceiverBillboard.PixelsPerTile / 4, out var calibratedSun)
            && Near(calibratedSun.Value, new(-1, 0, 1)), "native sun-length units and direction conversion");
        Check(!ReceiverBillboard.TrySunSource(new(1, 0), 0, out _), "zero sun length rejected instead of infinite slope");
        return checks;
    }

    private static bool Near(N3 a, N3 b) => N3.DistanceSquared(a, b) < .000001f;
}
