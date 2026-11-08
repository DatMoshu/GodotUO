// SPDX-License-Identifier: BSD-2-Clause
// Same-ticket native weather shelter status; no manually authored receiver pixels.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Game.GameObjects;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private async Task<byte[]> ShelterBytes(string name, Shot shot)
    {
        var frame = SpeedModeBindings.Current;
        Require(frame.Sequence == shot.Sequence && frame.Shelter != null
            && ReferenceEquals(frame.Shelter, Lights.PhysicalShelter), "shelter published with actual same-frame native light " + name);
        byte[] status = null; Exception failure = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            try {
                var pass = Shadows.Shelter;
                Require(pass != null && pass.Ready && pass.SnapshotKey == shot.Snapshot.Key,
                    "shelter uses exact bound physical generation " + name);
                status = RenderingServer.GetRenderingDevice().TextureGetData(pass.StatusRid, 0);
            } catch (Exception error) { failure = error; }
        }));
        await Painted(); if (failure != null) throw failure;
        Require(status?.Length == shot.Surface.GetWidth() * shot.Surface.GetHeight(), "native shelter R8 dimensions " + name);
        using var image = Image.CreateFromData(shot.Surface.GetWidth(), shot.Surface.GetHeight(), false, Image.Format.L8, status);
        image.SavePng(Path.Combine(_stills, name + "-shelter.png"));
        return status;
    }

    private async Task<List<int>> CheckNativeShelter(string name, Shot shot, HashSet<ulong> roofOwners)
    {
        byte[] status = await ShelterBytes(name, shot);
        int protectedExpected = 0, protectedActual = 0, exposedExpected = 0, exposedActual = 0, unknownPixels = 0, unknownWrong = 0;
        var protectedPixels = new List<int>(); int width = shot.Surface.GetWidth();
        for (int y = 0; y < shot.Surface.GetHeight(); y += 3) for (int x = 0; x < width; x += 3) {
            int index = y * width + x;
            var pixel = shot.Surface.GetPixel(x, y); int packed = (int)MathF.Round(pixel.A);
            if ((packed & 256) == 0 || (packed & 128) != 0) {
                unknownPixels++; if (status[index] != 0) unknownWrong++; continue;
            }
            if ((packed & 15) != 1) continue;
            N3 p = new(pixel.R + shot.Origin.X, pixel.G + shot.Origin.Y, pixel.B);
            if (p.X < X - 4 || p.X > X + 4 || p.Y < Y - 3 || p.Y > Y + 3) continue;
            // Keep the independent ray away from finite tile boundaries and the
            // native floor's own raster edge; no arbitrary upper height is used.
            if (p.X - MathF.Floor(p.X) < .05f || p.X - MathF.Floor(p.X) > .95f
                || p.Y - MathF.Floor(p.Y) < .05f || p.Y - MathF.Floor(p.Y) > .95f) continue;
            var ray = PhysicalWorldOracle.Trace(shot.Snapshot, p, new(p.X, p.Y, PhysicalShelterPassRd.MaximumZ), PhysicalMaterial.BlocksWeather);
            if (ray.Blocked && ray.Owner.Kind == PhysicalSourceKind.Item && roofOwners.Contains(ray.Owner.Identity)) {
                protectedExpected++; if (status[index] == 255) { protectedActual++; protectedPixels.Add(index); }
            } else if (ray.Known && !ray.Blocked) { exposedExpected++; if (status[index] == 128) exposedActual++; }
        }
        Check(protectedExpected >= 3 && protectedActual == protectedExpected, "hidden native roof protects actual lower receiver " + name);
        Check(exposedExpected >= 3 && exposedActual == exposedExpected, "known exterior native receiver stays exposed " + name);
        Check(unknownPixels > 0 && unknownWrong == 0, "unqualified native pixels retain unknown shelter " + name);
        _rows.Add(new { test = "native-shelter-status", name, generation = shot.Snapshot.Key.Generation,
            sequence = shot.Sequence, protectedExpected, protectedActual, exposedExpected, exposedActual, unknownPixels, unknownWrong,
            precipitationCount = _game.UO.World.Weather.Count }); Save();
        return protectedPixels;
    }

    private async Task CheckShelterRemoved(Shot before, Shot removed, List<int> protectedPixels)
    {
        byte[] status = await ShelterBytes("roof-removed-first-published-frame", removed);
        int sameReceiver = 0, nowExposed = 0;
        int width = removed.Surface.GetWidth();
        foreach (int index in protectedPixels) {
            int x = index % width, y = index / width;
            var old = before.Surface.GetPixel(x, y); var current = removed.Surface.GetPixel(x, y);
            if (Math.Abs(old.R - current.R) > .01f || Math.Abs(old.G - current.G) > .01f || Math.Abs(old.B - current.B) > .2f) continue;
            sameReceiver++; if (status[index] == 128) nowExposed++;
        }
        Check(sameReceiver >= 3 && nowExposed == sameReceiver, "removed roof clears shelter on first changed frame for the same native pixels");
        _rows.Add(new { test = "native-shelter-roof-removal", oldGeneration = before.Snapshot.Key.Generation,
            newGeneration = removed.Snapshot.Key.Generation, sameReceiver, nowExposed }); Save();
    }

    private async Task RunShelterCases()
    {
#if DEBUG
        int savedZ = _z; bool savedWeather = _profile.SpeedModeWeather;
        try {
            // The sun fixture's tall wall occludes the floor beneath its roof.
            // Use an independent native canopy so protection has visible lower
            // receivers as well as the existing exposed and unknown controls.
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            int highestGround = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                _game.UO.World.Map.GetMapZ(X + x, Y + y, out sbyte landZ, out _);
                highestGround = Math.Max(highestGround, landZ);
            }
            _z = highestGround + 8;
            _profile.SpeedModeWeather = true; _profile.DrawRoofs = false;
            Require(_game.UO.World.Weather.Count == 0, "native shelter status is independent of live precipitation");
            _game.UO.World.Player.SetInWorldTile(X, Y - 2, (sbyte)_z);
            Item floorWitness = null;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                var floor = Place(0x0495, X + x, Y + y, _z);
                if (x == 0 && y == 0) floorWitness = floor;
            }
            await CheckMovingReceiver(floorWitness, 1, "native-floor");
            var recipe = FindRoof(); var roofs = new List<Item>();
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                var roof = Place(recipe.Graphic, X + x, Y + y, _z + 25 + (x + 1) * recipe.Gx + (y + 1) * recipe.Gy);
                roof.AlphaHue = 0; roofs.Add(roof);
            }
            Mutated();
            using var hidden = await Capture("native-canopy-hidden-first-published-frame");
            var protectedPixels = await CheckNativeShelter("hidden-native-canopy", hidden,
                roofs.Select(roof => (ulong)roof.Serial).ToHashSet());
            // Enter a real elevated viewing floor to see the native roof, then
            // return to the identical lower view for shelter-removal comparison.
            var upperDeck = Place(0x0495, X, Y - 2, _z + 50);
            _game.UO.World.Player.SetInWorldTile(X, Y - 2, (sbyte)(_z + 50));
            _profile.DrawRoofs = true; foreach (var roof in roofs) roof.AlphaHue = 255;
            await CheckMovingReceiver(roofs[0], 5, "native-roof");
            _game.UO.World.Player.SetInWorldTile(X, Y - 2, (sbyte)_z);
            upperDeck.Destroy(); _profile.DrawRoofs = false;
            foreach (var roof in roofs) roof.Destroy(); Mutated();
            using var removed = await Capture("native-canopy-removed-first-published-frame");
            Check(removed.Snapshot.Key.Generation > hidden.Snapshot.Key.Generation,
                "native canopy removal advances first bound physical generation");
            await CheckShelterRemoved(hidden, removed, protectedPixels);
            _profile.SpeedModeWeather = false;
            using var disabled = await Capture("native-shelter-disabled-first-published-frame");
            Check(SpeedModeBindings.Current.Shelter == null && Lights.PhysicalShelter == null,
                "disabled weather retires published native shelter on the next frame");
        } finally { _profile.SpeedModeWeather = savedWeather; _z = savedZ; }
#endif
    }
}
