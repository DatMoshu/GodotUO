// SPDX-License-Identifier: BSD-2-Clause
// Independent GPU receipts for the shipped receiver-aware soften shader.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Renderer.DepthWorld;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class ReceiverSoftenProbe : Node
{
    private readonly List<object> _rows = new();
    private int _checks;
    private static readonly float[] Dark = { .125f, .0625f, .03125f, .25f };
    private static readonly float[] Bright = { 4, 2, 1, .75f };

    public override void _Ready()
    {
        try
        {
            Run(); Save(null);
            GD.Print($"[receiver-soften] PASS checks={_checks}; backend=gpu-raster-half");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            Save(error.ToString()); GD.PrintErr("[receiver-soften] FAIL " + error); GetTree().Quit(1);
        }
    }

    private void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
        _checks++;
    }

    private void Run()
    {
        RD rd = RenderingServer.CreateLocalRenderingDevice();
        Check(rd != null, "local GPU raster device available");
        try
        {
            using var raster = new Raster(rd, Check);
            _rows.Add(new { shader = "shipped soften_fragment.glsl.txt", fragmentSha256 =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raster.Fragment))) });
            foreach (int axis in new[] { 0, 1 })
            {
                var size = axis == 0 ? new Vector2I(9, 3) : new Vector2I(3, 9);
                foreach (string edge in new[] { "z", "x", "y", "confidence", "class", "cover" })
                    VerifyBoundary(raster, size, axis, edge, 3);
                VerifyBoundary(raster, size, axis, "z", 10);
                VerifyBoundary(raster, size, axis, "x", 10);
                foreach (int radius in new[] { 1, 3, 10 })
                    VerifyContinuous(raster, size, axis, "flat", radius);
                // Every adjacent slope step is 0.25 Z, while the total reach
                // exceeds 0.5 Z. This distinguishes adjacent from centre tests.
                VerifyContinuous(raster, size, axis, "slope", 3);
                VerifyContinuous(raster, size, axis, "auxiliary-bit", 3);
                MakeStrip(size, axis, "flat", out float[] light, out float[] surface);
                byte[] input = HalfBytes(light);
                byte[] zero = raster.Draw(size, input, HalfBytes(surface), axis, 0);
                Check(zero.AsSpan().SequenceEqual(input), "radius zero is bit-exact on axis " + axis);
            }
            VerifyTwoAxes(raster, false);
            VerifyTwoAxes(raster, true);
        }
        finally { rd.Free(); }
    }

    private void VerifyBoundary(Raster raster, Vector2I size, int axis, string edge, int radius)
    {
        MakeStrip(size, axis, edge, out float[] light, out float[] surface);
        byte[] input = HalfBytes(light);
        byte[] receivers = HalfBytes(surface);
        byte[] result = raster.Draw(size, input, receivers, axis, radius);
        VerifyBoundaryResult(raster, size, light, receivers, result, axis, edge, radius);
    }

    private void VerifyBoundaryResult(Raster raster, Vector2I size, float[] light, byte[] receivers,
        byte[] result, int axis, string edge, int radius, bool bothAxes = false)
    {
        byte[] input = HalfBytes(light);
        bool exact = result.AsSpan().SequenceEqual(input);
        // A uniform control has the exact same receiver edges and weighted
        // divisions, but no other light value that could cross the edge.
        // Soften GPU02 confirmed one-half-ULP conversion error and bit-exact
        // control agreement. Allow that measured precision, never a leak epsilon.
        var dark = new float[light.Length]; var bright = new float[light.Length];
        for (int i = 0; i < light.Length; i += 4)
        {
            Array.Copy(Dark, 0, dark, i, 4); Array.Copy(Bright, 0, bright, i, 4);
        }
        byte[] darkResult = raster.Draw(size, HalfBytes(dark), receivers, axis, radius, bothAxes);
        byte[] brightResult = raster.Draw(size, HalfBytes(bright), receivers, axis, radius, bothAxes);
        var isolated = new byte[input.Length];
        for (int pixel = 0; pixel < size.X * size.Y; pixel++)
            (light[pixel * 4] == Bright[0] ? brightResult : darkResult)
                .AsSpan(pixel * 8, 8).CopyTo(isolated.AsSpan(pixel * 8, 8));
        bool controlExact = result.AsSpan().SequenceEqual(isolated);
        var differences = DifferenceReceipt(result, input, size);
        _rows.Add(new { test = "boundary", axis, edge, radius, bothAxes, exact, differences,
            uniformControls = new { exact = controlExact, differences = DifferenceReceipt(result, isolated, size) },
            numericTolerance = new { maxHalfUlps = 1, controlComparison = "bit-exact" } });
        Check(controlExact,
            $"no light or alpha transfer across {edge} boundary, axis {axis}, radius {radius}");
        Check(differences.maxHalfUlps <= 1,
            $"constant-region result stays within one half ULP, {edge}, axis {axis}, radius {radius}");
    }

    private sealed record DifferenceSummary(int differingComponents, float maxAbsoluteError,
        int maxHalfUlps, List<object> firstDifferences);

    private static DifferenceSummary DifferenceReceipt(byte[] actual, byte[] expected, Vector2I size)
    {
        var firstDifferences = new List<object>();
        int differing = 0, maxHalfUlps = 0; float maxAbsolute = 0;
        for (int component = 0; component < actual.Length / 2; component++)
        {
            ushort gotBits = BitConverter.ToUInt16(actual, component * 2);
            ushort expectedBits = BitConverter.ToUInt16(expected, component * 2);
            if (gotBits == expectedBits) continue;
            differing++;
            float got = (float)BitConverter.UInt16BitsToHalf(gotBits);
            float wanted = (float)BitConverter.UInt16BitsToHalf(expectedBits);
            int ulps = Math.Abs((int)gotBits - expectedBits); // fixture values are positive finite halfs
            maxHalfUlps = Math.Max(maxHalfUlps, ulps);
            maxAbsolute = Math.Max(maxAbsolute, Math.Abs(got - wanted));
            if (firstDifferences.Count < 16)
            {
                int pixel = component / 4;
                firstDifferences.Add(new { x = pixel % size.X, y = pixel / size.X, channel = component % 4,
                    actual = got, expected = wanted, actualBits = gotBits, expectedBits, halfUlps = ulps });
            }
        }
        return new(differing, maxAbsolute, maxHalfUlps, firstDifferences);
    }

    private void VerifyContinuous(Raster raster, Vector2I size, int axis, string mode, int radius)
    {
        MakeStrip(size, axis, mode, out float[] light, out float[] surface);
        byte[] result = raster.Draw(size, HalfBytes(light), HalfBytes(surface), axis, radius);
        // At the last dark pixel all positive-side taps see bright light,
        // including clamped edge taps: r*(r+1)/2 out of (r+1)^2 weight.
        // At the first bright pixel the complementary fraction is bright.
        float fraction = radius / (2f * (radius + 1));
        for (int side = 0; side < 2; side++)
        {
            int pixel = axis == 0 ? size.X + 3 + side : (3 + side) * size.X + 1;
            VerifyMix(result, pixel, side == 0 ? fraction : 1 - fraction,
                $"continuous {mode}, axis {axis}, radius {radius}, side {side}");
        }
        _rows.Add(new { test = "continuous", axis, mode, radius, lastDarkBrightFraction = fraction });
    }

    private static void MakeStrip(Vector2I size, int axis, string mode, out float[] light, out float[] surface)
    {
        light = new float[size.X * size.Y * 4]; surface = new float[light.Length];
        for (int y = 0; y < size.Y; y++)
        for (int x = 0; x < size.X; x++)
        {
            int p = (y * size.X + x) * 4, along = axis == 0 ? x : y;
            bool bright = along >= 4;
            Array.Copy(bright ? Bright : Dark, 0, light, p, 4);
            surface[p] = x / 16f; surface[p + 1] = y / 16f;
            surface[p + 2] = 18 + (mode == "slope" ? along * .25f : 0);
            surface[p + 3] = 257; // native flat class plus physical-confidence bit
            if (!bright) continue;
            switch (mode)
            {
                case "z": surface[p + 2] += 1; break;
                case "x": surface[p] += 1; break;
                case "y": surface[p + 1] += 1; break;
                case "confidence": surface[p + 3] = 1; break;
                case "class": surface[p + 3] = 258; break;
                case "cover": surface[p + 3] = 273; break;
                case "auxiliary-bit": surface[p + 3] = 289; break;
            }
        }
    }

    private void VerifyTwoAxes(Raster raster, bool boundary)
    {
        var size = new Vector2I(9, 9);
        var light = new float[9 * 9 * 4]; var surface = new float[light.Length];
        for (int y = 0; y < 9; y++)
        for (int x = 0; x < 9; x++)
        {
            int p = (y * 9 + x) * 4; bool bright = x >= 4 && y >= 4;
            Array.Copy(bright ? Bright : Dark, 0, light, p, 4);
            surface[p] = x / 16f; surface[p + 1] = y / 16f;
            surface[p + 2] = boundary && bright ? 19 : 18; surface[p + 3] = 257;
        }
        byte[] input = HalfBytes(light);
        byte[] receivers = HalfBytes(surface);
        byte[] result = raster.Draw(size, input, receivers, 0, 3, bothAxes: true);
        if (boundary)
            VerifyBoundaryResult(raster, size, light, receivers, result, 0, "two-pass-z", 3, bothAxes: true);
        else
        {
            // Separable radius-three triangular weights have 6/16 and 10/16
            // bright fractions at the two edge pixels; the quadrant multiplies them.
            VerifyMix(result, 3 * 9 + 3, 36f / 256, "two-pass continuous dark corner");
            VerifyMix(result, 4 * 9 + 4, 100f / 256, "two-pass continuous bright corner");
        }
        _rows.Add(new { test = "horizontal-then-vertical", boundary });
    }

    private void VerifyMix(byte[] bytes, int pixel, float brightFraction, string reason)
    {
        for (int channel = 0; channel < 4; channel++)
        {
            float expected = Dark[channel] + (Bright[channel] - Dark[channel]) * brightFraction;
            float actual = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, (pixel * 4 + channel) * 2));
            _rows.Add(new { test = reason, channel, actual, expected });
            Check(Math.Abs(actual - expected) <= .002f, reason + " channel " + channel);
        }
    }

    private static byte[] HalfBytes(float[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), BitConverter.HalfToUInt16Bits((Half)values[i]));
        return bytes;
    }

    private sealed class Raster : IDisposable
    {
        private readonly RD _rd;
        private readonly Action<bool, string> _check;
        private Rid _shader, _sampler;
        internal string Fragment { get; }
        internal Raster(RD rd, Action<bool, string> check)
        {
            _rd = rd; _check = check;
            Fragment = OrderedWorldDevice.SpeedShader("soften_fragment.glsl.txt");
            try
            {
                using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
                    SourceVertex = OrderedWorldDevice.SpeedShader("soften_vertex.glsl.txt"), SourceFragment = Fragment };
                using var code = rd.ShaderCompileSpirVFromSource(source);
                check(string.IsNullOrEmpty(code.CompileErrorVertex) && string.IsNullOrEmpty(code.CompileErrorFragment),
                    "shipped soften shaders compile: " + code.CompileErrorVertex + code.CompileErrorFragment);
                _shader = rd.ShaderCreateFromSpirV(code, "guo_receiver_soften_probe");
                using var state = new RDSamplerState { MinFilter = RD.SamplerFilter.Nearest,
                    MagFilter = RD.SamplerFilter.Nearest, MipFilter = RD.SamplerFilter.Nearest,
                    RepeatU = RD.SamplerRepeatMode.ClampToEdge, RepeatV = RD.SamplerRepeatMode.ClampToEdge };
                _sampler = rd.SamplerCreate(state);
                check(_shader.IsValid && _sampler.IsValid, "shipped shader and nearest sampler valid");
            }
            catch { Dispose(); throw; }
        }

        internal byte[] Draw(Vector2I size, byte[] light, byte[] surface, int axis, int radius, bool bothAxes = false)
        {
            var owned = new List<Rid>();
            Rid Keep(Rid rid) { owned.Add(rid); _check(rid.IsValid, "soften fixture GPU resource valid"); return rid; }
            try
            {
                Rid input = Keep(Texture(size, light)), receiver = Keep(Texture(size, surface));
                Rid output = default;
                for (int pass = 0; pass < (bothAxes ? 2 : 1); pass++)
                {
                    output = Keep(Texture(size, null));
                    Rid framebuffer = Keep(_rd.FramebufferCreate(new Godot.Collections.Array<Rid> { output }));
                    using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
                    using var samples = new RDPipelineMultisampleState();
                    using var depth = new RDPipelineDepthStencilState();
                    using var attachment = new RDPipelineColorBlendStateAttachment { WriteR = true, WriteG = true, WriteB = true, WriteA = true };
                    using var blend = new RDPipelineColorBlendState { Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
                    Rid pipeline = Keep(_rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(framebuffer), RD.InvalidId,
                        RD.RenderPrimitive.Triangles, raster, samples, depth, blend));
                    using var lighting = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 };
                    lighting.AddId(_sampler); lighting.AddId(input);
                    using var geometry = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 1 };
                    geometry.AddId(_sampler); geometry.AddId(receiver);
                    Rid set = Keep(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { lighting, geometry }, _shader, 0));
                    int direction = bothAxes ? pass : axis;
                    var push = new byte[32];
                    BitConverter.TryWriteBytes(push.AsSpan(0, 4), (float)size.X);
                    BitConverter.TryWriteBytes(push.AsSpan(4, 4), (float)size.Y);
                    BitConverter.TryWriteBytes(push.AsSpan(8, 4), direction == 0 ? 1f : 0f);
                    BitConverter.TryWriteBytes(push.AsSpan(12, 4), direction == 1 ? 1f : 0f);
                    BitConverter.TryWriteBytes(push.AsSpan(16, 4), radius);
                    long list = _rd.DrawListBegin(framebuffer);
                    _check(list != RD.InvalidId, "soften raster draw opens");
                    try
                    {
                        _rd.DrawListBindRenderPipeline(list, pipeline);
                        _rd.DrawListBindUniformSet(list, set, 0);
                        _rd.DrawListSetPushConstant(list, push, 32);
                        _rd.DrawListDraw(list, false, 1, 3);
                    }
                    finally { _rd.DrawListEnd(); }
                    input = output;
                }
                _rd.Submit(); _rd.Sync();
                _check(_rd.TextureGetData(owned[0], 0).AsSpan().SequenceEqual(light), "soften leaves raw source unchanged");
                _check(_rd.TextureGetData(receiver, 0).AsSpan().SequenceEqual(surface), "soften leaves physical receivers unchanged");
                byte[] result = _rd.TextureGetData(output, 0);
                _check(result.Length == light.Length, "actual RGBA16F soften readback size");
                return result;
            }
            finally { for (int i = owned.Count - 1; i >= 0; i--) if (owned[i].IsValid) _rd.FreeRid(owned[i]); }
        }

        private Rid Texture(Vector2I size, byte[] bytes)
        {
            using var spec = new RDTextureFormat { Width = (uint)size.X, Height = (uint)size.Y,
                Depth = 1, ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
                Samples = RD.TextureSamples.Samples1, Format = RD.DataFormat.R16G16B16A16Sfloat,
                UsageBits = RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit
                    | (bytes == null ? RD.TextureUsageBits.ColorAttachmentBit : RD.TextureUsageBits.CanUpdateBit) };
            using var view = new RDTextureView();
            return bytes == null ? _rd.TextureCreate(spec, view)
                : _rd.TextureCreate(spec, view, new Godot.Collections.Array<byte[]> { bytes });
        }

        public void Dispose()
        {
            if (_sampler.IsValid) _rd.FreeRid(_sampler);
            if (_shader.IsValid) _rd.FreeRid(_shader);
            _sampler = _shader = default;
        }
    }

    private void Save(string failure)
    {
        string root = System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");
        if (string.IsNullOrWhiteSpace(root)) return;
        root = Path.GetFullPath(root);
        string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        if (root.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GPU fixture evidence must stay outside project.");
        string folder = Path.Combine(root, "data"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "receiver-soften-gpu-results.json"), JsonSerializer.Serialize(
            new { backend = "gpu-raster-half", checks = _checks, rows = _rows, failure },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
