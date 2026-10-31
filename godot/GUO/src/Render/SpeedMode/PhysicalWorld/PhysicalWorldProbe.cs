// SPDX-License-Identifier: BSD-2-Clause
// Independent source-contract regression. No shard, art, GPU or scene reads.
using System;
using GUO.Assets;
using GUO.Renderer.SpeedMode.Shadows;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class PhysicalWorldProbe : Godot.Node
{
    public override void _Ready()
    {
        try {
            int checks = Run();
            Godot.GD.Print("[physical-world] PASS checks=" + checks + "; native/runtime=not-tested");
            GetTree().Quit(0);
        } catch (Exception error) {
            Godot.GD.PrintErr("[physical-world] FAIL " + error); GetTree().Quit(1);
        }
    }

    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Physical world: " + name);
            checks++;
        }

        // Native client tent roofs 0x0648..0x064A are NoShoot without the Roof,
        // Wall, Surface or Impassable flags. They must reach the unsupported
        // structural path, rather than let a lamp/weather query certify clear.
        const TileFlag tentRoofFlags = (TileFlag)0x04046000UL;
        Check((tentRoofFlags & (TileFlag.Roof | TileFlag.Wall | TileFlag.Surface | TileFlag.Impassable)) == 0
            && NativePhysicalWorldProducer.RequiresStructuralClassification(tentRoofFlags),
            "NoShoot-only native tent roof requires structural classification instead of clear decoration");
        Check(!NativePhysicalWorldProducer.RequiresStructuralClassification(TileFlag.ArticleA),
            "ordinary decorative metadata does not claim a structural blocker");
        Check(NativePhysicalWorldProducer.RequiresStructuralClassification(TileFlag.Bridge),
            "bridge-only metadata remains unsupported structure rather than clear decoration");

        PhysicalSnapshotKey key = new(7, 11, 0);
        PhysicalRect bounds = new(1415, 1732, 4, 4);
        // The cache must forward an incomplete/truncated source signature as
        // Unknown, without reading more native data or inferring partial roofs.
        var truncated = NativePhysicalWorldProducer.Build(null, key, bounds, enumerationIncomplete: true);
        Check(truncated.Triangles.IsEmpty && truncated.CompleteCellCount == 0 && !truncated.IsComplete,
            "truncated native enumeration cannot publish inferred blockers or clear cells");
        Check((truncated.UnknownAt(bounds.X, bounds.Y) & PhysicalUnknownReason.EnumerationLimit) != 0
            && (truncated.UnknownAt(bounds.Right - 1, bounds.Bottom - 1) & PhysicalUnknownReason.EnumerationLimit) != 0,
            "source enumeration overflow marks the entire bounded domain explicitly unknown");
        var builder = new PhysicalWorldBuilder(key, bounds);
        var missing = builder.Seal();
        Check(!missing.IsComplete && missing.CompleteCellCount == 0, "unvisited cells are unknown");
        Check(missing.UnknownAt(1415, 1732) == PhysicalUnknownReason.MissingNativeBlock,
            "missing source cannot certify empty space");
        Check(missing.UnknownAt(1414, 1732) == PhysicalUnknownReason.Boundary,
            "outside capture cannot certify empty space");
        for (int y = bounds.Y; y < bounds.Bottom; y++)
            for (int x = bounds.X; x < bounds.Right; x++) builder.MarkComplete(x, y);
        PhysicalOwnerKey floorOwner = new(PhysicalSourceKind.Item, 0x40001001);
        PhysicalOwnerKey roofOwner = new(PhysicalSourceKind.Item, 0x40001002);
        PhysicalTriangle floor = new(new(1417, 1734, 0), new(1418, 1734, 0), new(1417, 1735, 0),
            floorOwner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.NativeFloor, 0x0495);
        PhysicalTriangle roof = new(new(1417, 1734, 30), new(1418, 1734, 30), new(1417, 1735, 30),
            roofOwner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.NeighbourRoof, 0x05E4);
        Check(builder.AddTriangle(floor) && builder.AddTriangle(roof), "two native layers admitted");
        var first = builder.Seal(1234);
        Check(first.IsComplete && first.CompleteCellCount == 16 && !first.Overflow,
            "all cells have an explicit complete record");
        Check(first.Key == key && first.SourceFingerprint == 1234, "ticket keeps source and generation identity");
        Check(first.Triangles.Length == 2 && first.Triangles.Span[0] == floor
            && first.Triangles.Span[1] == roof, "two heights preserve separate native owner records");
        Check(first.Max.Z == 30 && first.Min.Z == 0, "bounds include elevated roof");
        Check(first.Triangles.Span[0].Owner != first.Triangles.Span[1].Owner,
            "same XY does not merge physical owners");

        // Renderer cutaway state is deliberately absent from a physical source ticket.
        Check(first.Triangles.Span[1].Material.HasFlag(PhysicalMaterial.BlocksSun)
            && first.Triangles.Span[1].Material.HasFlag(PhysicalMaterial.BlocksLocalLight)
            && first.Triangles.Span[1].Material.HasFlag(PhysicalMaterial.BlocksWeather),
            "a physical roof blocks each admitted source independently of visibility");
        builder.MarkUnknown(1417, 1734, PhysicalUnknownReason.UnsupportedStructure);
        builder.AddTriangle(floor with { A = new(1417, 1734, -20) });
        var second = builder.Seal(5678);
        Check(first.IsComplete && first.Triangles.Length == 2 && first.SourceFingerprint == 1234,
            "later producer changes cannot alter an admitted ticket");
        Check(!second.IsComplete && !second.IsCellComplete(1417, 1734)
            && second.UnknownAt(1417, 1734) == PhysicalUnknownReason.UnsupportedStructure,
            "unsupported structure remains explicit unknown");
        Check(second.IsCellComplete(1416, 1734), "unknown cell does not erase adjacent known evidence");
        Check(!missing.IsCellComplete(1417, 1734), "first missing ticket remains immutable");
        Check(second.Min.Z == -20, "negative native Z is retained");
        Check(!first.IsCellComplete(bounds.Right, bounds.Y) && !first.IsCellComplete(bounds.X, bounds.Bottom),
            "half-open capture bounds");

        var invalid = new PhysicalWorldBuilder(key, new(0, 0, 1, 1));
        invalid.MarkComplete(0, 0);
        Check(!invalid.AddTriangle(floor with { A = new(float.NaN, 0, 0) }), "nonfinite source rejected");
        var invalidTicket = invalid.Seal();
        Check(invalidTicket.Overflow && !invalidTicket.IsComplete && invalidTicket.CompleteCellCount == 0
            && !invalidTicket.IsCellComplete(0, 0), "failed source cannot publish exposed light");

        var capped = new PhysicalWorldBuilder(key, new(0, 0, 1, 1));
        capped.MarkComplete(0, 0);
        for (int i = 0; i < PhysicalWorldBuilder.MaxTriangles; i++)
            if (!capped.AddTriangle(floor)) throw new InvalidOperationException("Physical world: premature triangle cap");
        Check(!capped.AddTriangle(roof), "triangle capacity is bounded");
        var cappedTicket = capped.Seal();
        Check(cappedTicket.Overflow && cappedTicket.Triangles.Length == PhysicalWorldBuilder.MaxTriangles
            && !cappedTicket.IsCellComplete(0, 0), "capacity failure invalidates completeness");
        // Analytic ray witnesses are deliberately independent of the GPU projection.
        // These are geometry-only checks, not claims about native tree reconstruction.
        PhysicalWorldSnapshot Plane(float x, float y, float bottom, float top)
        {
            var b = new PhysicalWorldBuilder(key, new(-4, -4, 12, 12));
            for (int cy = -4; cy < 8; cy++) for (int cx = -4; cx < 8; cx++) b.MarkComplete(cx, cy);
            b.AddTriangle(new(new(x, y, bottom), new(x, y + 1, bottom), new(x, y + 1, top),
                roofOwner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.ArtQualifiedWall));
            b.AddTriangle(new(new(x, y, bottom), new(x, y + 1, top), new(x, y, top),
                roofOwner, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.ArtQualifiedWall));
            return b.Seal();
        }
        var wall = Plane(1, 0, 0, 30);
        var hit = PhysicalWorldOracle.Trace(wall, new(2, .5f, 0), new(0, .5f, 20));
        Check(hit.Known && hit.Blocked && Math.Abs(hit.Fraction - .5f) < .00001f,
            "wall casts onto lower ground at analytic midpoint");
        hit = PhysicalWorldOracle.Trace(wall, new(2, .5f, 40), new(0, .5f, 60));
        Check(hit.Known && !hit.Blocked, "short ground wall cannot shade a higher receiver");
        hit = PhysicalWorldOracle.Trace(Plane(1, 0, 0, 70), new(2, .5f, 40), new(0, .5f, 60));
        Check(hit.Known && hit.Blocked, "tall caster can shade higher receiver");
        hit = PhysicalWorldOracle.Trace(Plane(1, 0, 20, 30), new(2, .5f, 5), new(0, .5f, 5));
        Check(hit.Known && !hit.Blocked, "lamp ray passes underneath raised beam");
        hit = PhysicalWorldOracle.Trace(Plane(1, 0, 0, 20), new(2, .5f, 5), new(0, .5f, 5));
        Check(hit.Known && hit.Blocked, "closed door intercepts point-light segment");
        hit = PhysicalWorldOracle.Trace(Plane(1, 1, 0, 20), new(2, .5f, 5), new(0, .5f, 5));
        Check(hit.Known && !hit.Blocked, "door moved clear opens point-light segment");
        hit = PhysicalWorldOracle.Trace(wall, new(2, .5f, 5), new(1.5f, .5f, 5));
        Check(hit.Known && !hit.Blocked, "geometry beyond a point source does not block it");
        var projection = PhysicalSunProjection.Create(wall, new(0, 0), new(1, 0), 2, .55f, 2048);
        N3 sample = new(2, .5f, 10), alongRay = sample + projection.SourceToSun * 2;
        N3 p = projection.UvDepth(sample), q = projection.UvDepth(alongRay);
        Check(Math.Abs(p.X - q.X) < .00001f && Math.Abs(p.Y - q.Y) < .00001f && q.Z < p.Z,
            "toward-source points retain shadow UV and move nearer in depth");
        float nativeHeight = 20;
        N3 contact = sample - projection.SourceToSun * (sample.Z / projection.SourceToSun.Z);
        Check(Math.Abs(contact.X - (sample.X + sample.Z * PhysicalSunProjection.ZScale * 2)) < .00001f,
            "sun direction preserves native shadow travel sign");
        Check(Math.Abs(nativeHeight * PhysicalSunProjection.ZScale * 2 * 22 * MathF.Sqrt(2) - nativeHeight * 4 * 2) < .0001f,
            "native Z casts legacy four-pixel-per-Z shadow length");
        var movedOrigin = PhysicalSunProjection.Create(wall, new(1417, 1734), new(1, 0), 2, .55f, 2048);
        Check(N3.Distance(projection.UvDepth(sample), movedOrigin.UvDepth(sample)) < .0001f,
            "floating surface origin does not move world shadows");
        Check(projection.DepthBias > 0 && projection.Strength == .55f && projection.Resolution == 2048,
            "depth comparison carries explicit bounded parameters");
        foreach ((int gx, int gy) in new[] { (0, 0), (3, 0), (-3, 0), (0, 4), (0, -4), (6, 0), (0, -6) })
        {
            const float x = .2f, y = .7f, lift = 2.5f;
            float z = gx * x + gy * y;
            N2 pixel = new(22 * (x - y), 22 * (x + y) - 4 * (z + lift));
            Check(PhysicalReceiverRecipe.TryInverseRoof(pixel, gx, gy, lift, out var native)
                && N3.Distance(native, new(x, y, z)) < .00001f,
                "native roof inverse removes only art lift " + gx + "," + gy);
        }
        Check(!PhysicalReceiverRecipe.TryInverseRoof(N2.Zero, 6, 5, 0, out _),
            "near-singular roof projection stays unsupported");
        uint[] diamond = new uint[44 * 44];
        for (int row = 0; row < 44; row++) for (int column = 0; column < 44; column++)
            if (Math.Abs(column + .5f - 22) + Math.Abs(row + .5f - 22) <= 22)
                diamond[row * 44 + column] = uint.MaxValue;
        var fit = PhysicalReceiverRecipe.FitRoofPixels(diamond, 44, 44, 0, 0);
        Check(fit.Qualified && fit.Lift == 0 && fit.Coverage > .99f,
            "complete independently rastered native diamond qualifies at zero lift");
        Check(PhysicalArtRecipe.CheckFloor(new ArtInfo { Pixels = diamond, Width = 44, Height = 44 }, 0),
            "complete native floor diamond qualifies without a guessed interior-count threshold");
        uint[] cropped = new uint[16 * 16]; Array.Fill(cropped, uint.MaxValue);
        Check(!PhysicalReceiverRecipe.FitRoofPixels(cropped, 16, 16, 0, 0).Qualified,
            "opaque cropped patch cannot certify a complete physical roof");
        Check(!PhysicalArtRecipe.CheckFloor(new ArtInfo { Pixels = cropped, Width = 16, Height = 16 }, 0),
            "cropped floor cannot certify uncovered physical corners");
        diamond[22 * 44 + 22] = 0;
        Check(!PhysicalReceiverRecipe.FitRoofPixels(diamond, 44, 44, 0, 0).Qualified,
            "native interior hole does not become opaque roof geometry");
        Check(!PhysicalArtRecipe.CheckFloor(new ArtInfo { Pixels = diamond, Width = 44, Height = 44 }, 0),
            "single interior floor hole remains unsupported");

        ShadowLamp lampSource = new(new(.5f, .5f, 10), 10, 7);
        var lamp = PhysicalLampProjection.Create(wall, lampSource, N2.Zero, 2);
        for (int face = 0; face < 6; face++)
        {
            PhysicalLampProjection.Basis(face, out var forward, out var right, out var up);
            Check(Math.Abs(N3.Dot(forward, right)) < .000001f && Math.Abs(N3.Dot(forward, up)) < .000001f
                && Math.Abs(N3.Dot(right, up)) < .000001f && forward.LengthSquared() == 1
                && right.LengthSquared() == 1 && up.LengthSquared() == 1, "orthonormal lamp face " + face);
            N3 offset = forward * 2; offset.Z /= PhysicalLampProjection.ZScale;
            Check(lamp.TryProject(lampSource.Position + offset, out int selectedFace, out var uv, out float depth)
                && selectedFace == face && N2.Distance(uv, new((face % 3 + .5f) / 3, (face / 3 + .5f) / 2)) < .00001f
                && Math.Abs(depth - 2 / lamp.Far) < .00001f, "six-face lamp centre mapping " + face);
        }
        Check(!lamp.TryProject(lampSource.Position, out _, out _, out _), "lamp near-origin query remains unknown");
        Check(!lamp.TryProject(lampSource.Position + new N3(lamp.Far, 0, 0), out _, out _, out _),
            "lamp far-plane endpoint remains outside depth proof");
        Check(!lamp.TryProject(new(float.NaN, 0, 0), out _, out _, out _), "nonfinite lamp receiver rejected");
        Check(lamp.TryProject(lampSource.Position + new N3(2, 1.9999f, 0), out int edgeX, out _, out _)
            && lamp.TryProject(lampSource.Position + new N3(2, 2.0001f, 0), out int edgeY, out _, out _)
            && edgeX == 0 && edgeY == 2, "cube seam selects adjacent dominant-axis faces");
        var movedLampOrigin = PhysicalLampProjection.Create(wall, lampSource, new(1417, 1734), 2);
        Check(lamp.TryProject(new(2, 2, 12), out int originalFace, out var originalUv, out float originalDepth)
            && movedLampOrigin.TryProject(new(2, 2, 12), out int shiftedFace, out var shiftedUv, out float shiftedDepth)
            && originalFace == shiftedFace && originalUv == shiftedUv && originalDepth == shiftedDepth,
            "camera origin does not move native lamp shadows");
        var finite = new PhysicalWorldBuilder(key, new(0, 0, 4, 4));
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) finite.MarkComplete(x, y);
        var finiteSnapshot = finite.Seal();
        var finiteLamp = PhysicalLampProjection.Create(finiteSnapshot, new(new(1.5f, 1.5f, 10), 10, 0), N2.Zero, 0);
        uint[] known = new uint[16];
        Check(finiteLamp.BuildKnownReceiverCells(finiteSnapshot, known) == 16,
            "finite complete lamp domain can certify every receiver cell");
        finite.MarkUnknown(2, 2, PhysicalUnknownReason.UnsupportedStructure);
        var interrupted = finite.Seal();
        finiteLamp.BuildKnownReceiverCells(interrupted, known);
        Check(known[3 * 4 + 3] == 0 && known[1 * 4] == 1,
            "unknown lamp corridor fails only affected finite segments");
        finite.MarkUnknown(1, 1, PhysicalUnknownReason.OpticalOpening);
        Check(finiteLamp.BuildKnownReceiverCells(finite.Seal(), known) == 0,
            "unknown lamp source cell cannot certify clear visibility");
        return checks;
    }
}

