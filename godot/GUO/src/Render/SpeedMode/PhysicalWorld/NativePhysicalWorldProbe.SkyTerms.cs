// SPDX-License-Identifier: BSD-2-Clause
// Native evidence for compatibility sky/direct terms, not physical irradiance.
#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer.DepthWorld;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private readonly Dictionary<Shot, byte[]> _skySunMasks = new();
    private static float SkyChannel(Shot shot, int pixel, int channel) =>
        (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(shot.Light, pixel * 8 + channel * 2));

    // Every additive lamp draw may round once to RGBA16F. Bound those rounds
    // explicitly; this is not the separate GPU oracle's bit-exact neutral test.
    private static float SkyHalfAllowance(int operations, params float[] values)
    {
        float largest = values.Max(v => MathF.Abs(v));
        if (!float.IsFinite(largest) || largest >= 65504f) return 0;
        ushort bits = BitConverter.HalfToUInt16Bits((Half)largest);
        float ulp = (float)BitConverter.UInt16BitsToHalf((ushort)(bits + 1))
            - (float)BitConverter.UInt16BitsToHalf(bits);
        return MathF.Max(.00002f, operations * ulp);
    }

    private async Task<Shot> CaptureSkyTerm(string name, int term, float expectedStrength, int expectedBudget)
    {
        _scene.SpeedAmbientProbeTerm = term;
        var shot = await Capture(name);
        byte[] sunMask = null; Exception readFailure = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            try { sunMask = Lights.SunMask.IsValid
                ? RenderingServer.GetRenderingDevice().TextureGetData(Lights.SunMask, 0)
                : new byte[shot.Surface.GetWidth() * shot.Surface.GetHeight()]; }
            catch (Exception error) { readFailure = error; }
        }));
        await Painted();
        if (readFailure != null) { shot.Dispose(); throw readFailure; }
        Require(sunMask.Length == shot.Surface.GetWidth() * shot.Surface.GetHeight(), "actual R8 sun mask layout " + name);
        _skySunMasks.Add(shot, sunMask);
        File.WriteAllBytes(Path.Combine(_root, "data", name + "-sun-mask.r8"), sunMask);
        float expectedQ = expectedBudget / 255f;
        Check(Lights.AmbientProbeTerm == term, "same prepared sky diagnostic term " + name);
        Check(Lights.SunShadowStrength == expectedStrength && shot.Projection.Strength == expectedStrength,
            "same prepared/projection resolved sun strength " + name);
        Check(SkyLightTerms.QuantizedSunBudget(Lights.SunShadowStrength) == expectedQ,
            "independent native-strength byte budget " + name);
        _rows.Add(new { test = "sky-term-publication", name, term, shot.Sequence,
            clockValid = _game.UO.World.ShardClock.Valid,
            minute = _game.UO.World.ShardClock.MinuteAt(Time.Ticks), tick = Time.Ticks,
            source = _scene.SpeedSunSource, strength = Lights.SunShadowStrength, expectedStrength,
            budget = SkyLightTerms.QuantizedSunBudget(Lights.SunShadowStrength), expectedBudget,
            overall = _game.UO.World.Light.Overall, personal = _game.UO.World.Light.Personal,
            ambient = new[] { Lights.Ambient.X, Lights.Ambient.Y, Lights.Ambient.Z }, Lights.RoofShare,
            lightCount = Lights.Count, physicalGeneration = shot.Snapshot.Key.Generation,
            shot.Snapshot.SourceFingerprint, shot.DepthHash,
            legacySunMaskSha256 = Convert.ToHexString(SHA256.HashData(sunMask)),
            rawHdrSha256 = Convert.ToHexString(SHA256.HashData(shot.Light)),
            finalRgbaSha256 = Convert.ToHexString(SHA256.HashData(shot.Presented)) });
        Save(); return shot;
    }

    private void CheckSkyTrio(string name, Shot normal, Shot sky, Shot direct, int expectedBudget)
    {
        Require(normal.Origin == sky.Origin && normal.Origin == direct.Origin
            && normal.Surface.GetData().AsSpan().SequenceEqual(sky.Surface.GetData())
            && normal.Surface.GetData().AsSpan().SequenceEqual(direct.Surface.GetData()),
            "identical native receivers across sky/direct diagnostic " + name);
        Check(ReferenceEquals(normal.Snapshot, sky.Snapshot) && ReferenceEquals(normal.Snapshot, direct.Snapshot)
            && normal.DepthHash == sky.DepthHash && normal.DepthHash == direct.DepthHash,
            "unchanged native geometry/depth across sky diagnostic " + name);
        Check(normal.Offers == sky.Offers && normal.Offers == direct.Offers,
            "identical lamp offers across sky diagnostic " + name);
        Check(_skySunMasks[normal].AsSpan().SequenceEqual(_skySunMasks[sky])
            && _skySunMasks[normal].AsSpan().SequenceEqual(_skySunMasks[direct]),
            "identical actual legacy sun mask across diagnostic terms " + name);
        int samples = 0, invalid = 0, skyPositive = 0, directPositive = 0, finalSky = 0, finalDirect = 0;
        float maxSkyError = 0, maxTolerance = 0;
        float[] ambient = { Lights.Ambient.X, Lights.Ambient.Y, Lights.Ambient.Z };
        float q = expectedBudget / 255f;
        int width = normal.Surface.GetWidth();
        for (int y = 0; y < normal.Surface.GetHeight(); y += 3)
        for (int x = 0; x < width; x += 3)
        {
            var p = normal.Surface.GetPixel(x, y); int packed = (int)MathF.Round(p.A);
            if ((packed & 271) != 257 || MathF.Abs(p.B - _z) > .2f) continue;
            float wx = p.R + normal.Origin.X, wy = p.G + normal.Origin.Y;
            if (wx < X - 4 || wx > X + 4 || wy < Y - 4 || wy > Y + 4) continue;
            int pixel = y * width + x;
            float roof = (packed & 16) != 0 ? Lights.RoofShare : 1f;
            bool positiveSky = false, positiveDirect = false;
            for (int c = 0; c < 3; c++)
            {
                float n = SkyChannel(normal, pixel, c), s = SkyChannel(sky, pixel, c), d = SkyChannel(direct, pixel, c);
                float expectedSky = ambient[c] * (roof * (1f - q));
                float tolerance = SkyHalfAllowance(Lights.Count + 3, n, s, d);
                float error = MathF.Abs(n - d - expectedSky);
                maxSkyError = MathF.Max(maxSkyError, error); maxTolerance = MathF.Max(maxTolerance, tolerance);
                if (!float.IsFinite(n) || !float.IsFinite(s) || !float.IsFinite(d)
                    || error > tolerance || n - s < -tolerance || n - s > ambient[c] * roof * q + tolerance) invalid++;
                positiveSky |= n - d > tolerance; positiveDirect |= n - s > tolerance;
            }
            samples++; if (positiveSky) skyPositive++; if (positiveDirect) directPositive++;
            int at = pixel * 4;
            if (Enumerable.Range(0, 3).Sum(c => normal.Presented[at + c] - direct.Presented[at + c]) > 2) finalSky++;
            if (Enumerable.Range(0, 3).Sum(c => normal.Presented[at + c] - sky.Presented[at + c]) > 2) finalDirect++;
        }
        Check(samples >= 100 && invalid == 0 && skyPositive >= 3 && directPositive >= 3,
            "actual native positive sky/direct decomposition " + name);
        Check(finalSky >= 3 && finalDirect >= 3, "both sky/direct terms affect final native presentation " + name);
        _rows.Add(new { test = "native-sky-terms", name, samples, invalid, skyPositive, directPositive, finalSky, finalDirect,
            maxSkyError, maxTolerance, expectedBudget,
            oracle = "Normal-Direct=A*(roof*(1-Q)); 0<=Normal-Sky<=A*roof*Q; unchanged lamp offers cancel",
            precision = "RGBA16F allowance (offered lamp count+3)*ULP at largest sampled channel; minimum 0.00002" });
        Save();
    }

    private void CheckSkyLamp(string name, Shot[] withLamp, Shot[] withoutLamp)
    {
        Require(withLamp[0].Origin == withoutLamp[0].Origin, "unchanged receiver origin through lamp removal " + name);
        Require(withLamp[0].DepthHash == withoutLamp[0].DepthHash && withLamp[0].Projection == withoutLamp[0].Projection,
            "unchanged structural sun depth/projection through lamp removal " + name);
        int width = withLamp[0].Surface.GetWidth(), samples = 0, positive = 0, invalid = 0, changedSunMask = 0;
        var invalidExamples = new List<object>();
        float maximumError = 0, maximumAllowance = 0;
        for (int y = 0; y < withLamp[0].Surface.GetHeight(); y += 3)
        for (int x = 0; x < width; x += 3)
        {
            var p = withLamp[0].Surface.GetPixel(x, y);
            if (p != withoutLamp[0].Surface.GetPixel(x, y) || ((int)MathF.Round(p.A) & 271) != 257
                || MathF.Abs(p.B - _z) > .2f) continue;
            float wx = p.R + withLamp[0].Origin.X, wy = p.G + withLamp[0].Origin.Y;
            if (wx < X - 4 || wx > X + 4 || wy < Y - 4 || wy > Y + 4) continue;
            int pixel = y * width + x; bool lit = false;
            // A native lamp is also a sun caster. Removing it changes ambient
            // beneath its silhouette; only an independently equal R8 mask can
            // isolate the additive lamp delta. Never infer this domain from RGB.
            if (_skySunMasks[withLamp[0]][pixel] != _skySunMasks[withoutLamp[0]][pixel]) { changedSunMask++; continue; }
            for (int c = 0; c < 3; c++)
            {
                float[] a = withLamp.Select(s => SkyChannel(s, pixel, c)).ToArray();
                float[] b = withoutLamp.Select(s => SkyChannel(s, pixel, c)).ToArray();
                float baseline = a[0] - b[0];
                float tolerance = SkyHalfAllowance(2 * (Lights.Count + 1) + 5, a.Concat(b).ToArray());
                maximumAllowance = MathF.Max(maximumAllowance, tolerance);
                for (int term = 1; term < 3; term++) {
                    float error = MathF.Abs(a[term] - b[term] - baseline);
                    maximumError = MathF.Max(maximumError, error);
                    if (!float.IsFinite(error) || error > tolerance) {
                        invalid++;
                        if (invalidExamples.Count < 12) invalidExamples.Add(new { x, y, channel = c, term,
                            withLamp = a, withoutLamp = b, error, tolerance,
                            sunMask = _skySunMasks[withLamp[0]][pixel] });
                    }
                }
                lit |= baseline > MathF.Max(.005f, tolerance);
            }
            samples++; if (lit) positive++;
        }
        Check(samples >= 100 && positive >= 3 && invalid == 0, "native lamp contribution unchanged across diagnostic terms " + name);
        _rows.Add(new { test = "native-sky-lamp-invariance", name, samples, positive, invalid, changedSunMask,
            maximumError, maximumAllowance, invalidExamples,
            domain = "identical native receiver + structural depth + actual R8 legacy sun mask; lamp's removed sun silhouette excluded by mask bytes only",
            precision = "Four HDR readbacks, (2*(remaining offer count+1)+5)*half ULP; actual lamp removed between trios" }); Save();
    }

    private async Task RunSkyTermCases()
    {
        var world = _game.UO.World; var savedClock = world.ShardClock;
        int savedOverall = world.Light.Overall, savedPersonal = world.Light.Personal;
        bool savedCustom = _profile.UseCustomLightLevel;
        try
        {
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            int highest = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                world.Map.GetMapZ(X + x, Y + y, out sbyte land, out _); highest = Math.Max(highest, land);
            }
            _z = highest + 8; world.Player.SetInWorldTile(X - 1, Y - 1, (sbyte)_z);
            _profile.UseCustomLightLevel = false; _profile.SpeedModeSunShadows = true;
            _profile.SpeedModeLampShadows = 1; _profile.SpeedModeNightFloor = 0;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) Place(0x0495, X + x, Y + y, _z);
            ushort wall = FindWall(); for (int y = -1; y <= 1; y++) Place(wall, X - 3, Y + y, _z);
            if (System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_SKY_TRANSITION_LEGACY") == "1")
            {
                world.Light.Overall = 24; world.Light.Personal = 0; world.ShardClock.Set(0, 0, 0, Time.Ticks);
                Place(0x0B1D, X - 1, Y, _z); Mutated();
                _scene.SpeedAmbientProbeTerm = 0; _profile.LightingSpeedMode = false;
                using var off = await Capture("legacy-expression-transition-off", false);
                RecordSkyPreCompose("legacy-expression-after-off-before-first-reenable");
                _profile.LightingSpeedMode = true;
                using var first = await CaptureSkyTerm("legacy-expression-reenabled-first-normal", 0, .20f, 51);
                using var second = await CaptureSkyTerm("legacy-expression-reenabled-second-normal", 0, .20f, 51);
                using var third = await CaptureSkyTerm("legacy-expression-reenabled-third-normal", 0, .20f, 51);
                RecordSkySetup("original-expression-term0-only-reenable", first, second);
                Check(second.Light.AsSpan().SequenceEqual(third.Light) && second.World.AsSpan().SequenceEqual(third.World)
                    && second.Presented.AsSpan().SequenceEqual(third.Presented), "original-expression stable neutral repeat exact");
                _rows.Add(new { test = "legacy-expression-transition-scope",
                    shader = "original ambient expression compiled before child Main; current host bookkeeping",
                    comparison = "term0 only; first, second and third successful frames require exact HDR/world/final parity" }); Save();
                return;
            }
            var cases = new[] { (Name: "noon", Hour: 12, Overall: 0, Strength: .55f, Budget: 141),
                (Name: "night", Hour: 0, Overall: 24, Strength: .20f, Budget: 51),
                (Name: "moon-fallback", Hour: -1, Overall: 30, Strength: .32f, Budget: 82) };
            foreach (var item in cases)
            {
                _skySunMasks.Clear();
                world.Light.Overall = item.Overall; world.Light.Personal = 0;
                if (item.Hour < 0) world.ShardClock = default; else world.ShardClock.Set((byte)item.Hour, 0, 0, Time.Ticks);
                var lamp = Place(0x0B1D, X - 1, Y, _z); Mutated();
                // Preserve the first setup capture as a strict transition check;
                // subsequent sky/direct isolation also retains its stable baseline.
                using var setup = await CaptureSkyTerm(item.Name + "-setup-normal", 0, item.Strength, item.Budget);
                using var normal = await CaptureSkyTerm(item.Name + "-normal", 0, item.Strength, item.Budget);
                RecordSkySetup(item.Name + "-setup", setup, normal);
                var frozen = FrozenState();
                using var sky = await CaptureSkyTerm(item.Name + "-sky", 1, item.Strength, item.Budget);
                using var direct = await CaptureSkyTerm(item.Name + "-direct", 2, item.Strength, item.Budget);
                using var repeat = await CaptureSkyTerm(item.Name + "-normal-restored", 0, item.Strength, item.Budget);
                Check(ReferenceEquals(normal.Snapshot, repeat.Snapshot) && normal.DepthHash == repeat.DepthHash
                    && normal.Offers == repeat.Offers, "neutral restoration preserves source/depth/lamp offers " + item.Name);
                Check(normal.Light.AsSpan().SequenceEqual(repeat.Light) && normal.Presented.AsSpan().SequenceEqual(repeat.Presented)
                    && normal.World.AsSpan().SequenceEqual(repeat.World), "neutral restoration exact raw HDR/world/final " + item.Name);
                _rows.Add(new { test = "native-sky-neutral-restoration", name = item.Name,
                    rawHdrEqual = normal.Light.AsSpan().SequenceEqual(repeat.Light),
                    worldEqual = normal.World.AsSpan().SequenceEqual(repeat.World),
                    finalEqual = normal.Presented.AsSpan().SequenceEqual(repeat.Presented) });
                CheckSkyTrio(item.Name + "-with-lamp", normal, sky, direct, item.Budget);
                int countWithLamp = Lights.Count;
                lamp.Destroy(); Mutated();
                using var noNormal = await CaptureSkyTerm(item.Name + "-without-lamp-normal", 0, item.Strength, item.Budget);
                using var noSky = await CaptureSkyTerm(item.Name + "-without-lamp-sky", 1, item.Strength, item.Budget);
                using var noDirect = await CaptureSkyTerm(item.Name + "-without-lamp-direct", 2, item.Strength, item.Budget);
                Check(countWithLamp == Lights.Count + 1, "exactly one native fixture lamp offer removed " + item.Name);
                CheckSkyTrio(item.Name + "-without-lamp", noNormal, noSky, noDirect, item.Budget);
                CheckSkyLamp(item.Name, new[] { normal, sky, direct }, new[] { noNormal, noSky, noDirect });
                Check(_scene.SpeedSunSource == (item.Hour < 0 ? "shard-brightness-approximation" : "shard-clock"),
                    "actual scene sun provenance " + item.Name);
                Check(FrozenState() == frozen, "same native time/player/camera across sky and lamp comparisons " + item.Name);
                // Keep an actual native lamp for transition proof: a missing
                // first-frame pool must fail even when the HDR producer is ready.
                var transitionLamp = Place(0x0B1D, X - 1, Y, _z); Mutated();
                using var transitionBaseline = await CaptureSkyTerm(item.Name + "-transition-lamp-baseline", 0, item.Strength, item.Budget);
                // Compare actual old/new canvas recording at the same frozen
                // tick with a valid settled texture, independently of shaders.
                UltimaBatcher2D.ProbeLegacySpeedCompose = true;
                using var legacyCompose = await CaptureSkyTerm(item.Name + "-settled-legacy-compose", 0, item.Strength, item.Budget);
                UltimaBatcher2D.ProbeLegacySpeedCompose = false;
                using var currentCompose = await CaptureSkyTerm(item.Name + "-settled-current-compose", 0, item.Strength, item.Budget);
                RecordSkySetup(item.Name + "-settled-old-new-compose", legacyCompose, currentCompose);
                _scene.SpeedAmbientProbeTerm = 0; _profile.LightingSpeedMode = false;
                using var off = await Capture(item.Name + "-parent-off", false);
                RecordSkyPreCompose(item.Name + "-after-off-before-failure");
                _profile.LightingSpeedMode = true; _scene.SpeedAmbientProbeTerm = 1;
                OrderedWorldCapture.InjectSpeedLightFailure = true;
                using var failed = await Capture(item.Name + "-failure", false);
                OrderedWorldCapture.InjectSpeedLightFailure = false;
                RecordSkyPreCompose(item.Name + "-after-failure-before-first-recovery");
                _scene.SpeedAmbientProbeTerm = 0;
                // No intervening draw, native mutation or warm frame is allowed.
                using var recovered = await CaptureSkyTerm(item.Name + "-recovered-first-normal", 0, item.Strength, item.Budget);
                using var recoveryRepeat = await CaptureSkyTerm(item.Name + "-recovered-second-normal", 0, item.Strength, item.Budget);
                RecordSkySetup(item.Name + "-immediate-failure-recovery", recovered, recoveryRepeat);
                RecordSkySetup(item.Name + "-baseline-to-first-recovery", transitionBaseline, recovered);
                _profile.LightingSpeedMode = false; _scene.SpeedAmbientProbeTerm = 2;
                using var offAfter = await Capture(item.Name + "-parent-off-after", false);
                Check(off.World.AsSpan().SequenceEqual(failed.World) && off.World.AsSpan().SequenceEqual(offAfter.World)
                    && off.Presented.AsSpan().SequenceEqual(failed.Presented) && off.Presented.AsSpan().SequenceEqual(offAfter.Presented),
                    "diagnostic terms preserve exact disabled/failure world and final " + item.Name);
                _rows.Add(new { test = "native-sky-off-failure", name = item.Name,
                    offFinalSha256 = Convert.ToHexString(SHA256.HashData(off.Presented)),
                    failedFinalSha256 = Convert.ToHexString(SHA256.HashData(failed.Presented)),
                    offAfterFinalSha256 = Convert.ToHexString(SHA256.HashData(offAfter.Presented)) });
                RecordSkyPreCompose(item.Name + "-after-off-before-first-reenable");
                _profile.LightingSpeedMode = true; _scene.SpeedAmbientProbeTerm = 0;
                // Independent term0-only toggle witness: no scene, clock,
                // geometry or diagnostic-term mutation occurs between draws.
                using var reenabled = await CaptureSkyTerm(item.Name + "-reenabled-first-normal", 0, item.Strength, item.Budget);
                using var settled = await CaptureSkyTerm(item.Name + "-reenabled-second-normal", 0, item.Strength, item.Budget);
                RecordSkySetup(item.Name + "-term0-only-reenable", reenabled, settled);
                RecordSkySetup(item.Name + "-baseline-to-first-reenable", transitionBaseline, reenabled);
                transitionLamp.Destroy(); Mutated();
                Save();
            }
            _rows.Add(new { test = "native-sky-terms-scope", x = X, y = Y, z = _z, wall, soften = 0,
                neutral = "new-code term0 restoration; original-shader bit parity is a separate GPU proof",
                interpretation = "artistic compatibility split retains current roof/night policy; not physical GI or new brightness" }); Save();
        }
        finally
        {
            _scene.SpeedAmbientProbeTerm = 0; OrderedWorldCapture.InjectSpeedLightFailure = false;
            UltimaBatcher2D.ProbeLegacySpeedCompose = false;
            OrderedWorldDevice.ProbeLegacyAmbientOutput = false;
            _skySunMasks.Clear();
            world.ShardClock = savedClock; world.Light.Overall = savedOverall; world.Light.Personal = savedPersonal;
            _profile.UseCustomLightLevel = savedCustom;
        }
    }

    private void RecordSkySetup(string name, Shot first, Shot second)
    {
        bool rawHdrEqual = first.Light.AsSpan().SequenceEqual(second.Light);
        bool worldEqual = first.World.AsSpan().SequenceEqual(second.World);
        bool finalEqual = first.Presented.AsSpan().SequenceEqual(second.Presented);
        Check(rawHdrEqual && worldEqual && finalEqual, "first successful native frame exact HDR/world/final " + name);
        int finalChangedPixels = 0;
        for (int i = 0; i < first.Presented.Length; i += 4)
            if (first.Presented[i] != second.Presented[i] || first.Presented[i + 1] != second.Presented[i + 1]
                || first.Presented[i + 2] != second.Presented[i + 2]) finalChangedPixels++;
        _rows.Add(new { test = "native-sky-setup-observation", name, finalChangedPixels,
            rawHdrEqual, worldEqual, finalEqual, strict = true,
            sameSnapshot = ReferenceEquals(first.Snapshot, second.Snapshot), sameOffers = first.Offers == second.Offers,
            note = "First successful frame is accepted only when HDR, native world and complete final RGBA all match exactly" }); Save();
    }

    private void RecordSkyPreCompose(string name)
    {
        // Read the borrowed Texture2Drd before Capture queues any successful
        // draw. Its dimensions may be zero after off/failure retires its RD view.
        var texture = Device.SpeedLightTexture; var size = texture.GetSize();
        _rows.Add(new { test = "native-light-pre-compose-texture", name,
            width = size.X, height = size.Y, rdRidValid = texture.TextureRdRid.IsValid,
            deviceLightReady = Device.SpeedLightReady, beforeNextDraw = true,
            diagnosticTerm = _scene.SpeedAmbientProbeTerm, enhancedOption = _profile.LightingSpeedMode }); Save();
    }
}
#endif
