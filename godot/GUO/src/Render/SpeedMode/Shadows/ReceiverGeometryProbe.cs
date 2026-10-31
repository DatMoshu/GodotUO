// SPDX-License-Identifier: BSD-2-Clause
// Task1053 frozen source-only regression. No Godot, art, shard or scene reads.
using System;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal static class ReceiverGeometryProbe
{
    // Lead may invoke from an admitted probe harness after contract review.
    // No shared probe registration or scene entry point is changed here.
    internal static int Run()
    {
        int checks = 0;
        ReceiverCover[] none = Array.Empty<ReceiverCover>();
        ReceiverSource sun = new(ReceiverSourceKind.Sun, new N3(-1, 0, 10), 100);
        ReceiverPatch Floor(float z, uint layer = 1) => new(new(-20, -20), new(20, 20), z, layer, ReceiverPlaneKind.Floor);
        ReceiverCaster ground = new(0, 20), deck = new(30, 50), tree = new(0, 60);
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Receiver geometry: " + name);
            checks++;
        }
        bool Project(ReceiverCaster caster, N3 sample, ReceiverPatch receiver,
            ReceiverSource source, ReceiverCover[] covers, out N3 hit) =>
            ReceiverGeometry.TryProject(caster, sample, receiver, source, covers, true, out hit);

        // Analytic oracle: 20Z / 10Z-per-tile -> +2 tiles along X.
        Check(Project(ground, new(0, 0, 20), Floor(0), sun, none, out var p) && Near(p, new(2, 0, 0)), "exposed ground");
        Check(Project(deck, new(0, 0, 50), Floor(30, 2), sun, none, out p) && Near(p, new(2, 0, 30)), "upper deck");
        Check(Project(deck, new(0, 0, 50), Floor(0), sun, none, out p) && Near(p, new(5, 0, 0)), "elevated caster to lower ground");
        var roof = Floor(30, 2) with { Kind = ReceiverPlaneKind.Roof };
        Check(Project(tree, new(0, 0, 60), roof, sun, none, out p) && Near(p, new(3, 0, 30)), "tall caster to higher roof");
        Check(!Project(ground, new(0, 0, 20), roof, sun, none, out _), "short caster cannot shade higher roof");
        Check(!Project(tree, new(0, 0, 10), roof, sun, none, out _), "low alpha sample rejected even with tall top");

        ReceiverCover[] ceiling = { new(new(-10, -10, 30), new(10, 10, 32)) };
        Check(!Project(ground, new(0, 0, 20), Floor(0), sun, ceiling, out _), "indoor caster cannot see sun through physical roof");
        Check(!Project(tree, new(0, 0, 60), Floor(0), sun, ceiling, out _), "roof between caster and ground");
        // The same physical snapshot is used for visible and hidden roofs.
        for (int cutaway = 0; cutaway < 2; cutaway++)
        {
            Check(ReceiverGeometry.TrySheltered(new(0, 0, 0), 100, ceiling, true, out var sheltered) && sheltered, "cutaway shelter invariant");
            Check(!Project(ground, new(0, 0, 20), Floor(0), sun, ceiling, out _), "cutaway shadow invariant");
        }
        Check(Project(tree, new(0, 0, 60), roof with { Z = 32 }, sun, ceiling, out _), "roof top remains exposed to tall caster");
        ReceiverCover[] porch = { new(new(0, -1, 25), new(1, 1, 26)) };
        Check(ReceiverGeometry.TrySheltered(new(.5f, 0, 0), 100, porch, true, out var under) && under, "porch protected");
        Check(ReceiverGeometry.TrySheltered(new(2, 0, 0), 100, porch, true, out under) && !under, "paving outside porch exposed");
        ReceiverCover[] wall = { new(new(.9f, -1, 0), new(1.1f, 1, 40)) };
        Check(!Project(ground, new(0, 0, 20), Floor(0), sun, wall, out _), "wall crosses receiver-source ray");

        var lamp = new ReceiverSource(ReceiverSourceKind.Point, new(-2, 0, 40));
        Check(Project(ground, new(0, 0, 20), Floor(0), lamp, none, out p) && Near(p, new(2, 0, 0)), "point-light analytic projection");
        Check(Project(ground, new(0, 0, 20), Floor(0), lamp with { Value = new(2, 0, 40) }, none, out p)
            && Near(p, new(-2, 0, 0)), "lamp moves to opposite side");
        Check(!Project(ground, new(0, 0, 20), Floor(0), lamp with { Value = new(0, 0, 20) }, none, out _), "horizontal ray rejected");
        Check(!Project(ground, new(0, 0, 20), Floor(0), lamp with { Value = new(0, 0, 10) }, none, out _), "upward projection unsupported");

        // Same screen stamp can contain legal and illegal receiver layers.
        // No single merged caster-base channel can preserve this distinction.
        Check(!Project(ground, new(0, 0, 20), roof, sun, none, out _)
            && Project(tree, new(0, 0, 60), roof, sun, none, out _), "overlapping casters evaluated separately");
        Check(!Project(ground, new(0, 0, 20), Floor(0) with { Min = new(3, -1) }, sun, none, out _), "finite receiver bounds");
        Check(!Project(ground, new(0, 0, 20), Floor(0) with { Max = new(2, 1) }, sun, none, out _), "half-open receiver edge");
        Check(!Project(ground, new(0, 0, 20), Floor(0) with { Kind = ReceiverPlaneKind.Unknown }, sun, none, out _), "wall/slope/unknown plane rejected");
        Check(!Project(ground, new(0, 0, 20), Floor(0, 0), sun, none, out _), "missing layer");
        Check(!ReceiverGeometry.TryProject(ground, new(0, 0, 20), Floor(0), sun, none, false, out _), "incomplete cover");
        Check(!ReceiverGeometry.TrySheltered(new(0, 0, 0), 100, none, false, out _), "unknown shelter not exposed");
        Check(!Project(ground, new(0, 0, 21), Floor(0), sun, none, out _), "sample outside caster span");
        Check(!Project(ground, new(float.NaN, 0, 20), Floor(0), sun, none, out _), "nonfinite sample");
        Check(!Project(ground, new(0, 0, 20), Floor(0), default, none, out _), "uninitialized source");
        ReceiverCover[] invalid = { new(new(1, 0, 0), new(0, 1, 30)) };
        Check(!Project(ground, new(0, 0, 20), Floor(0), sun, invalid, out _), "invalid physical cover");
        Check(!Project(ground, new(0, 0, 20), Floor(0), sun with { SkyZ = 31 }, ceiling, out _), "unproven sky ceiling rejected");
        ReceiverCover[] stacked = {
            new(new(-10, -10, 30), new(10, 10, 32)),
            new(new(-10, -10, 60), new(10, 10, 62))
        };
        Check(ReceiverGeometry.TrySheltered(new(0, 0, 32), 100, stacked, true, out under) && under, "upper interior still sheltered");
        Check(ReceiverGeometry.TrySheltered(new(0, 0, 62), 100, stacked, true, out under) && !under, "highest roof top exposed");
        ReceiverCover[] behindLamp = { new(new(-3, -1, 45), new(-2, 1, 50)) };
        Check(Project(ground, new(0, 0, 20), Floor(0), lamp, behindLamp, out _), "cover beyond point source does not block");
        ReceiverCover[] nearReceiver = { new(new(-10, -1, .0001f), new(10, 1, .0002f)) };
        Check(!Project(ground, new(0, 0, 20), Floor(0), sun, nearReceiver, out _), "thin cover near endpoint retained");
        // Exact shared weather1055 V01-V20 reference, not another weather helper.
        ReceiverCover a = new(new(0, 0, 30), new(4, 4, 32));
        ReceiverCover b = new(new(0, 0, 60), new(4, 4, 62));
        ReceiverCover porchP = new(new(4, 1, 25), new(5, 3, 26));
        ReceiverCover wallW = new(new(4, 0, 0), new(4.125f, 4, 40));
        ReceiverCover plane = new(new(0, 0, 30), new(4, 4, 30));
        ReceiverCover[] onlyA = { a }, both = { a, b }, aPorch = { a, porchP }, aWall = { a, wallW }, onlyPlane = { plane };
        (string Id, ReceiverCover[] Covers, N3 Point, float Sky, bool Complete, bool Success, bool Sheltered)[] vectors = {
            ("V01", onlyA, new(2,2,0), 100, true, true, true),
            ("V02", onlyA, new(2,2,12), 100, true, true, true),
            ("V03", onlyA, new(2,2,20), 100, true, true, true),
            ("V04", onlyA, new(2,2,32), 100, true, true, false),
            ("V05", onlyA, new(2,2,30), 100, true, true, true),
            ("V06", onlyA, new(4,2,0), 100, true, true, true),
            ("V07", onlyA, new(4.125f,2,0), 100, true, true, false),
            ("V08", aPorch, new(4.5f,2,0), 100, true, true, true),
            ("V09", aPorch, new(5.125f,2,0), 100, true, true, false),
            ("V10", both, new(2,2,32), 100, true, true, true),
            ("V11", both, new(2,2,62), 100, true, true, false),
            ("V12", none, new(2,2,0), 100, true, true, false),
            ("V13", none, new(2,2,0), 100, false, false, false),
            ("V14", onlyA, new(2,2,0), 100, false, false, false),
            ("V15", onlyA, new(2,2,0), 31, true, false, false),
            ("V16", onlyA, new(float.NaN,2,0), 100, true, false, false),
            ("V17", aWall, new(4.0625f,2,20), 100, true, true, true),
            ("V18", aWall, new(4.25f,2,20), 100, true, true, false),
            ("V19", onlyPlane, new(2,2,0), 100, true, true, true),
            ("V20", onlyPlane, new(2,2,30), 100, true, true, false)
        };
        foreach (var vector in vectors)
        {
            bool success = ReceiverGeometry.TrySheltered(vector.Point, vector.Sky, vector.Covers, vector.Complete, out bool result);
            Check(success == vector.Success && result == vector.Sheltered, "weather shared " + vector.Id);
        }
        ReceiverCover face = new(new(4, 0, 0), new(4, 4, 40));
        ReceiverCover[] exactFace = { face };
        Check(ReceiverGeometry.ValidCover(face), "zero-thickness axis wall face valid");
        Check(ReceiverGeometry.ValidCover(new(new(0,4,0), new(4,4,40))), "zero-thickness Y wall face valid");
        Check(ReceiverGeometry.TrySheltered(new(4,2,20), 100, exactFace, true, out under) && under, "exact X4 face sheltered");
        Check(ReceiverGeometry.TrySheltered(new(4.125f,2,20), 100, exactFace, true, out under) && !under, "nearby paving positive");
        Check(ReceiverGeometry.TrySheltered(new(4,0,20), 100, exactFace, true, out under) && under, "closed wall corner tangent");
        Check(ReceiverGeometry.TrySheltered(new(4,2,40), 100, exactFace, true, out under) && !under, "own wall top origin exposed");
        Check(!Project(ground, new(4,4,10), Floor(0), sun, exactFace, out _), "angled ray tangent to closed wall end");
        Check(Project(ground, new(4,4.125f,10), Floor(0), sun, exactFace, out _), "angled ray beyond wall end positive");
        ReceiverCover line = new(new(4,0,0), new(4,0,40)), pointCover = new(new(4,0,0), new(4,0,0));
        Check(!ReceiverGeometry.ValidCover(line) && !ReceiverGeometry.ValidCover(pointCover), "line and point are invalid structure");
        ReceiverCover[] invalidAfterHit = { face, line };
        Check(!ReceiverGeometry.TrySheltered(new(4,2,20), 100, invalidAfterHit, true, out under) && !under,
            "invalid late cover preserves unknown outfalse after earlier hit");
        return checks;
    }

    private static bool Near(N3 a, N3 b) => N3.DistanceSquared(a, b) < 0.000001f;
}
