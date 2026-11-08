// SPDX-License-Identifier: BSD-2-Clause
// Actual production LightPass; synthetic receiver/shadow inputs, no UO data.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.Lights;
using N3 = System.Numerics.Vector3;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class SkyLightTermsGpuProbe : Node
{
    private readonly List<object> _rows = new();
    private int _checks;
    private string _output;
    private const string LegacyOutput = "light=vec4(pc.ambient_roof.rgb*(roof*sun),1); return;";

    private readonly record struct Receiver(string Name, float X, float Y, float Z, int Packed);
    private static readonly Receiver[] Receivers = {
        new("empty", .1f, .5f, 0, 0), new("legacy-floor", .15f, .5f, 0, 1),
        new("union-floor", .2f, .5f, 0, 257), new("structural-floor", .25f, .5f, 0, 257),
        new("upper-floor", .3f, .5f, 64, 257), new("wall-y", .35f, .5f, 0, 258),
        new("roof", .4f, .5f, 0, 261), new("covered-floor", .45f, .5f, 0, 273),
        new("mobile", .5f, .5f, 0, 7), new("outside-domain", 2, .5f, 0, 257),
        new("unqualified-wall", .6f, .5f, 0, 2), new("covered-legacy", .65f, .5f, 0, 17),
        new("wall-x", .7f, .5f, 0, 259), new("diagonal-wall", .75f, .5f, 0, 260)
    };

    private readonly record struct Sample(string Name, N3 Ambient, float Roof, float Strength,
        byte Mask, bool Legacy = true, bool Structural = true, bool Lamp = false, LightCurve Curve = LightCurve.A);

    public override async void _Ready()
    {
#if DEBUG
        var baseline = new OrderedWorldDevice(); var current = new OrderedWorldDevice();
        Exception failure = null;
        try
        {
            string output = System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");
            Check(!string.IsNullOrWhiteSpace(output) && Path.IsPathRooted(output), "external evidence directory supplied");
            output = Path.GetFullPath(output);
            string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
            Check(!output.StartsWith(project, StringComparison.OrdinalIgnoreCase), "evidence outside project");
            _output = output;
            Directory.CreateDirectory(Path.Combine(_output, "data"));
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            RenderingServer.CallOnRenderThread(Callable.From(() => {
                var owned = new List<Rid>(); RD rd = RenderingServer.GetRenderingDevice();
                try
                {
                    Check(rd != null, "main rendering device available");
                    byte[] surface = SurfaceBytes();
                    Initialize(baseline, rd, surface); Initialize(current, rd, surface);
                    Run(rd, baseline, current, owned, surface);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    OrderedWorldDevice.ProbeLegacyAmbientOutput = false;
                    baseline.DisposeRT(); current.DisposeRT();
                    for (int i = owned.Count - 1; i >= 0; i--) if (owned[i].IsValid) rd.FreeRid(owned[i]);
                }
            }));
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (failure != null) throw failure;
            Check(!baseline.SpeedLightReady && !current.SpeedLightReady
                && !baseline.HasSurfaceResources && !current.HasSurfaceResources, "normal device disposal retires probe inputs and light targets");
            Save(null); GD.Print($"[sky-light-terms-gpu] PASS checks={_checks} actual-LightPass neutral-bit-exact sky/direct/HDR");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            Save(error.ToString()); GD.PrintErr("[sky-light-terms-gpu] FAIL " + error); GetTree().Quit(1);
        }
        finally
        {
            OrderedWorldDevice.ProbeLegacyAmbientOutput = false;
            foreach (var device in new[] { baseline, current })
            {
                device.Presenter.Dispose(); device.SurfaceTexture.Dispose(); device.SpeedLightTexture.Dispose();
            }
        }
#else
        GD.PrintErr("[sky-light-terms-gpu] Debug fixture required"); GetTree().Quit(1);
#endif
    }

#if DEBUG
    private void Initialize(OrderedWorldDevice device, RD rd, byte[] surface)
    {
        var size = new Vector2I(Receivers.Length, 1);
        device.InitializeRT(size, OrderedWorldCapture.ShaderSource("resident_world_vertex.glsl.txt"),
            OrderedWorldCapture.ShaderSource("ordered_world_fragment.glsl.txt"),
            OrderedWorldCapture.ShaderSource("depth_world_resident_vertex.glsl.txt"));
        // Only the input G-buffer is synthetic. The complete production
        // RenderLightsRT allocates/binds/pushes/draws the actual light program.
        // Ownership transfers to DisposeSurfaceRT, exactly as a rendered input.
        Rid texture = Texture(rd, size, RD.DataFormat.R16G16B16A16Sfloat, surface);
        var type = typeof(OrderedWorldDevice);
        type.GetField("_surfaceColor", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(device, texture);
        type.GetProperty("SurfaceReady", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(device, true);
        type.GetProperty("SurfaceOrigin", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(device, Vector2I.Zero);
        device.SurfaceTexture.TextureRdRid = texture;
        Check(device.ReadSurfaceRT().AsSpan().SequenceEqual(surface), "exact synthetic receiver input attached");
    }

    private void Run(RD rd, OrderedWorldDevice baseline, OrderedWorldDevice current, List<Rid> owned, byte[] surface)
    {
        string source = OrderedWorldDevice.SpeedShader("light_fragment.glsl.txt");
        Check(source.Contains("// AMBIENT_LIGHT_OUTPUT", StringComparison.Ordinal), "production shader has bounded ambient output seam");
        string complete = source.Replace("// STRUCTURAL_LAMP_SAMPLING", StructuralLampDepthRd.SamplingGlsl);
        _rows.Add(new { test = "program-identity", productionFragmentSha256 = Hash(source),
            assembledBaselineSha256 = Hash(complete.Replace("// AMBIENT_LIGHT_OUTPUT", LegacyOutput)),
            assembledCurrentSha256 = Hash(complete.Replace("// AMBIENT_LIGHT_OUTPUT", SkyLightTerms.OutputGlsl)),
            baseline = "same complete production LightPass with original ambient output expression at the explicit seam",
            frozenAmbientExpression = LegacyOutput, legacyExpressionSha256 = Hash(LegacyOutput),
            sourceBoundary = "synthetic RGBA16F receiver, R8 legacy sun, structural depth and SSBO; actual RenderLightsRT binding and draws" });
        Rid Keep(Rid rid) { Check(rid.IsValid, "valid external shadow input"); owned.Add(rid); return rid; }
        Rid depth = Keep(Texture(rd, Vector2I.One, RD.DataFormat.R32Sfloat, BitConverter.GetBytes(.5f)));
        var reach = new RealtimeLightField.SpeedReach { Side = 4, Scale = 1, X = 0, Y = 0, Revision = 1, Pixels = new byte[32] };
        Array.Fill(reach.Pixels, (byte)255);
        var samples = new List<Sample> {
            new("sun-disabled", new(1,.75f,.5f), .55f, 0, 0, false, false),
            new("moon-r8-ceiling", new(.4f,.6f,.9f), .55f, .32f, 82),
            new("moon-partial-mask", new(.4f,.6f,.9f), .55f, .32f, 41),
            new("structural-only", new(1,.7f,.3f), .55f, .32f, 0, false),
            new("legacy-only", new(1,.7f,.3f), .55f, .32f, 82, true, false),
            new("daylight-r8-ceiling", new(1,.8f,.6f), .55f, .72f, 184),
            new("native-day-strength", new(1,.8f,.6f), .55f, .55f, 141),
            new("native-high-strength", new(.8f,.9f,1), .55f, .62f, 159),
            new("full-block", new(.7f,1,.6f), .55f, 1, 255),
            new("no-roof-dimming", new(.2f,.4f,.8f), 1, .45f, 115),
            new("roof-zero", new(.2f,.4f,.8f), 0, .45f, 115),
            new("night-floor", new(.02f,.03f,.05f), .55f, 0, 0, false, false),
            new("night-native-strength", new(.02f,.03f,.05f), .55f, .20f, 51),
            new("night-moon", new(.02f,.03f,.05f), .55f, .32f, 82),
            new("ambient-plus-lamp", new(.2f,.3f,.4f), .55f, .32f, 82, true, true, true)
        };
        foreach (LightCurve curve in new[] { LightCurve.A, LightCurve.B, LightCurve.C, LightCurve.D, LightCurve.E, LightCurve.Custom })
            samples.Add(new("lamp-only-" + curve, N3.Zero, .55f, .32f, 82, true, true, true, curve));
        foreach (var sample in samples)
        {
            byte expectedBudgetByte = ExpectedBudgetByte(sample.Strength);
            Check(SkyLightTerms.QuantizedSunBudget(sample.Strength) == expectedBudgetByte / 255f,
                "explicit native-strength R8 budget golden: " + sample.Name);
            byte[] mask = new byte[Receivers.Length]; Array.Fill(mask, sample.Mask);
            mask[3] = mask[4] = 0; // structural-only ground hit and upper-floor miss
            Rid maskRid = Keep(Texture(rd, new Vector2I(Receivers.Length, 1), RD.DataFormat.R8Unorm, mask));
            Rid parameters = Keep(rd.StorageBufferCreate(132, SunParameters(sample)));
            var frame = new SpeedLightFrame { Origin = Vector2I.Zero,
                Ambient = new Vector3(sample.Ambient.X, sample.Ambient.Y, sample.Ambient.Z),
                RoofShare = sample.Roof, SunShadowStrength = sample.Strength,
                SunMask = sample.Legacy ? maskRid : default,
                StructuralSunDepth = depth, StructuralSunParameters = parameters };
            if (sample.Lamp)
            {
                frame.Count = 1;
                frame.Lights[0] = new SpeedLightFrame.Entry {
                    Source = new RealtimeLightField.Source(0, 0, -10, 8, new Color(1,.5f,.25f),
                        curve: sample.Curve, energy: 4, customCurve: sample.Curve == LightCurve.Custom ? new[] { 1f, 0f } : null),
                    Reach = reach, PixelCentre = new Vector2(Receivers.Length / 2f, .5f),
                    PixelRadius = Receivers.Length, Flicker = 1, ShadowRow = -1 };
            }
            frame.AmbientProbeTerm = 0; OrderedWorldDevice.ProbeLegacyAmbientOutput = true;
            baseline.RenderLightsRT(frame); byte[] old = baseline.ReadSpeedLightRT();
            OrderedWorldDevice.ProbeLegacyAmbientOutput = false;
            current.RenderLightsRT(frame); byte[] neutral = current.ReadSpeedLightRT();
            var parity = Difference(old, neutral);
            _rows.Add(new { test = "neutral-parity", sample.Name, sample.Strength, sample.Mask,
                expectedBudgetByte, budget = SkyLightTerms.QuantizedSunBudget(sample.Strength), parity,
                baselineSha256 = Hash(old), currentSha256 = Hash(neutral) });
            Check(parity.Components == 0, "raw HDR neutral output bit exact against original expression: " + sample.Name);
            frame.AmbientProbeTerm = 1; current.RenderLightsRT(frame); byte[] sky = current.ReadSpeedLightRT();
            frame.AmbientProbeTerm = 2; current.RenderLightsRT(frame); byte[] direct = current.ReadSpeedLightRT();
            if (!sample.Lamp) VerifyAmbient(sample, mask, neutral, sky, direct);
            else if (sample.Ambient == N3.Zero)
            {
                Check(neutral.AsSpan().SequenceEqual(sky) && neutral.AsSpan().SequenceEqual(direct),
                    "ambient debug selection leaves lamp-only HDR bit exact: " + sample.Name);
                VerifyLamp(sample, neutral);
            }
            // Toggle back in the SAME allocated device: no stale debug term.
            frame.AmbientProbeTerm = 0; current.RenderLightsRT(frame);
            Check(neutral.AsSpan().SequenceEqual(current.ReadSpeedLightRT()), "normal mode restored without target recreation: " + sample.Name);
            Check(current.ReadSurfaceRT().AsSpan().SequenceEqual(surface)
                && baseline.ReadSurfaceRT().AsSpan().SequenceEqual(surface), "receiver input immutable: " + sample.Name);
        }
    }

    private void VerifyAmbient(Sample sample, byte[] mask, byte[] neutral, byte[] sky, byte[] direct)
    {
        // Explicit native-strength goldens avoid reproducing the helper and
        // avoid promoting .20f before multiplication (which would ceil to52).
        double q = ExpectedBudgetByte(sample.Strength) / 255d;
        int maxUlps = 0; double maxReconstruction = 0;
        var points = new List<object>();
        for (int i = 0; i < Receivers.Length; i++)
        {
            Receiver p = Receivers[i]; int kind = p.Packed & 15;
            double roof = (p.Packed & 16) != 0 ? sample.Roof : 1;
            double legacy = sample.Legacy && kind == 1 ? 1 - mask[i] / 255d : 1;
            bool hit = sample.Structural && sample.Strength > 0 && (p.Packed & 256) != 0
                && H(p.X) >= 0 && H(p.X) < 1 && H(p.Y) >= 0 && H(p.Y) < 1
                && .5 < .75 - H(p.Z) / 128d - .001;
            double structural = hit ? 1 - sample.Strength : 1, sun = Math.Min(legacy, structural);
            Check(sun >= 1 - q - 1e-7, "source attenuation remains inside the R8-aware budget: " + sample.Name);
            var helper = SkyLightTerms.Split(sample.Ambient, (float)roof, (float)sun, (float)q);
            Check(helper.Admitted, "CPU split admits bounded actual GPU fixture: " + sample.Name);
            for (int c = 0; c < 3; c++)
            {
                double ambient = c == 0 ? sample.Ambient.X : c == 1 ? sample.Ambient.Y : sample.Ambient.Z;
                double n = ambient * roof * sun, s = ambient * roof * (1 - q);
                double d = ambient * roof * Math.Max(0, sun - (1 - q));
                maxUlps = Math.Max(maxUlps, HalfGolden(neutral, i, c, n, sample.Name + " neutral"));
                maxUlps = Math.Max(maxUlps, HalfGolden(sky, i, c, s, sample.Name + " sky"));
                maxUlps = Math.Max(maxUlps, HalfGolden(direct, i, c, d, sample.Name + " direct"));
                float hn = c == 0 ? helper.Neutral.X : c == 1 ? helper.Neutral.Y : helper.Neutral.Z;
                float hs = c == 0 ? helper.Sky.X : c == 1 ? helper.Sky.Y : helper.Sky.Z;
                float hd = c == 0 ? helper.Direct.X : c == 1 ? helper.Direct.Y : helper.Direct.Z;
                Check(Math.Abs(hn - n) < 2e-7 && Math.Abs(hs - s) < 2e-7 && Math.Abs(hd - d) < 2e-7,
                    "CPU helper agrees with independent double golden: " + sample.Name);
                double error = Math.Abs(Read(sky, i, c) + Read(direct, i, c) - Read(neutral, i, c));
                maxReconstruction = Math.Max(maxReconstruction, error);
                // Three independent half stores: sum their individual adjacent
                // representable spacings, not an arbitrary image epsilon.
                double limit = HalfStep(n) + HalfStep(s) + HalfStep(d);
                Check(error <= limit, "sky plus direct reconstructs normal within half-store precision: " + sample.Name);
            }
            Check(Read(neutral, i, 3) == 1 && Read(sky, i, 3) == 1 && Read(direct, i, 3) == 1,
                "ambient alpha unchanged: " + sample.Name);
            points.Add(new { receiver = p.Name, p.Packed, z = p.Z, legacy, structural, union = sun,
                normal = Read(neutral, i, 0), sky = Read(sky, i, 0), direct = Read(direct, i, 0) });
        }
        _rows.Add(new { test = "ambient-goldens", sample.Name, q, maxHalfUlps = maxUlps,
            maxReconstruction, points, numericTolerance = "one half ULP per stored term; reconstruction bounded by sum of three half spacings" });
    }

    private void VerifyLamp(Sample sample, byte[] output)
    {
        float maximum = 0; int maxUlps = 0;
        for (int i = 0; i < Receivers.Length; i++)
        {
            Receiver p = Receivers[i]; int kind = p.Packed & 15;
            double x = .5 - H(p.X), y = .5 - H(p.Y), z = -H(p.Z) / 11d;
            double length = Math.Sqrt(x*x + y*y + z*z), t = length / 8;
            double k = 1 - t*t;
            double falloff = sample.Curve switch {
                LightCurve.B => k*k, LightCurve.C => k*k / (1 + 8*t*t), LightCurve.D => k,
                LightCurve.E => Smooth(Math.Clamp((1-t)/.4, 0, 1)), _ => 1-t };
            double nz = z + .00001, norm = Math.Sqrt(x*x + y*y + nz*nz);
            double facing = kind == 2 ? Math.Max(.2, y/norm) : kind == 3 ? Math.Max(.2, x/norm)
                : kind == 4 ? Math.Max(.2, (x+y)/(norm*Math.Sqrt(2))) : 1;
            for (int c = 0; c < 3; c++)
            {
                double expected = kind == 0 || t >= 1 ? 0 : 4 * falloff * facing * (c == 0 ? 1 : c == 1 ? .5 : .25);
                maxUlps = Math.Max(maxUlps, HalfGolden(output, i, c, expected, sample.Name));
                maximum = Math.Max(maximum, Read(output, i, c));
            }
            Check(Read(output, i, 3) == 1, "additive lamp preserves ambient alpha: " + sample.Name);
        }
        Check(maximum > 3, "raw lamp HDR remains above display range: " + sample.Name);
        _rows.Add(new { test = "lamp-goldens", sample.Name, maximum, maxHalfUlps = maxUlps,
            curve = sample.Curve.ToString(), numericTolerance = "one half ULP, actual additive production shader" });
    }

    private int HalfGolden(byte[] bytes, int pixel, int channel, double expected, string reason)
    {
        ushort actualBits = BitConverter.ToUInt16(bytes, pixel * 8 + channel * 2);
        ushort expectedBits = BitConverter.HalfToUInt16Bits((Half)expected);
        float actual = (float)BitConverter.UInt16BitsToHalf(actualBits);
        int ulps = Math.Abs((int)actualBits - expectedBits);
        if (!float.IsFinite(actual) || actual < 0 || ulps > 1)
            _rows.Add(new { test = "golden-difference", reason, receiver = Receivers[pixel].Name, channel,
                actual, expected, actualBits, expectedBits, ulps });
        Check(float.IsFinite(actual) && actual >= 0 && ulps <= 1,
            $"{reason}: {Receivers[pixel].Name} channel {channel}, actual={actual:R} expected={expected:R} halfULPs={ulps}");
        return ulps;
    }

    private static byte[] SunParameters(Sample sample)
    {
        float[] values = new float[33];
        values[0] = 1; values[7] = .5f; values[10] = -1f/128; values[11] = .75f;
        values[14] = values[15] = 1; values[19] = .001f;
        values[22] = 1; values[23] = sample.Strength;
        values[24] = -128; values[25] = 382; values[26] = sample.Structural ? 1 : 0;
        return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    private static byte[] SurfaceBytes()
    {
        byte[] bytes = new byte[Receivers.Length * 8];
        for (int i = 0; i < Receivers.Length; i++)
        {
            var p = Receivers[i]; float[] channels = { p.X, p.Y, p.Z, p.Packed };
            for (int c = 0; c < 4; c++) BitConverter.TryWriteBytes(bytes.AsSpan(i*8+c*2, 2), BitConverter.HalfToUInt16Bits((Half)channels[c]));
        }
        return bytes;
    }

    private Rid Texture(RD rd, Vector2I size, RD.DataFormat dataFormat, byte[] bytes)
    {
        using var format = new RDTextureFormat { Width = (uint)size.X, Height = (uint)size.Y, Depth = 1,
            ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D, Format = dataFormat,
            UsageBits = RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit };
        using var view = new RDTextureView();
        Rid texture = rd.TextureCreate(format, view, new Godot.Collections.Array<byte[]> { bytes });
        Check(texture.IsValid, "synthetic typed input allocated"); return texture;
    }

    private static double Smooth(double x) => x*x*(3-2*x);
    private static byte ExpectedBudgetByte(float strength) => strength switch {
        0 => 0, .20f => 51, .32f => 82, .45f => 115, .55f => 141, .62f => 159, .72f => 184, 1 => 255,
        _ => throw new InvalidOperationException("No independent budget golden for fixture strength")
    };
    private static float H(float x) => (float)(Half)x;
    private static float Read(byte[] bytes, int pixel, int channel) =>
        (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, pixel*8+channel*2));
    private static double HalfStep(double value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        return (float)BitConverter.UInt16BitsToHalf((ushort)(bits+1)) - (float)BitConverter.UInt16BitsToHalf(bits);
    }
    private readonly record struct Delta(int Components, int MaxHalfUlps);
    private static Delta Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new InvalidOperationException("light output size changed");
        int differences = 0, ulps = 0;
        for (int i = 0; i < a.Length; i += 2)
        {
            int x = BitConverter.ToUInt16(a, i), y = BitConverter.ToUInt16(b, i);
            if (x == y) continue; differences++; ulps = Math.Max(ulps, Math.Abs(x-y));
        }
        return new(differences, ulps);
    }
#endif

    private void Check(bool ok, string reason) { _checks++; if (!ok) throw new InvalidOperationException(reason); }
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private void Save(string failure)
    {
        if (string.IsNullOrWhiteSpace(_output)) return;
        string folder = Path.Combine(_output, "data"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sky-light-terms-gpu-results.json"), JsonSerializer.Serialize(
            new { backend = "main-rendering-device actual OrderedWorldDevice.RenderLightsRT", checks = _checks, rows = _rows, failure },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
