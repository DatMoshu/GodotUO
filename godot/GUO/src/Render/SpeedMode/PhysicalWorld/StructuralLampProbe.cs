// SPDX-License-Identifier: BSD-2-Clause
// Synthetic GPU raster evidence. No shard, native artwork or fabricated images.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Renderer.SpeedMode.Shadows;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class StructuralLampProbe : Node
{
    private readonly List<object> _samples = new();
    private int _checks;
    private ulong _generation;
    public override void _Ready()
    {
        try { Run(); Save(null); GD.Print($"[structural-lamp] PASS checks={_checks} samples={_samples.Count}; backend=gpu-raster-d32"); GetTree().Quit(0); }
        catch (Exception error) { Save(error.ToString()); GD.PrintErr("[structural-lamp] FAIL " + error); GetTree().Quit(1); }
    }

    private void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
    }

    private PhysicalWorldBuilder Builder()
    {
        var b = new PhysicalWorldBuilder(new PhysicalSnapshotKey(31, ++_generation, 0), new PhysicalRect(-8, -8, 16, 16));
        for (int y = -8; y < 8; y++) for (int x = -8; x < 8; x++) b.MarkComplete(x, y);
        return b;
    }

    private static void Quad(PhysicalWorldBuilder b, N3 a, N3 c, N3 d, N3 e, ulong owner = 1)
    {
        var key = new PhysicalOwnerKey(PhysicalSourceKind.Item, owner);
        b.AddTriangle(new PhysicalTriangle(a, c, d, key, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.ArtQualifiedWall));
        b.AddTriangle(new PhysicalTriangle(a, d, e, key, PhysicalMaterial.OpaqueStructure, PhysicalProvenance.ArtQualifiedWall));
    }

    private static void Wall(PhysicalWorldBuilder b, float x, float bottom, float top, ulong owner = 1)
        => Quad(b, new(x, -2, bottom), new(x, 2, bottom), new(x, 2, top), new(x, -2, top), owner);
    private static N3 Native(N3 metric) => new(metric.X, metric.Y, metric.Z / PhysicalLampProjection.ZScale);

    private void Run()
    {
        RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice();
        Check(rd != null, "local raster RenderingDevice is available");
        var pass = new StructuralLampDepthRd();
        try
        {
            var cube = Builder();
            for (int face = 0; face < 6; face++)
            {
                PhysicalLampProjection.Basis(face, out var forward, out var right, out var up);
                N3 middle = forward * 2, r = right * 2.5f, u = up * 2.5f;
                Quad(cube, Native(middle-r-u), Native(middle+r-u), Native(middle+r+u), Native(middle-r+u), (ulong)(face+1));
            }
            var cubeSnapshot = cube.Seal();
            var lamp = new ShadowLamp(N3.Zero, 7, 100);
            var p = PhysicalLampProjection.Create(cubeSnapshot, lamp, new N2(5000, 6000), 3);
            byte[] data = Draw(rd, pass, cubeSnapshot, p);
            for (int face = 0; face < 6; face++)
            {
                PhysicalLampProjection.Basis(face, out var forward, out var right, out var up);
                Sample(data, cubeSnapshot, p, Native(forward * 4 + right * .2f + up * .3f), true, "six-face-" + face);
            }
            Sample(data, cubeSnapshot, p, Native(new(4, 3.999f, .3f)), true, "atlas-seam-x");
            Sample(data, cubeSnapshot, p, Native(new(3.999f, 4, .3f)), true, "atlas-seam-y");
            int uploads = pass.GeometryUploads, draws = pass.DepthDraws;
            Draw(rd, pass, cubeSnapshot, p);
            Check(pass.GeometryUploads == uploads && pass.DepthDraws == draws, "unchanged snapshot/lamp reuses actual GPU depth");

            var low = Builder(); Wall(low, 1, 0, 10);
            var lowSnapshot = low.Seal();
            lamp = new ShadowLamp(new N3(0, 0, 25), 7, 101);
            p = PhysicalLampProjection.Create(lowSnapshot, lamp, N2.Zero, 2);
            Sample(Draw(rd, pass, lowSnapshot, p), lowSnapshot, p, new N3(5, 0, 5), false, "short-first-wall-ray-over-top");
            Wall(low, 3, 0, 30, 2);
            var layered = low.Seal();
            Sample(Draw(rd, pass, layered, p), layered, p, new N3(5, 0, 5), true, "taller-second-wall-still-blocks");

            var beam = Builder(); Wall(beam, 2, 20, 30);
            var beamSnapshot = beam.Seal();
            lamp = new ShadowLamp(new N3(0, 0, 5), 7, 102);
            p = PhysicalLampProjection.Create(beamSnapshot, lamp, N2.Zero, 1);
            Sample(Draw(rd, pass, beamSnapshot, p), beamSnapshot, p, new N3(4, 0, 5), false, "raised-beam-permits-light-beneath");
            p = PhysicalLampProjection.Create(beamSnapshot, lamp with { Position = new N3(0, 0, 25) }, N2.Zero, 1);
            Sample(Draw(rd, pass, beamSnapshot, p), beamSnapshot, p, new N3(4, 0, 25), true, "raised-beam-blocks-at-its-own-height");

            var door = Builder(); Wall(door, 2, 0, 20);
            var closed = door.Seal();
            lamp = new ShadowLamp(new N3(0, 0, 5), 7, 103);
            p = PhysicalLampProjection.Create(closed, lamp, N2.Zero, 0);
            Sample(Draw(rd, pass, closed, p), closed, p, new N3(4, 0, 5), true, "closed-door");
            var open = Builder().Seal();
            uploads = pass.GeometryUploads; draws = pass.DepthDraws;
            Sample(Draw(rd, pass, open, p), open, p, new N3(4, 0, 5), false, "removed-door-first-generation-restores-light");
            Check(pass.SnapshotKey == open.Key && pass.GeometryUploads == uploads + 1 && pass.DepthDraws == draws + 1,
                "door mutation replaces generation and clears previous depth");

            var behind = Builder(); Wall(behind, 4, 0, 20);
            var behindSnapshot = behind.Seal();
            p = PhysicalLampProjection.Create(behindSnapshot, lamp, N2.Zero, 0);
            Sample(Draw(rd, pass, behindSnapshot, p), behindSnapshot, p, new N3(2, 0, 5), false, "blocker-beyond-receiver-does-not-block");
            p = PhysicalLampProjection.Create(closed, lamp with { Position = new N3(3, 0, 5) }, N2.Zero, 0);
            Sample(Draw(rd, pass, closed, p), closed, p, new N3(4, 0, 5), false, "wall-behind-point-source-does-not-block");

            var unknown = Builder(); unknown.MarkUnknown(1, 0, PhysicalUnknownReason.UnsupportedStructure);
            var unknownSnapshot = unknown.Seal();
            p = PhysicalLampProjection.Create(unknownSnapshot, lamp, N2.Zero, 4);
            Draw(rd, pass, unknownSnapshot, p);
            byte[] parameters = rd.BufferGetData(pass.ParametersRid);
            int index = (0-unknownSnapshot.Bounds.Y)*unknownSnapshot.Bounds.Width+4-unknownSnapshot.Bounds.X;
            Check(BitConverter.ToUInt32(parameters, StructuralLampDepthRd.ParameterHeaderBytes + index*4) == 0,
                "missing structural segment publishes unknown coverage, not clear");
#if DEBUG
            draws = pass.DepthDraws;
            StructuralLampDepthRd.ProbeBypass = true;
            Draw(rd, pass, unknownSnapshot, p);
            Check(BitConverter.ToSingle(rd.BufferGetData(pass.ParametersRid), 60) == 0 && pass.DepthDraws == draws,
                "disable writes inactive shader header without redrawing or stale active state");
            StructuralLampDepthRd.ProbeBypass = false;
            Draw(rd, pass, unknownSnapshot, p);
            Check(BitConverter.ToSingle(rd.BufferGetData(pass.ParametersRid), 60) == 1,
                "enable restores shader header immediately");
#endif
            pass.Dispose();
            Check(!pass.Ready && !pass.DepthRid.IsValid && !pass.ParametersRid.IsValid && pass.SnapshotKey == default,
                "dispose revokes all published GPU identities");
            pass.Dispose();
            Draw(rd, pass, open, p);
            Check(pass.Ready && pass.SnapshotKey == open.Key, "retired GPU pass can be recreated for a new admitted ticket");
        }
        finally
        {
#if DEBUG
            StructuralLampDepthRd.ProbeBypass = false;
#endif
            pass.Dispose(); rd.Free();
        }
    }

    private byte[] Draw(RenderingDevice rd, StructuralLampDepthRd pass, PhysicalWorldSnapshot snapshot, PhysicalLampProjection p)
    {
        pass.Render(rd, snapshot, p); rd.Submit(); rd.Sync();
        Check(pass.Ready && pass.SnapshotKey == snapshot.Key, "GPU pass publishes correct immutable snapshot");
        byte[] bytes = rd.TextureGetData(pass.DepthRid, 0);
        Check(bytes.Length == p.FaceResolution*p.FaceResolution*6*4, "actual D32 six-face atlas readback size");
        return bytes;
    }

    private void Sample(byte[] bytes, PhysicalWorldSnapshot snapshot, PhysicalLampProjection p,
        N3 receiver, bool expectedBlocked, string name)
    {
        Check(p.TryProject(receiver, out int face, out N2 uv, out float receiverDepth), "receiver projects: " + name);
        int width = p.FaceResolution*3, height = p.FaceResolution*2;
        int px = Math.Clamp((int)MathF.Floor(uv.X*width), 0, width-1);
        int py = Math.Clamp((int)MathF.Floor(uv.Y*height), 0, height-1);
        float actualDepth = BitConverter.ToSingle(bytes, (py*width+px)*4);
        PhysicalLampProjection.Basis(face, out var forward, out var right, out var up);
        float u = ((px%p.FaceResolution+.5f)/p.FaceResolution*2-1)*p.Tangent;
        float v = ((py%p.FaceResolution+.5f)/p.FaceResolution*2-1)*p.Tangent;
        N3 nativeRay = Native(forward + right*u + up*v);
        float first = Intersect(snapshot, p.Lamp.Position, nativeRay, p.Near, p.Far);
        float oracleDepth = float.IsFinite(first) ? first/p.Far : 1;
        bool actualBlocked = actualDepth < receiverDepth-p.Bias/p.Far;
        bool oracleBlocked = oracleDepth < receiverDepth-p.Bias/p.Far;
        _samples.Add(new { name, face, px, py, actualDepth, oracleDepth, receiverDepth,
            actualBlocked, oracleBlocked, expectedBlocked, generation = snapshot.Key.Generation });
        Check(float.IsFinite(actualDepth) && MathF.Abs(actualDepth-oracleDepth) < .0002f, "GPU raster matches independent ray/triangle depth: " + name);
        Check(oracleBlocked == expectedBlocked && actualBlocked == expectedBlocked, "GPU visibility witness: " + name);
    }

    // Independent Moller-Trumbore triangle intersection along the sampled texel ray.
    // t is dominant-axis physical distance because the direction's major component is1.
    private static float Intersect(PhysicalWorldSnapshot snapshot, N3 origin, N3 ray, float near, float far)
    {
        float first = float.PositiveInfinity;
        foreach (var triangle in snapshot.Triangles.Span)
        {
            if ((triangle.Material & PhysicalMaterial.BlocksLocalLight) == 0) continue;
            N3 e1 = triangle.B-triangle.A, e2 = triangle.C-triangle.A;
            N3 h = N3.Cross(ray,e2); float determinant = N3.Dot(e1,h);
            if (MathF.Abs(determinant) < .000001f) continue;
            N3 s = origin-triangle.A; float inverse = 1/determinant;
            float u = inverse*N3.Dot(s,h);
            if (u < 0 || u > 1) continue;
            N3 q = N3.Cross(s,e1); float v = inverse*N3.Dot(ray,q);
            if (v < 0 || u+v > 1) continue;
            float t = inverse*N3.Dot(e2,q);
            if (t >= near && t < far && t < first) first = t;
        }
        return first;
    }

    private void Save(string failure)
    {
        string root = System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");
        if (string.IsNullOrWhiteSpace(root)) return;
        root = Path.GetFullPath(root);
        string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        if (root.StartsWith(project, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("GPU fixture evidence must be outside project.");
        string folder = Path.Combine(root, "data"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "structural-lamp-gpu-results.json"), JsonSerializer.Serialize(
            new { backend = "gpu-raster-d32", checks = _checks, samples = _samples, failure }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
