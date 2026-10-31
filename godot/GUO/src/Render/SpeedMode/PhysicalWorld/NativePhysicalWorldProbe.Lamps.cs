// SPDX-License-Identifier: BSD-2-Clause
// Real native lamp + dynamic door attribution. Invoked after the sun fixture.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Godot;
using GUO.Game.GameObjects;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private sealed class LampShot : IDisposable
    {
        internal Image Surface;
        internal byte[] Light, Depth, Parameters, Presented, World;
        internal PhysicalWorldSnapshot Snapshot;
        internal PhysicalLampProjection Projection;
        internal Vector2I Origin;
        internal string Offers;
        public void Dispose() => Surface?.Dispose();
    }

    private async Task<LampShot> CaptureLamp(string name)
    {
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position);
        _draw.Invoke(_game, null); await Painted();
        using var image = _targets.WorldRenderTarget.Texture.GetImage();
        image.SavePng(Path.Combine(_stills, name + ".png"));
        using var presented = GetViewport().GetTexture().GetImage();
        presented.SavePng(Path.Combine(_stills, name + "-presented.png"));
        var presentationFormat = presented.GetFormat();
        Require(presented.GetSize() == image.GetSize(), "native lamp final presentation maps receiver pixels directly");
        if (presentationFormat == Image.Format.Rgb8) presented.Convert(Image.Format.Rgba8);
        Require(presented.GetFormat() == Image.Format.Rgba8, "native lamp presentation has eight-bit RGB pixels");
        var frame = SpeedModeBindings.Current;
        Require(frame.Surface != null && frame.LightTarget != null && frame.Sequence > 0, "native lamp surface/light published " + name);
        var shot = new LampShot { Surface = frame.Surface.GetImage(), Origin = frame.Origin,
            Snapshot = Shadows.PhysicalWorld, Offers = Offers(), Presented = presented.GetData(), World = image.GetData() };
        Require(shot.Snapshot != null && ReferenceEquals(shot.Snapshot, Lights.PhysicalWorld), "native lamp shares bound physical ticket " + name);
        Exception failure = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            try {
                var pass = Shadows.StructuralLamp;
                Require(pass != null && pass.Ready && pass.SnapshotKey == shot.Snapshot.Key,
                    "native selected lamp depth matches source generation " + name);
                var rd = RenderingServer.GetRenderingDevice();
                shot.Projection = pass.Projection; shot.Light = Device.ReadSpeedLightRT();
                shot.Depth = rd.TextureGetData(pass.DepthRid, 0); shot.Parameters = rd.BufferGetData(pass.ParametersRid);
            } catch (Exception error) { failure = error; }
        }));
        await Painted(); if (failure != null) { shot.Dispose(); throw failure; }
        int offer = shot.Projection.OfferIndex;
        Require(offer >= 0 && offer < Lights.Count, "selected native lamp offer exists");
        var source = Lights.Lights[offer].Source;
        Require(source.X == X - 1 && source.Y == Y, "fixture lamp is the actual selected native offer");
        _rows.Add(new { test = "native-lamp-publication", name, sequence = frame.Sequence,
            generation = shot.Snapshot.Key.Generation, fingerprint = shot.Snapshot.SourceFingerprint,
            offer, offers = Lights.Count, source.X, source.Y, source.Z, source.Radius,
            depthHash = Convert.ToHexString(SHA256.HashData(shot.Depth)),
            knownCells = Shadows.StructuralLamp.KnownReceiverCellCount,
            presentationFormat = presentationFormat.ToString(),
            physicalSunActive = Lights.StructuralSunDepth.IsValid,
            ambientR = Lights.Ambient.X, ambientG = Lights.Ambient.Y, ambientB = Lights.Ambient.Z,
            nativeOverall = _game.UO.World.Light.Overall, nativePersonal = _game.UO.World.Light.Personal,
            emitterCell = shot.Snapshot.UnknownAt(source.X, source.Y).ToString(),
            emitterX = shot.Projection.Lamp.Position.X, emitterY = shot.Projection.Lamp.Position.Y, emitterZ = shot.Projection.Lamp.Position.Z,
            buildMilliseconds = shot.Snapshot.Diagnostics.BuildMilliseconds }); Save();
        Check(!Lights.StructuralSunDepth.IsValid, "lamp proof remains independent when sun option is off");
        return shot;
    }

    private static int LampStatus(LampShot shot, N3 point)
    {
        if (!shot.Projection.TryProject(point, out _, out var uv, out float depth)) return -1;
        int width = shot.Projection.FaceResolution * 3, height = shot.Projection.FaceResolution * 2;
        int x = Math.Clamp((int)(uv.X * width), 0, width - 1), y = Math.Clamp((int)(uv.Y * height), 0, height - 1);
        float blocker = BitConverter.ToSingle(shot.Depth, (y * width + x) * 4);
        if (blocker < depth - shot.Projection.Bias / shot.Projection.Far) return 1;
        var b = shot.Snapshot.Bounds; int cellX = (int)MathF.Floor(point.X), cellY = (int)MathF.Floor(point.Y);
        if (!b.Contains(cellX, cellY)) return -1;
        int index = (cellY - b.Y) * b.Width + cellX - b.X;
        return BitConverter.ToUInt32(shot.Parameters, StructuralLampDepthRd.ParameterHeaderBytes + index * 4) != 0 ? 0 : -1;
    }

    private ushort FindOpaqueDoor()
    {
        ref var tiles = ref _game.UO.FileManager.TileData.StaticData;
        int candidates = 0;
        for (ushort graphic = 1; graphic < Math.Min(tiles.Length, 0x4000) && candidates < 256; graphic++) {
            var td = tiles[graphic];
            if (!td.IsDoor || td.IsAnimated || td.IsTransparent || td.IsTranslucent || td.Height < 16) continue;
            candidates++;
            var art = PhysicalArtRecipe.For(graphic);
            if (art.Faces.Count == 0 || (art.Faces.First.AlongX && (art.Faces.Count < 2 || art.Faces.Second.AlongX))) continue;
            var candidate = Place(graphic, X, Y, _z);
            var source = NativePhysicalWorldProducer.Build(_game.UO.World, new(1, 1, 0), new(X, Y, 1, 1));
            bool admitted = source.Triangles.Span.ContainsOwner(new(PhysicalSourceKind.Item, candidate.Serial));
            candidate.Destroy();
            if (admitted) return graphic;
        }
        throw new InvalidOperationException("No native opaque door plane admitted among " + candidates + " candidates; cannot claim native door lighting.");
    }

    private static float LampEnergy(LampShot shot, int pixel)
    {
        float sum = 0;
        for (int channel = 0; channel < 3; channel++)
            sum += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(shot.Light, pixel * 8 + channel * 2));
        return sum;
    }

    private void CompareNativeDoor(LampShot closed, LampShot bypass, LampShot opened, uint doorSerial)
    {
        Check(closed.Offers == bypass.Offers && closed.Offers == opened.Offers, "door states preserve native lamp offers");
        Require(closed.Surface.GetData().AsSpan().SequenceEqual(bypass.Surface.GetData()), "native door bypass preserves actual receivers");
        int sourceBlocked = 0, gpuBlocked = 0, darkerThanLegacy = 0, clearAfterMove = 0, brighterAfterMove = 0, unknownAfterMove = 0;
        int visibleClosed = 0, visibleOpened = 0;
        for (int y = 0; y < closed.Surface.GetHeight(); y += 2) for (int x = 0; x < closed.Surface.GetWidth(); x += 2) {
            var p = closed.Surface.GetPixel(x, y); int packed = (int)MathF.Round(p.A);
            if ((packed & 15) != 1 || (packed & 256) == 0 || (packed & 128) != 0 || Math.Abs(p.B - _z) > .2f) continue;
            N3 receiver = new(p.R + closed.Origin.X, p.G + closed.Origin.Y, p.B);
            if (receiver.X < X - 1 || receiver.X > X + 3.5f || receiver.Y < Y - 2 || receiver.Y > Y + 2) continue;
            var ray = PhysicalWorldOracle.Trace(closed.Snapshot, receiver, closed.Projection.Lamp.Position);
            if (!ray.Blocked || ray.Owner.Kind != PhysicalSourceKind.Item || ray.Owner.Identity != doorSerial) continue;
            sourceBlocked++;
            if (LampStatus(closed, receiver) == 1) gpuBlocked++;
            int pixel = y * closed.Surface.GetWidth() + x;
            if (LampEnergy(bypass, pixel) - LampEnergy(closed, pixel) > .001f) darkerThanLegacy++;
            int displayClosed = 0;
            for (int c = 0; c < 3; c++) displayClosed += bypass.Presented[pixel * 4 + c] - closed.Presented[pixel * 4 + c];
            if (displayClosed > 2) visibleClosed++;
            var next = opened.Surface.GetPixel(x, y);
            if (((int)MathF.Round(next.A) & 15) != 1 || Math.Abs(next.R - p.R) > .01f
                || Math.Abs(next.G - p.G) > .01f || Math.Abs(next.B - p.B) > .2f) continue;
            int status = LampStatus(opened, receiver);
            if (status == -1) unknownAfterMove++;
            if (status == 0) {
                clearAfterMove++;
                if (LampEnergy(opened, pixel) - LampEnergy(closed, pixel) > .001f) brighterAfterMove++;
                int displayOpened = 0;
                for (int c = 0; c < 3; c++) displayOpened += opened.Presented[pixel * 4 + c] - closed.Presented[pixel * 4 + c];
                if (displayOpened > 2) visibleOpened++;
            }
        }
        Check(sourceBlocked >= 3 && gpuBlocked >= 3, "native closed door has independent geometric and actual GPU blockers");
        Check(darkerThanLegacy >= 3, "native structural door reduces actual HDR beyond unchanged legacy control");
        Check(clearAfterMove >= 3 && brighterAfterMove >= 3, "native moved door restores finite clear segment and actual light immediately");
        Check(visibleClosed >= 3 && visibleOpened >= 3, "native final presentation shows door blocking and restored light");
        _rows.Add(new { test = "native-door-lamp-HDR-attribution", sourceBlocked, gpuBlocked, darkerThanLegacy,
            clearAfterMove, brighterAfterMove, unknownAfterMove, visibleClosed, visibleOpened }); Save();
    }

    private void CheckLampOverlap(LampShot single, LampShot overlap)
    {
        int high = 0, stronger = 0, colourVisible = 0, exceedsNativeArt = 0, detailPairs = 0;
        Require(single.Surface.GetSize() == overlap.Surface.GetSize(), "same native overlap viewport");
        int width = overlap.Surface.GetWidth();
        for (int y = 1; y < overlap.Surface.GetHeight() - 1; y++) for (int x = 1; x < width - 1; x++) {
            var p = overlap.Surface.GetPixel(x, y); int packed = (int)MathF.Round(p.A);
            if ((packed & 15) != 1 || (packed & 256) == 0 || (packed & 128) != 0 || Math.Abs(p.B - _z) > .2f) continue;
            int pixel = y * width + x, at = pixel * 4;
            float r = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(overlap.Light, pixel * 8));
            float g = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(overlap.Light, pixel * 8 + 2));
            float b = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(overlap.Light, pixel * 8 + 4));
            float maximum = MathF.Max(r, MathF.Max(g, b)), minimum = MathF.Min(r, MathF.Min(g, b));
            int nativeMax = Math.Max(overlap.World[at], Math.Max(overlap.World[at + 1], overlap.World[at + 2]));
            int nativeMin = Math.Min(overlap.World[at], Math.Min(overlap.World[at + 1], overlap.World[at + 2]));
            if (maximum <= 1.05f || nativeMax < 100 || nativeMax > 245 || nativeMax - nativeMin > 40) continue;
            high++;
            int finalMax = Math.Max(overlap.Presented[at], Math.Max(overlap.Presented[at + 1], overlap.Presented[at + 2]));
            int finalMin = Math.Min(overlap.Presented[at], Math.Min(overlap.Presented[at + 1], overlap.Presented[at + 2]));
            if (finalMax > nativeMax + 2) exceedsNativeArt++;
            if (maximum - minimum > .1f && finalMax - finalMin > 5) colourVisible++;
            var before = single.Surface.GetPixel(x, y);
            if (Math.Abs(before.R - p.R) < .01f && Math.Abs(before.G - p.G) < .01f && Math.Abs(before.B - p.B) < .2f
                && LampEnergy(overlap, pixel) - LampEnergy(single, pixel) > .05f) stronger++;
            var next = overlap.Surface.GetPixel(x + 1, y);
            if ((int)MathF.Round(next.A) != packed || Math.Abs(next.B - p.B) > .2f) continue;
            int nativeDelta = 0, visibleDelta = 0;
            for (int c = 0; c < 3; c++) {
                nativeDelta += overlap.World[at + c] - overlap.World[at + 4 + c];
                visibleDelta += overlap.Presented[at + c] - overlap.Presented[at + 4 + c];
            }
            if (Math.Abs(nativeDelta) >= 15 && Math.Sign(nativeDelta) == Math.Sign(visibleDelta) && Math.Abs(visibleDelta) >= 3) detailPairs++;
        }
        Check(Lights.Count >= 3 && high >= 10 && stronger >= 10, "actual native overlapping lamps increase raw HDR on pale art");
        Check(exceedsNativeArt == 0 && colourVisible >= 10 && detailPairs >= 3,
            "final overlapping coloured light preserves pale native-art range and local detail");
        _rows.Add(new { test = "native-multiple-lamp-presentation", offeredLights = Lights.Count,
            high, stronger, colourVisible, exceedsNativeArt, detailPairs }); Save();
    }

    // Root may invoke this after Cases' sun assertions. It leaves the sun option
    // restored for the existing off/fallback parity sequence.
    private async Task RunLampCases()
    {
#if DEBUG
        bool savedSun = _profile.SpeedModeSunShadows; int savedLamps = _profile.SpeedModeLampShadows, savedZ = _z;
        int savedOverall = _game.UO.World.Light.Overall, savedPersonal = _game.UO.World.Light.Personal;
        bool savedCustomLight = _profile.UseCustomLightLevel;
        try {
            // A native night level leaves visible luminance headroom. At full
            // white daylight, adding a lamp changes normalized hue rather than
            // necessarily increasing brightness; that is the later overlap case.
            _profile.UseCustomLightLevel = false;
            _game.UO.World.Light.Overall = 24; _game.UO.World.Light.Personal = 0;
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            // The Britain ground varies across these cells. A test platform must
            // sit above that native terrain, or its lamp rays begin inside land.
            int highestGround = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                _game.UO.World.Map.GetMapZ(X + x, Y + y, out sbyte landZ, out _);
                highestGround = Math.Max(highestGround, landZ);
            }
            _z = highestGround + 8;
            _profile.SpeedModeSunShadows = false; _profile.SpeedModeLampShadows = 1; _profile.DrawRoofs = false;
            _game.UO.World.Player.SetInWorldTile(X - 1, Y - 1, (sbyte)_z);
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) Place(0x0495, X + x, Y + y, _z);
            ushort lampGraphic = 0x0B1D;
            Require(_game.UO.FileManager.TileData.StaticData[lampGraphic].IsLight, "native lamp metadata");
            var lamp = Place(lampGraphic, X - 1, Y, _z);
            ushort doorGraphic = FindOpaqueDoor(); Mutated();
            using var openBefore = await CaptureLamp("native-lamp-open-before");
            var door = Place(doorGraphic, X, Y, _z); Mutated();
            using var closed = await CaptureLamp("native-lamp-door-first-published-frame");
            _rows.Add(new { test = "native-door-source-triangles", graphic = doorGraphic, serial = door.Serial, platformZ = _z,
                triangles = closed.Snapshot.Triangles.ToArray().Where(t => t.Owner.Kind == PhysicalSourceKind.Item && t.Owner.Identity == door.Serial)
                    .Select(t => new { ax = t.A.X, ay = t.A.Y, az = t.A.Z, bx = t.B.X, by = t.B.Y, bz = t.B.Z,
                        cx = t.C.X, cy = t.C.Y, cz = t.C.Z }).ToArray() });
            Check(closed.Snapshot.Key.Generation > openBefore.Snapshot.Key.Generation,
                "native door closure changes first lamp source generation");
            StructuralLampDepthRd.ProbeBypass = true;
            using var bypass = await CaptureLamp("native-lamp-door-structural-bypass");
            StructuralLampDepthRd.ProbeBypass = false;
            door.SetInWorldTile(X, Y + 3, (sbyte)_z); Mutated();
            using var opened = await CaptureLamp("native-lamp-door-moved-first-published-frame");
            Check(opened.Snapshot.Key.Generation > closed.Snapshot.Key.Generation
                && opened.Snapshot.SourceFingerprint != closed.Snapshot.SourceFingerprint,
                "native door movement invalidates first lamp depth generation");
            CompareNativeDoor(closed, bypass, opened, door.Serial);
            _game.UO.World.Light.Overall = savedOverall; _game.UO.World.Light.Personal = savedPersonal;
            _profile.UseCustomLightLevel = savedCustomLight;
            using var daylightSingle = await CaptureLamp("native-single-lamp-daylight-overlap-control");
            Place(lampGraphic, X - 1, Y + 1, _z); Place(lampGraphic, X - 2, Y + 1, _z); Mutated();
            using var overlap = await CaptureLamp("native-three-lamp-overlap-presented");
            CheckLampOverlap(daylightSingle, overlap);
            _rows.Add(new { test = "native-lamp-fixture", lampGraphic, lampSerial = lamp.Serial, doorGraphic, platformZ = _z,
                doorSerial = door.Serial, sun = false, selectedLampBudget = 1, liveServer = false }); Save();
        } finally {
            StructuralLampDepthRd.ProbeBypass = false;
            _profile.SpeedModeSunShadows = savedSun; _profile.SpeedModeLampShadows = savedLamps;
            _game.UO.World.Light.Overall = savedOverall; _game.UO.World.Light.Personal = savedPersonal;
            _profile.UseCustomLightLevel = savedCustomLight;
            _z = savedZ;
        }
#endif
    }
}