// Slow diagnostic oracle. It reads the producer's immutable triangles, never draws
// or modifies runtime receivers, and refuses exposure claims through unknown cells.
internal static class PhysicalWorldOracle
{
    internal readonly record struct Result(bool Known, bool Blocked, PhysicalOwnerKey Owner, float Fraction);

    internal static Result Trace(PhysicalWorldSnapshot snapshot, N3 start, N3 end,
        PhysicalMaterial blocks = PhysicalMaterial.BlocksSun)
    {
        N3 ray = end - start;
        bool known = snapshot != null && !snapshot.Overflow && Finite(start) && Finite(end);
        if (!known) return default;
        int left = (int)MathF.Floor(MathF.Min(start.X, end.X));
        int right = (int)MathF.Floor(MathF.Max(start.X, end.X));
        int top = (int)MathF.Floor(MathF.Min(start.Y, end.Y));
        int bottom = (int)MathF.Floor(MathF.Max(start.Y, end.Y));
        // Include a neighbouring cell for a ray exactly on an integer edge.
        if (start.X == end.X && start.X == MathF.Floor(start.X)) left--;
        if (start.Y == end.Y && start.Y == MathF.Floor(start.Y)) top--;
        if (right - left > 128 || bottom - top > 128) return default;
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            float enter = 0, leave = 1;
            if (Slab(start.X, ray.X, x, x + 1, ref enter, ref leave)
                && Slab(start.Y, ray.Y, y, y + 1, ref enter, ref leave)
                && !snapshot.IsCellComplete(x, y)) known = false;
        }
        float first = float.PositiveInfinity;
        PhysicalOwnerKey owner = default;
        foreach (PhysicalTriangle triangle in snapshot.Triangles.Span)
        {
            if ((triangle.Material & blocks) == 0) continue;
            N3 e1 = triangle.B - triangle.A, e2 = triangle.C - triangle.A;
            N3 cross = N3.Cross(ray, e2);
            float determinant = N3.Dot(e1, cross);
            if (Math.Abs(determinant) < .000001f) continue;
            float inverse = 1 / determinant;
            N3 offset = start - triangle.A;
            float u = N3.Dot(offset, cross) * inverse;
            if (u < 0 || u > 1) continue;
            N3 q = N3.Cross(offset, e1);
            float v = N3.Dot(ray, q) * inverse;
            if (v < 0 || u + v > 1) continue;
            float t = N3.Dot(e2, q) * inverse;
            // Avoid self hits without dropping a distant roof or a thin wall.
            if (t > .0001f && t < 1 && t < first) { first = t; owner = triangle.Owner; }
        }
        return new(known, float.IsFinite(first), owner, first);
    }

    private static bool Finite(N3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static bool Slab(float p, float d, float low, float high, ref float enter, ref float leave)
    {
        if (d == 0) return p >= low && p <= high;
        float a = (low - p) / d, b = (high - p) / d;
        if (a > b) (a, b) = (b, a);
        enter = MathF.Max(enter, a); leave = MathF.Min(leave, b);
        return enter <= leave;
    }
}
