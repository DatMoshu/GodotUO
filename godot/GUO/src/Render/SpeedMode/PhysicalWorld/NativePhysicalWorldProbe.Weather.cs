// SPDX-License-Identifier: BSD-2-Clause
// Actual native canopy, packet-driven weather and final emitted canvas effects.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Network;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private int _weatherProbeRainMask;
    private bool _weatherProbeSuppressWet, _weatherProbeSuppressShelter;
    private long _weatherProbeHookSequence;
    private SpeedModeWeatherRuntime WeatherRuntime => Field<SpeedModeWeatherRuntime>(_scene, "_speedWeatherRuntime");

    private void WeatherPacket(WeatherType type, byte count)
    {
        byte[] bytes = { 0x65, (byte)type, count, 15 };
        PacketHandlers.Handler.Append(bytes, true);
        int packets = PacketHandlers.Handler.ParsePackets(NetClient.Socket, _game.UO.World, Span<byte>.Empty);
        Require(packets == 1 && _game.UO.World.Weather.Count == count, "actual framed native weather packet accepted");
        bool clear = type == WeatherType.WT_INVALID_0 || type == WeatherType.WT_INVALID_1;
        Require(clear ? !_game.UO.World.Weather.CurrentWeather.HasValue : _game.UO.World.Weather.CurrentWeather == type,
            "native weather packet selects requested active/clear state");
        _rows.Add(new { test = "emitted-weather-packet", type = type.ToString(), count, packets,
            activeType = _game.UO.World.Weather.CurrentWeather?.ToString(), tick = Time.Ticks });
    }

    // Registered after the actual runtime, so source isolation happens after
    // its normal same-ticket Bind. No receiver, status, artwork or draw is authored.
    private void IsolateWeatherComponents(SpeedModeBindings.Frame frame)
    {
        var runtime = WeatherRuntime;
        if (runtime == null || !runtime.Bound || runtime.BoundSequence != frame.Sequence) return;
        var rain = Field<SpeedModeRainPass>(runtime, "_rain");
        var wet = Field<SpeedModeWetPass>(runtime, "_wet");
        if (rain.Material is ShaderMaterial rainMaterial)
        {
            rainMaterial.SetShaderParameter("component_mask", _weatherProbeRainMask);
            if (_weatherProbeSuppressShelter) rainMaterial.SetShaderParameter("shelter_ready", false);
        }
        if (wet.Material is ShaderMaterial wetMaterial)
        {
            if (_weatherProbeSuppressWet) wetMaterial.SetShaderParameter("wetness", 0f);
            if (_weatherProbeSuppressShelter) wetMaterial.SetShaderParameter("shelter_ready", false);
        }
        _weatherProbeHookSequence = frame.Sequence;
    }

    private async Task<Shot> CaptureWeather(string name)
    {
        var shot = await Capture(name);
        var runtime = WeatherRuntime; var frame = SpeedModeBindings.Current;
        Require(runtime != null && runtime.Bound && runtime.BoundSequence == shot.Sequence
            && _weatherProbeHookSequence == shot.Sequence && frame.Sequence == shot.Sequence,
            "actual weather canvas consumed this published ticket " + name);
        Require(frame.Shelter != null && ReferenceEquals(frame.Shelter, Lights.PhysicalShelter),
            "weather borrows actual same-ticket shelter " + name);
        var rain = Field<SpeedModeRainPass>(runtime, "_rain");
        var wet = Field<SpeedModeWetPass>(runtime, "_wet");
        Require(rain.Active && rain.Material is ShaderMaterial && wet.Material is ShaderMaterial,
            "actual native rain and wet materials exist " + name);
        var rainMaterial = (ShaderMaterial)rain.Material;
        var wetMaterial = (ShaderMaterial)wet.Material;
        Require(ReferenceEquals(rainMaterial.GetShaderParameter("shelter_status").AsGodotObject(), frame.Shelter)
            && ReferenceEquals(wetMaterial.GetShaderParameter("shelter_status").AsGodotObject(), frame.Shelter),
            "both actual weather shaders bind the physical shelter texture " + name);
        Require(rainMaterial.GetShaderParameter("shelter_ready").AsBool() != _weatherProbeSuppressShelter
            && wetMaterial.GetShaderParameter("shelter_ready").AsBool() != _weatherProbeSuppressShelter,
            "weather shelter source isolation matches request " + name);
        Require(shot.World.Length == shot.Surface.GetWidth() * shot.Surface.GetHeight() * 4,
            "world-target RGBA8 layout verified for weather comparison");
        _rows.Add(new { test = "emitted-weather-publication", name, sequence = shot.Sequence,
            physicalGeneration = shot.Snapshot.Key.Generation, precipitationCount = _game.UO.World.Weather.Count,
            rainIntensity = rainMaterial.GetShaderParameter("intensity").AsSingle(),
            wetness = wetMaterial.GetShaderParameter("wetness").AsSingle(), mask = _weatherProbeRainMask,
            shelterEnabled = !_weatherProbeSuppressShelter, tick = Time.Ticks,
            captures = "WorldRenderTarget RGBA8 and final viewport RGBA8; both independently compared" });
        Save(); return shot;
    }

    private byte[] WeatherRegions(Shot shot, byte[] status, HashSet<int> canopyPixels)
    {
        int width = shot.Surface.GetWidth(); var mask = new byte[status.Length];
        for (int y = 0; y < shot.Surface.GetHeight(); y++)
        for (int x = 0; x < width; x++)
        {
            int index = y * width + x; var p = shot.Surface.GetPixel(x, y);
            int packed = (int)MathF.Round(p.A), kind = packed & 15;
            float wx = p.R + shot.Origin.X, wy = p.G + shot.Origin.Y;
            if (wx < X - 5 || wx > X + 5 || wy < Y - 5 || wy > Y + 5 || kind == 0) continue;
            if (status[index] == 0) { mask[index] = 3; continue; }
            if (kind != 1 || (packed & 256) == 0 || Math.Abs(p.B - _z) > .2f) continue;
            // Stay inside actual tile pixels, away from raster/alpha seams.
            float fx = wx - MathF.Floor(wx), fy = wy - MathF.Floor(wy);
            if (fx < .05f || fx > .95f || fy < .05f || fy > .95f) continue;
            // Protected witnesses also passed the independent CPU ray against
            // the actual fixture roof owner. Nearby town roofs are not removed.
            if (status[index] == 255) { if (canopyPixels.Contains(index)) mask[index] = 1; }
            else if (status[index] == 128) mask[index] = 2;
        }
        return mask;
    }

    private (int[] World, int[] Presented) WeatherChanges(string name, Shot baseline, Shot actual, byte[] mask)
    {
        Require(baseline.Origin == actual.Origin && baseline.Surface.GetData().AsSpan().SequenceEqual(actual.Surface.GetData())
            && ReferenceEquals(baseline.Snapshot, actual.Snapshot), "same native receiver/source for weather-only comparison " + name);
        int[] samples = new int[4], world = new int[4], presented = new int[4];
        for (int pixel = 0; pixel < mask.Length; pixel++)
        {
            int region = mask[pixel]; if (region == 0) continue; samples[region]++;
            int p = pixel * 4;
            if (baseline.World[p] != actual.World[p] || baseline.World[p + 1] != actual.World[p + 1]
                || baseline.World[p + 2] != actual.World[p + 2]) world[region]++;
            if (baseline.Presented[p] != actual.Presented[p] || baseline.Presented[p + 1] != actual.Presented[p + 1]
                || baseline.Presented[p + 2] != actual.Presented[p + 2]) presented[region]++;
        }
        _rows.Add(new { test = "emitted-weather-difference", name,
            regionOrder = new[] { "unused", "protected native floor", "exposed native floor", "unknown native receiver" },
            samples, worldTargetChanged = world, finalViewportChanged = presented,
            baselineSequence = baseline.Sequence, actualSequence = actual.Sequence, tick = Time.Ticks });
        Save(); return (world, presented);
    }

    private async Task RunEmittedWeatherCases()
    {
        var world = _game.UO.World; var player = world.Player;
        int savedZ = _z, px = player.X, py = player.Y, pz = player.Z;
        uint savedTick = Time.Ticks;
        bool weather = _profile.SpeedModeWeather, sun = _profile.SpeedModeSunShadows, roofsVisible = _profile.DrawRoofs;
        bool subscribed = false;
        var roofs = new List<Item>();
        try
        {
            Require(world.Weather.Count == 0, "emitted weather fixture starts from dry native weather");
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            int highest = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++)
            {
                world.Map.GetMapZ(X + x, Y + y, out sbyte land, out _); highest = Math.Max(highest, land);
            }
            _z = highest + 8; _profile.SpeedModeWeather = true; _profile.SpeedModeSunShadows = true;
            _profile.DrawRoofs = false; player.SetInWorldTile(X, Y - 2, (sbyte)_z);
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) Place(0x0495, X + x, Y + y, _z);
            var recipe = FindRoof();
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                var roof = Place(recipe.Graphic, X + x, Y + y, _z + 25 + (x + 1) * recipe.Gx + (y + 1) * recipe.Gy);
                roof.AlphaHue = 0; roofs.Add(roof);
            }
            Mutated();
            using (var initialized = await Capture("emitted-weather-canopy-bootstrap")) { }
            Require(WeatherRuntime != null, "production weather runtime initialized before isolation hook");
            SpeedModeBindings.LightReady += IsolateWeatherComponents; subscribed = true;
            WeatherPacket(WeatherType.WT_RAIN, 70);
            SpeedModeWeather.Observe(world, _profile);
            // Advance the normal visual integrator by 60s, within native weather's
            // six-minute expiry. Same-type packets are intentionally ignored by
            // the native handler, so this does not send a pretend refresh. Paired draws
            // below freeze Time.Ticks; no shader time or wetness is fabricated.
            Time.Ticks += 60000;
            _weatherProbeRainMask = 0; _weatherProbeSuppressWet = false;
            using var hidden = await CaptureWeather("emitted-weather-canopy-hidden");
            var canopyPixels = await CheckNativeShelter("emitted-native-canopy", hidden, roofs.Select(r => (ulong)r.Serial).ToHashSet());
            byte[] hiddenStatus = await ShelterBytes("emitted-native-canopy-full", hidden);
            byte[] regions = WeatherRegions(hidden, hiddenStatus, canopyPixels.ToHashSet());
            Require(regions.Count(r => r == 1) >= 100 && regions.Count(r => r == 2) >= 100,
                "substantial actual protected and exposed native floor witnesses");
            Require(regions.Any(r => r == 3), "actual unknown receiver witness for legacy fallback");

            uint phaseStart = Time.Ticks; int exposedWorld = 0, exposedFinal = 0;
            for (int phase = 0; phase < 6; phase++)
            {
                Time.Ticks = phaseStart + (uint)(phase * 173);
                _weatherProbeRainMask = 0;
                using var withoutRain = await CaptureWeather("emitted-canopy-rain-off-" + phase);
                _weatherProbeRainMask = 7;
                using var withRain = await CaptureWeather("emitted-canopy-rain-on-" + phase);
                var diff = WeatherChanges("rain-only-" + phase, withoutRain, withRain, regions);
                Check(diff.World[1] == 0 && diff.Presented[1] == 0, "physical canopy blocks emitted rain on native protected pixels " + phase);
                exposedWorld += diff.World[2]; exposedFinal += diff.Presented[2];
            }
            Check(exposedWorld > 0 && exposedFinal > 0, "actual rain emits on exposed native pixels in both captured targets");

            _weatherProbeRainMask = 0; _weatherProbeSuppressWet = true;
            using var dry = await CaptureWeather("emitted-canopy-wet-off");
            _weatherProbeSuppressWet = false;
            using var wet = await CaptureWeather("emitted-canopy-wet-on");
            var wetDiff = WeatherChanges("wet-only", dry, wet, regions);
            Check(wetDiff.World[1] == 0 && wetDiff.Presented[1] == 0, "physical canopy protects actual wet shader pixels");
            Check(wetDiff.World[2] > 0 && wetDiff.Presented[2] > 0, "actual wet effect changes exposed native pixels");

            _weatherProbeRainMask = 7;
            using var known = await CaptureWeather("emitted-canopy-physical-shelter");
            _weatherProbeSuppressShelter = true;
            using var fallback = await CaptureWeather("emitted-canopy-legacy-cover-control");
            var unknown = WeatherChanges("unknown-keeps-legacy-cover", known, fallback, regions);
            Check(unknown.World[3] == 0 && unknown.Presented[3] == 0, "unknown shelter preserves actual legacy rain/wet fallback exactly");
            _weatherProbeSuppressShelter = false;

            // The first changed draw emits rain. Its matched no-rain control is
            // drawn afterwards, so no warm frame hides the mutation result.
            foreach (var roof in roofs) roof.Destroy(); Mutated();
            using var removed = await CaptureWeather("emitted-canopy-removed-first-published-frame");
            byte[] removedStatus = await ShelterBytes("emitted-canopy-removed", removed);
            Check(removed.Snapshot.Key.Generation > hidden.Snapshot.Key.Generation
                && roofs.All(r => !Contains(removed, r)), "removed native canopy retires in first emitted-weather generation");
            int restored = 0, sameProtectedReceiver = 0, width = removed.Surface.GetWidth();
            for (int i = 0; i < regions.Length; i++) if (regions[i] == 1)
            {
                var old = hidden.Surface.GetPixel(i % width, i / width);
                var current = removed.Surface.GetPixel(i % width, i / width);
                if (old.R != current.R || old.G != current.G || old.B != current.B) continue;
                sameProtectedReceiver++; if (removedStatus[i] == 128) restored++;
            }
            Check(sameProtectedReceiver == regions.Count(r => r == 1), "removed canopy retains the same protected native floor coordinates");
            Check(restored == regions.Count(r => r == 1), "every protected native floor becomes exposed on first removed-canopy frame");
            _weatherProbeRainMask = 0;
            using var removedControl = await CaptureWeather("emitted-canopy-removed-rain-off-control");
            var released = WeatherChanges("first-removal-emitted-rain", removedControl, removed, regions);
            Check(released.World[1] > 0 && released.Presented[1] > 0,
                "first removed-canopy frame restores emitted rain on the previously protected native pixels");

            _profile.SpeedModeWeather = false;
            using (var weatherOff = await Capture("emitted-weather-option-off"))
            {
                Check(SpeedModeBindings.Current.Shelter == null && Lights.PhysicalShelter == null
                    && !Field<SpeedModeRainPass>(WeatherRuntime, "_rain").Active,
                    "weather option off retires physical shelter and procedural rain");
            }
            _profile.LightingSpeedMode = false;
            using var classic = await Capture("emitted-weather-main-off-classic", false);
            _profile.SpeedModeWeather = true; _profile.LightingSpeedMode = true;
            OrderedWorldCapture.InjectSpeedLightFailure = true;
            using var failed = await Capture("emitted-weather-light-failure-classic", false);
            Check(classic.World.AsSpan().SequenceEqual(failed.World)
                && classic.Presented.AsSpan().SequenceEqual(failed.Presented),
                "light failure restores exact classic precipitation in world target and final viewport");
            Check(WeatherRuntime == null || !WeatherRuntime.Bound, "failed light ticket leaves no active weather consumer");
            // Frozen native timing can shorten line segments. Equality alone
            // therefore is not a visible-precipitation witness: remove rain via
            // the real packet and demand an actual positive image difference.
            OrderedWorldCapture.InjectSpeedLightFailure = false;
            // Native 0x65 ignores count changes while the type stays Rain. Its
            // 0xFE clear type resets precipitation and CurrentWeather instead.
            _profile.LightingSpeedMode = false; WeatherPacket(WeatherType.WT_INVALID_0, 0);
            using var classicDry = await Capture("emitted-weather-classic-clear-packet-control", false);
            int classicWorldChanged = 0, classicFinalChanged = 0;
            for (int i = 0; i < classic.World.Length; i += 4)
            {
                if (classic.World[i] != classicDry.World[i] || classic.World[i + 1] != classicDry.World[i + 1]
                    || classic.World[i + 2] != classicDry.World[i + 2]) classicWorldChanged++;
                if (classic.Presented[i] != classicDry.Presented[i] || classic.Presented[i + 1] != classicDry.Presented[i + 1]
                    || classic.Presented[i + 2] != classicDry.Presented[i + 2]) classicFinalChanged++;
            }
            Check(classicWorldChanged > 0 && classicFinalChanged > 0,
                "restored classic native precipitation visibly differs from native clear-weather packet control");
            _rows.Add(new { test = "emitted-weather-summary", exposedWorld, exposedFinal, sameProtectedReceiver, restored,
                classicWorldChanged, classicFinalChanged,
                source = "actual Item canopy and native 0x65 rain", comparisons = "world target and final viewport",
                windModel = "vertical shelter applied to screen-space procedural rain; directional weather collision not claimed" });
            Save();
        }
        finally
        {
            if (subscribed) SpeedModeBindings.LightReady -= IsolateWeatherComponents;
            OrderedWorldCapture.InjectSpeedLightFailure = false;
            _weatherProbeRainMask = 7; _weatherProbeSuppressWet = _weatherProbeSuppressShelter = false;
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            world.Weather.Reset(); SpeedModeWeather.Observe(world, _profile).Advance(null, 0, 0, 0, 180);
            Time.Ticks = savedTick; _z = savedZ;
            _profile.LightingSpeedMode = true; _profile.SpeedModeWeather = weather;
            _profile.SpeedModeSunShadows = sun; _profile.DrawRoofs = roofsVisible;
            player.SetInWorldTile((ushort)px, (ushort)py, (sbyte)pz); Mutated();
        }
    }
}
#endif
