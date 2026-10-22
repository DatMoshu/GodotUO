// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

/// <summary>
/// Synthetic exhaustive depth precision test. No shard, client data or client
/// switch. The CPU and actual Forward+ depth test each visit every adjacent
/// pair in the full 28-bit domain, including all rank/page and exponent boundaries.
/// </summary>
internal static class DepthKeyProbe
{
    private const int Width = 2048;
    private const uint Pixels = Width * Width;
    private sealed record Sweep(int Encoding, bool CrossPage, bool NearFirst,
        ulong Pairs, ulong WrongPixels, double ElapsedSeconds);

    public static async Task<bool> Run(Node parent)
    {
        if (RenderingServer.GetCurrentRenderingMethod() != "forward_plus")
        {
            GD.Print("[depth-key] exact 28-bit contract requires Forward+; NOT RUN");
            return false;
        }

        var clock = Stopwatch.StartNew();
        bool cpu = await Task.Run(() =>
        {
            float previous = WorldDepthKey.Encode(0);
            for (uint key = 1; key < WorldDepthKey.Count; key++)
            {
                float next = WorldDepthKey.Encode(key);
                if (!(previous < next) || !(next < 1f)
                    || BitConverter.SingleToUInt32Bits(next) - WorldDepthKey.FirstFloatBits != key)
                    return false;
                previous = next;
            }
            return true;
        });
        GD.Print($"[depth-key] CPU full domain: {(cpu ? "PASS" : "FAIL")},"
            + $" {WorldDepthKey.Count - 1} adjacent pairs, {clock.Elapsed.TotalSeconds:0.000}s");

        var results = new List<Sweep>();
        for (int encoding = 0; encoding < 2; encoding++)
        {
            foreach (bool crossPage in new[] { false, true })
            foreach (bool nearFirst in new[] { false, true })
            {
                using var sweep = new HardwareSweep(parent, crossPage, nearFirst, encoding);
                ulong wrong = 0;
                ulong pairs = 0;
                clock.Restart();
                for (uint first = 0; first < WorldDepthKey.Count - 1; first += Pixels)
                {
                    sweep.SetFirst(first);
                    // Two actual frames, as the original depth probe uses.
                    for (int frame = 0; frame < 2; frame++)
                        await parent.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);

                    using Image image = sweep.Viewport.GetTexture().GetImage();
                    image.Convert(Image.Format.Rgba8);
                    byte[] rgba = image.GetData();
                    // Only the last pixel of the last batch is outside the domain.
                    ulong batchWrong = 0;
                    uint visible = 0;
                    for (int pixel = 0; pixel < Pixels; pixel++)
                    {
                        int at = pixel * 4;
                        if (rgba[at + 3] == 0) continue;
                        visible++;
                        if (rgba[at] != 255 || rgba[at + 1] != (first >> 22) * 4
                            || rgba[at + 2] != (pixel & 255) || rgba[at + 3] != 255)
                            batchWrong++;
                    }
                    if (first == 0)
                        Save(image, $"encoding-{encoding}-{(crossPage ? "two-pages" : "one-page")}-{(nearFirst ? "near-first" : "far-first")}");
                    wrong += batchWrong;
                    pairs += visible;
                    if ((first / Pixels) % 16 == 15)
                        GD.Print($"[depth-key] progress scheme {encoding}, pages {(crossPage ? 2 : 1)},"
                            + $" near-first {nearFirst}: {pairs} pairs, {wrong} wrong");
                }
                var result = new Sweep(encoding, crossPage, nearFirst, pairs, wrong, clock.Elapsed.TotalSeconds);
                results.Add(result);
                GD.Print($"[depth-key] result {JsonSerializer.Serialize(result)}");
                // QueueFree must complete before the next viewport exists.
                sweep.Dispose();
                await parent.ToSignal(parent.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        bool exact = cpu && results.TrueForAll(r => r.Encoding != 0 || (r.WrongPixels == 0 && r.Pairs == WorldDepthKey.Count - 1));
        // A two-level scheme requires sorted instances within each page; the
        // reversed same-page run is a deliberate negative control. Both cross-
        // page assignments must still work without a global page ordering.
        bool coarse = results.TrueForAll(r => r.Encoding != 1 || (!r.CrossPage && r.NearFirst)
            || (r.WrongPixels == 0 && r.Pairs == WorldDepthKey.Count - 1));
        GD.Print($"[depth-key] exact encoding {(exact ? "PASS" : "FAIL")}; two-level/page-local sorting {(coarse ? "PASS" : "FAIL")}");
        string folder = System.Environment.GetEnvironmentVariable("GUO_DEPTH_PROBE_DUMP");
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "depth-key-results.json"), JsonSerializer.Serialize(new
            {
                Renderer = RenderingServer.GetCurrentRenderingMethod(), Driver = RenderingServer.GetCurrentRenderingDriverName(),
                Adapter = RenderingServer.GetVideoAdapterName(), CpuExact = cpu, Exact = exact, TwoLevel = coarse, Sweeps = results,
                Timing = "Probe wall time including CPU readback/vsync, not a per-instance performance benchmark",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return exact;
    }

    private static void Save(Image image, string subject)
    {
        string folder = System.Environment.GetEnvironmentVariable("GUO_DEPTH_PROBE_DUMP");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        image.SavePng(Path.Combine(folder, $"guo_GPU-00_depth-key-{subject}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png"));
    }

    private sealed class HardwareSweep : IDisposable
    {
        public SubViewport Viewport { get; }
        private readonly List<ShaderMaterial> _materials = new();
        private readonly List<MultiMesh> _meshes = new();
        private readonly List<ImageTexture> _textures = new();
        private readonly ArrayMesh _quad;
        private bool _disposed;

        public HardwareSweep(Node parent, bool crossPage, bool nearFirst, int encoding)
        {
            Viewport = new SubViewport
            {
                Size = new Vector2I(Width, Width), OwnWorld3D = true, TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Msaa3D = Godot.Viewport.Msaa.Disabled, ScreenSpaceAA = Godot.Viewport.ScreenSpaceAAEnum.Disabled,
                UseTaa = false, Scaling3DScale = 1f,
            };
            Viewport.AddChild(new Camera3D
            {
                Current = true, Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.ClearColor, TonemapMode = Godot.Environment.ToneMapper.Linear,
                },
            });
            parent.AddChild(Viewport);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[]
            {
                new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0),
            };
            arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
            _quad = new ArrayMesh();
            _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            Shader shader = GD.Load<Shader>("res://src/Bootstrap/depth_key_probe.gdshader");
            int pages = crossPage ? 2 : 1;
            for (int page = 0; page < pages; page++)
            {
                using Image art = Image.CreateEmpty(2, 1, false, Image.Format.Rgba8);
                art.SetPixel(0, 0, new Color(1, 0, 0, 1));
                art.SetPixel(1, 0, new Color(0, 1, 0, 1));
                var texture = ImageTexture.CreateFromImage(art);
                _textures.Add(texture);
                var material = new ShaderMaterial { Shader = shader };
                material.SetShaderParameter("encoding", encoding);
                material.SetShaderParameter("width", Width);
                material.SetShaderParameter("page", texture);
                _materials.Add(material);
                var mesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = _quad, InstanceCount = crossPage ? 1 : 2,
                    CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)),
                };
                _meshes.Add(mesh);
                var buffer = new float[mesh.InstanceCount * 12];
                for (int i = 0; i < mesh.InstanceCount; i++)
                {
                    buffer[i * 12] = buffer[i * 12 + 5] = buffer[i * 12 + 10] = 1f;
                    int ordinal = crossPage ? page : i;
                    buffer[i * 12 + 11] = nearFirst ? 1 - ordinal : ordinal;
                }
                RenderingServer.MultimeshSetBuffer(mesh.GetRid(), buffer);
                Viewport.AddChild(new MultiMeshInstance3D
                {
                    Multimesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }

        public void SetFirst(uint first)
        {
            foreach (ShaderMaterial material in _materials)
                material.SetShaderParameter("first_key", (int)first);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Viewport.QueueFree();
            foreach (MultiMesh mesh in _meshes) mesh.Dispose();
            foreach (ShaderMaterial material in _materials) material.Dispose();
            foreach (ImageTexture texture in _textures) texture.Dispose();
            _quad.Dispose();
        }
    }
}
