// SPDX-License-Identifier: BSD-2-Clause
// Offline Britain proof: actual native items, receiver target and bound physical ticket.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Network;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode.Shadows;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe : Node
{
    private const int X = 1417, Y = 1734;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<object> _rows = new();
    private readonly List<string> _failures = new();
    private readonly List<Item> _items = new();
    private string _root, _stills;
    private GUO.GameController _game;
    private GameScene _scene;
    private Profile _profile;
    private RenderTargets _targets;
    private UltimaBatcher2D _batcher;
    private MethodInfo _draw;
    private uint _serial = 0x40010000;
    private int _z;

    public override void _EnterTree()
    {
#if DEBUG
        _root = System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");
        Require(!string.IsNullOrWhiteSpace(_root) && Path.IsPathRooted(_root), "external GUO_PHYSICAL_WORLD_OUT required");
        _root = Path.GetFullPath(_root);
        if (System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_PERF") == "1")
            Require(!File.Exists(Path.Combine(_root, "data", "native-physical-world-results.json"))
                && !File.Exists(Path.Combine(_root, "data", "native-physical-performance.json")),
                "performance diagnostics require a new output run; prior acceptance artifacts are immutable");
        string project = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        Require(!_root.StartsWith(project, StringComparison.OrdinalIgnoreCase), "evidence must stay outside project");
        string scratch = Path.Combine(_root, "data", "fixture");
        _stills = Path.Combine(_root, "stills");
        Directory.CreateDirectory(scratch); Directory.CreateDirectory(_stills);
        // This runs before the child Main. No owner settings, profiles or cache are opened for writing.
        GUO.Workspace.Workspace.RootOverride = Path.Combine(scratch, "workspace");
        System.Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR", GUO.Workspace.Workspace.RootOverride);
        System.Environment.SetEnvironmentVariable("UO_CACHE_DIR", Path.Combine(scratch, "cache"));
        Settings.CustomSettingsFilepath = Path.Combine(scratch, "settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath, JsonSerializer.Serialize(new {
            autologin = false, saveaccount = false, login_music = false, is_win_maximized = false,
            profilespath = Path.Combine(scratch, "profiles"), plugins = Array.Empty<string>() }));
#endif
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private).GetValue(value);
    private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private void Check(bool ok, string reason) { if (!ok) _failures.Add(reason); }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task Painted() => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    private SpeedLightFrame Lights => Field<SpeedLightFrame>(_scene, "_speedLights");
    private SpeedShadowInputs Shadows => Field<SpeedShadowInputs>(_scene, "_speedShadows");
    private OrderedWorldDevice Device => Field<OrderedWorldDevice>(_batcher.OrderedWorldProbe, "_device");
    private StructuralSunDepthRd StructuralPass => Shadows.StructuralSun;
    private void Save() => File.WriteAllText(Path.Combine(_root, "data", "native-physical-world-results.json"),
        JsonSerializer.Serialize(new { disconnected = !NetClient.Socket.IsConnected, rows = _rows, failures = _failures },
            new JsonSerializerOptions { WriteIndented = true }));

    private sealed class Shot : IDisposable
    {
        internal Image Surface;
        internal byte[] World, Light, Presented;
        internal PhysicalWorldSnapshot Snapshot;
        internal PhysicalSunProjection Projection;
        internal Vector2I Origin;
        internal string Offers, DepthHash;
        internal long Sequence;
        public void Dispose() => Surface?.Dispose();
    }

    private string Offers() => JsonSerializer.Serialize(Enumerable.Range(0, Lights.Count).Select(i => {
        var e = Lights.Lights[i]; var s = e.Source;
        return new { s.X, s.Y, s.Z, s.Radius, s.OffsetX, s.OffsetY, s.Energy,
            curve = s.Curve.ToString(), colour = s.Color.ToString(), e.Flicker, e.PixelCentre, e.PixelRadius };
    }));

    private async Task<Shot> Capture(string name, bool requirePhysical = true)
    {
        _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position);
        _draw.Invoke(_game, null);
        await Painted();
        using var world = _targets.WorldRenderTarget.Texture.GetImage();
        world.SavePng(Path.Combine(_stills, name + ".png"));
        using var presented = GetViewport().GetTexture().GetImage();
        presented.SavePng(Path.Combine(_stills, name + "-presented.png"));
        var presentationFormat = presented.GetFormat();
        Require(presented.GetSize() == world.GetSize(), "fullsize native viewport maps world receiver pixels directly " + name);
        if (presentationFormat == Image.Format.Rgb8) presented.Convert(Image.Format.Rgba8);
        Require(presented.GetFormat() == Image.Format.Rgba8, "native viewport has an eight-bit RGB presentation " + name);
        _rows.Add(new { test = "presentation-readback", name, width = presented.GetWidth(), height = presented.GetHeight(),
            originalFormat = presentationFormat.ToString(), comparisonFormat = presented.GetFormat().ToString() });
        var shot = new Shot { World = world.GetData(), Presented = presented.GetData() };
        if (!requirePhysical) return shot;
        var frame = SpeedModeBindings.Current;
        Require(frame.Sequence > 0 && frame.Surface != null && frame.LightTarget != null, "same native light/surface publication " + name);
        shot.Surface = frame.Surface.GetImage(); shot.Origin = frame.Origin; shot.Sequence = frame.Sequence;
        shot.Snapshot = Shadows.PhysicalWorld;
        Require(shot.Snapshot != null && ReferenceEquals(shot.Snapshot, Lights.PhysicalWorld), "bound physical ticket shared by light producer " + name);
        shot.Offers = Offers();
        Exception failure = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            try {
                Require(Device.SpeedLightReady, "native HDR target ready " + name);
                var pass = StructuralPass;
                Require(pass != null && pass.Ready && pass.SnapshotKey == shot.Snapshot.Key,
                    "structural depth belongs to published physical generation " + name);
                shot.Projection = pass.Projection;
                shot.Light = Device.ReadSpeedLightRT();
                byte[] depth = RenderingServer.GetRenderingDevice().TextureGetData(pass.DepthRid, 0);
                shot.DepthHash = Convert.ToHexString(SHA256.HashData(depth));
                _rows.Add(new { test = "publication", name, sequence = shot.Sequence,
                    world = shot.Snapshot.Key.World, generation = shot.Snapshot.Key.Generation,
                    fingerprint = shot.Snapshot.SourceFingerprint, triangles = pass.TriangleCount,
                    buildMilliseconds = shot.Snapshot.Diagnostics.BuildMilliseconds,
                    cacheHits = _scene.SpeedPhysicalCacheHits, cacheBuilds = _scene.SpeedPhysicalCacheBuilds,
                    knownCells = pass.KnownReceiverCellCount, unknownCells = pass.UnknownReceiverCellCount,
                    depthHash = shot.DepthHash, uploads = pass.GeometryUploads, draws = pass.DepthDraws });
            } catch (Exception error) { failure = error; }
        }));
        await Painted();
        if (failure != null) { shot.Dispose(); throw failure; }
        Require(shot.Light.Length == shot.Surface.GetWidth() * shot.Surface.GetHeight() * 8, "RGBA16F native HDR layout");
        Save(); return shot;
    }

    private Item Place(ushort graphic, int x, int y, int z)
    {
        // Prime the real decoded image before the mutation. The first changed draw
        // still has to rebuild native geometry; there is no authored receiver target.
        Require(_game.UO.Arts.GetArt(graphic).Texture != null, "native source art " + graphic.ToString("X4"));
        var item = _game.UO.World.GetOrCreateItem(_serial++);
        item.Graphic = graphic; item.Amount = 1;
        item.SetInWorldTile((ushort)x, (ushort)y, (sbyte)z);
        item.CheckGraphicChange(); item.AlphaHue = 255; _items.Add(item); return item;
    }

    private void Mutated()
    {
        _scene.UpdateDrawPosition = true;
        _scene.UpdateMaxDrawZ(true);
        _scene.Update();
        // No ProcessFrame, warm draw or polling is allowed between this mutation
        // and the caller's Capture: it is the first actual published changed draw.
    }

    private static bool Contains(Shot shot, Item item) => shot.Snapshot.Triangles.Span
        .ContainsOwner(new PhysicalOwnerKey(PhysicalSourceKind.Item, item.Serial));

    private ushort FindWall()
    {
        ref var tiles = ref _game.UO.FileManager.TileData.StaticData;
        int candidates = 0;
        for (ushort id = 1; id < Math.Min(tiles.Length, 0x4000) && candidates < 256; id++) {
            var tile = tiles[id];
            if (!tile.IsWall || tile.IsWindow || tile.IsDoor || tile.IsTransparent || tile.IsTranslucent || tile.Height < 16 || tile.Height > 30) continue;
            candidates++;
            if (_game.UO.Arts.GetArt(id).Texture == null) continue;
            var item = Place(id, X - 2, Y, _z);
            var snapshot = NativePhysicalWorldProducer.Build(_game.UO.World, new(1, 1, 0), new(X - 2, Y, 1, 1));
            bool admitted = snapshot.Triangles.Span.ContainsOwner(new(PhysicalSourceKind.Item, item.Serial));
            item.Destroy();
            if (admitted) return id;
        }
        throw new InvalidOperationException("No native opaque wall face admitted among " + candidates + " candidates; cannot claim wall shadow proof.");
    }

    private (ushort Graphic, int Gx, int Gy) FindRoof()
    {
        var ids = new List<ushort> { 0x05E4 };
        ref var tiles = ref _game.UO.FileManager.TileData.StaticData;
        for (ushort id = 1; id < Math.Min(tiles.Length, 0x4000); id++)
            if (id != 0x05E4 && tiles[id].IsRoof && !tiles[id].IsAnimated && !tiles[id].IsTransparent && !tiles[id].IsTranslucent) ids.Add(id);
        var gradients = new[] { (0, 0), (3, 0), (-3, 0), (0, 3), (0, -3), (4, 0), (-4, 0), (0, 4), (0, -4), (6, 0), (-6, 0), (0, 6), (0, -6) };
        foreach (ushort id in ids.Take(256)) foreach ((int gx, int gy) in gradients) {
            var fit = PhysicalReceiverRecipe.FitRoof(id, gx, gy);
            if (!fit.Qualified) continue;
            _rows.Add(new { test = "qualified-native-roof-recipe", graphic = id, gx, gy,
                fit.Lift, fit.IntersectionOverUnion, fit.Coverage });
            return (id, gx, gy);
        }
        throw new InvalidOperationException("No native planar roof recipe qualified among bounded candidates; roof proof remains unavailable.");
    }

    private void Compare(string name, Shot bypass, Shot active, int receiverZ,
        HashSet<ulong> expectedCasters, bool requirePositive, bool requireNoHitNegative, bool roofReceiver = false)
    {
        Check(bypass.Offers == active.Offers, "identical native light offers " + name);
        Require(bypass.Surface.GetData().AsSpan().SequenceEqual(active.Surface.GetData()), "same actual receiver pixels " + name);
        Check(bypass.Snapshot.SourceFingerprint == active.Snapshot.SourceFingerprint, "same physical source for bypass pair " + name);
        Check(ReferenceEquals(bypass.Snapshot, active.Snapshot), "unchanged native source reuses immutable physical ticket " + name);
        int sampled = 0, blocked = 0, blockedDark = 0, misses = 0, missDark = 0, unknown = 0;
        int presentedBlockedDark = 0, presentedMissDark = 0;
        var snapshot = active.Snapshot;
        for (int y = 0; y < active.Surface.GetHeight(); y += 3) for (int x = 0; x < active.Surface.GetWidth(); x += 3) {
            var surface = active.Surface.GetPixel(x, y);
            int packed = (int)MathF.Round(surface.A), kind = packed & 15;
            if ((packed & 256) == 0 || (roofReceiver ? kind != 5 : kind != 1 || Math.Abs(surface.B - receiverZ) > .2f)) continue;
            N3 p = new(surface.R + active.Origin.X, surface.G + active.Origin.Y, surface.B);
            if (p.X < X - 3 || p.X > X + 4 || p.Y < Y - 2 || p.Y > Y + 2) continue;
            float reach = MathF.Max(1, snapshot.Max.Z + 1 - p.Z) / active.Projection.SourceToSun.Z;
            var result = PhysicalWorldOracle.Trace(snapshot, p, p + active.Projection.SourceToSun * reach);
            int at = (y * active.Surface.GetWidth() + x) * 8;
            bool dark = false;
            for (int c = 0; c < 3; c++) {
                float a = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bypass.Light, at + c * 2));
                float b = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(active.Light, at + c * 2));
                if (a - b > .0005f) dark = true;
            }
            int displayAt = at / 2, displayDelta = 0;
            for (int c = 0; c < 3; c++) displayDelta += bypass.Presented[displayAt + c] - active.Presented[displayAt + c];
            bool displayDark = displayDelta > 2;
            sampled++; if (!result.Known) unknown++;
            if (result.Blocked && expectedCasters.Contains(result.Owner.Identity) && result.Owner.Kind == PhysicalSourceKind.Item) {
                blocked++; if (dark) blockedDark++; if (displayDark) presentedBlockedDark++;
            } else if (!result.Blocked) { misses++; if (dark) missDark++; if (displayDark) presentedMissDark++; }
        }
        // A known geometric HIT is useful even when the complete path to empty sky
        // is unknown. A MISS here is a bounded-capture negative, never a claim of
        // globally exposed sky; that distinction is retained in the receipt.
        if (requirePositive) Check(blocked >= 3 && blockedDark >= 3, "positive native structural shadow " + name);
        if (requirePositive) Check(presentedBlockedDark >= 3, "actual final presentation shows structural shadow " + name);
        if (requireNoHitNegative) Check(misses >= 3 && missDark == 0, "bounded higher-receiver negative " + name);
        if (requireNoHitNegative) Check(presentedMissDark == 0, "actual final presentation preserves higher-receiver negative " + name);
        _rows.Add(new { test = "native-HDR-structural-attribution", name, receiverZ, sampled, blocked,
            blockedDark, misses, missDark, presentedBlockedDark, presentedMissDark,
            unknownCorridors = unknown, requirePositive, requireNoHitNegative }); Save();
    }

    private void SurfaceDistribution(string name, Shot shot)
    {
        var groups = new Dictionary<(int Packed, int Z), (int Count, float MinX, float MaxX, float MinY, float MaxY)>();
        for (int y = 0; y < shot.Surface.GetHeight(); y += 3) for (int x = 0; x < shot.Surface.GetWidth(); x += 3) {
            var p = shot.Surface.GetPixel(x, y); float wx = p.R + shot.Origin.X, wy = p.G + shot.Origin.Y;
            if (wx < X - 6 || wx > X + 6 || wy < Y - 6 || wy > Y + 6) continue;
            var key = ((int)MathF.Round(p.A), (int)MathF.Round(p.B));
            if (!groups.TryGetValue(key, out var g)) g = (0, wx, wx, wy, wy);
            groups[key] = (g.Count + 1, MathF.Min(g.MinX, wx), MathF.Max(g.MaxX, wx), MathF.Min(g.MinY, wy), MathF.Max(g.MaxY, wy));
        }
        _rows.Add(new { test = "native-surface-distribution", name, originX = shot.Origin.X, originY = shot.Origin.Y,
            groups = groups.OrderByDescending(p => p.Value.Count).Take(64).Select(p => new { packed = p.Key.Packed,
                kind = p.Key.Packed & 15, physical = (p.Key.Packed & 256) != 0, z = p.Key.Z, p.Value.Count,
                p.Value.MinX, p.Value.MaxX, p.Value.MinY, p.Value.MaxY }).ToArray() }); Save();
    }

    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[native-physical-world] Debug fixture only"); GetTree().Quit(1); return;
#else
        try {
            string[] args = OS.GetCmdlineUserArgs();
            Require(!args.Any(a => a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session" or "--window-size"), "isolated fixture has no account/shard/window overrides");
            int cacheAt = Array.IndexOf(args, "--cache-dir");
            Require(cacheAt >= 0 && cacheAt + 1 < args.Length && Path.GetFullPath(args[cacheAt + 1]).Equals(
                Path.Combine(_root, "data", "fixture", "cache"), StringComparison.OrdinalIgnoreCase), "explicit isolated --cache-dir required");
            for (int i = 0; Client.Game?.Scene is not LoginScene; i++) { if (i > 1200) throw new Exception("login timeout"); await Frame(); }
            _game = Client.Game; var world = _game.UO.World;
            Require(!NetClient.Socket.IsConnected, "disconnected bootstrap");
            var setup = typeof(NetClient).GetMethod("SetupSocket", Private);
            setup.Invoke(NetClient.Socket, new[] { Enum.Parse(setup.GetParameters()[0].ParameterType, "TcpSocket") });
            Settings.GlobalSettings.ProfilesPath = Path.Combine(_root, "data", "fixture", "profiles");
            typeof(ProfileManager).GetField("_rootPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            ProfileManager.Load("Offline", "native-physical-world", "native-physical-world");
            _profile = ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root, StringComparison.OrdinalIgnoreCase), "isolated profile");
            _profile.DefaultScale = 1; _profile.GameWindowFullSize = true; _profile.TopbarGumpIsDisabled = true;
            _profile.UseCircleOfTransparency = false; _profile.NoColorObjectsOutOfRange = false; _profile.EnableDeathScreen = false;
            _profile.LightingSpeedMode = true; _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 0;
            _profile.SpeedModeSoften = 0; _profile.SpeedModeWindowLight = false; _profile.SpeedModeWeather = false;
            _profile.SpeedModeFog = false; _profile.SpeedModeMorningHaze = 0; _profile.SpeedModeReflections = false;
            _profile.WaterStyle = "classic"; _profile.WindStrength = 0; _profile.DrawRoofs = false;
            _profile.ShadowsEnabled = false; _profile.ShadowsStatics = false;
            world.CreatePlayer(1); world.MapIndex = 0; world.Player.Graphic = 0x190;
            world.Player.SetInWorldTile(X, Y, 20); world.ShardClock.Set(12, 0, 0, Time.Ticks);
            _scene = new GameScene(world); _game.SetScene(_scene); _game.SetWindowSize(1280, 720); _scene.Camera.Zoom = 1;
            for (int i = 0; i < 12; i++) await Frame();
            world.Map.GetMapZ(X, Y, out sbyte ground, out _); _z = ground + 1;
            world.Player.SetInWorldTile(X, Y, (sbyte)_z);
            Item floorWitness = null;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                var floor = Place(0x0495, X + x, Y + y, _z); if (x == 0 && y == 0) floorWitness = floor;
            }
            var floorData = _game.UO.FileManager.TileData.StaticData[0x0495];
            var floorRecipe = PhysicalArtRecipe.For(0x0495);
            _rows.Add(new { test = "native-floor-source", graphic = 0x0495, tileHeight = floorData.Height,
                flags = floorData.Flags.ToString(), floorData.IsSurface, floorData.IsBridge, floorData.IsAnimated,
                recipeFlat = PhysicalReceiverRecipe.Flat(floorWitness, floorData.Height),
                fullFloorZero = floorRecipe.FullFloor(0), fullFloorHeight = floorRecipe.FullFloor(floorData.Height),
                sourceData = SurfaceRow.For(floorWitness).Data, artWidth = floorRecipe.Width, artHeight = floorRecipe.Height });
            _targets = Field<RenderTargets>(_game, "_renderTargets"); _batcher = Field<UltimaBatcher2D>(_game, "_uoSpriteBatch");
            _draw = _game.GetType().GetMethod("DrawFrame", Private);
            _scene.UpdateDrawPosition = true;
            for (int i = 0; i < 12; i++) await Frame();
            _game.SetProcess(false);
            if (System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_PERF") == "1") {
                await RunPerformanceCases();
                GD.Print("[native-physical-performance] PASS shared-load Debug mode comparison; no Release/FPS claim");
                GetTree().Quit(0); return;
            }
            for (int i = 0; i < 3; i++) { using var warm = await Capture("bootstrap-" + i); }
            _rows.Add(new { test = "pure-contract", checks = PhysicalWorldProbe.Run() });
            await Cases();
            Require(!NetClient.Socket.IsConnected, "fixture never connected"); Save();
            if (_failures.Count != 0) throw new InvalidOperationException(string.Join("; ", _failures));
            GD.Print("[native-physical-world] PASS native sun/door/shelter/overlap/first-frame/final-presentation/off-fallback");
            GetTree().Quit(0);
        } catch (Exception error) {
            _failures.Add(error.ToString()); Save(); GD.PrintErr("[native-physical-world] FAIL " + error); GetTree().Quit(1);
        } finally {
            StructuralSunDepthRd.ProbeBypass = false; OrderedWorldCapture.InjectSpeedLightFailure = false;
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
        }
#endif
    }

    private async Task Cases()
    {
#if DEBUG
        ushort wallGraphic = FindWall();
        var wallItems = new List<Item>();
        for (int y = -1; y <= 1; y++) wallItems.Add(Place(wallGraphic, X - 2, Y + y, _z));
        // Above the short wall: the same light must not project it upwards.
        int upperZ = _z + 50;
        for (int y = -1; y <= 1; y++) for (int x = 1; x <= 3; x++) Place(0x0495, X + x, Y + y, upperZ);
        Mutated();
        using var wallFirst = await Capture("wall-first-published-frame");
        SurfaceDistribution("wall-first-published-frame", wallFirst);
        Check(wallItems.All(item => Contains(wallFirst, item)), "native wall owner geometry present on first changed frame");
        StructuralSunDepthRd.ProbeBypass = true;
        using var wallBypass = await Capture("wall-structural-bypass");
        var wallOwners = wallItems.Select(i => (ulong)i.Serial).ToHashSet();
        Compare("wall-to-ground", wallBypass, wallFirst, _z, wallOwners, true, false);
        StructuralSunDepthRd.ProbeBypass = false;

        // Native culling follows the player's actual floor. Enter the upper deck
        // before testing it; do not override _maxZ or force a hidden sprite draw.
        _game.UO.World.Player.SetInWorldTile(X + 2, Y, (sbyte)upperZ); Mutated();
        using var upperShort = await Capture("upper-floor-short-wall");
        SurfaceDistribution("upper-floor-short-wall", upperShort);
        StructuralSunDepthRd.ProbeBypass = true;
        using var upperShortBypass = await Capture("upper-floor-short-wall-bypass");
        Compare("wall-below-upper-floor", upperShortBypass, upperShort, upperZ, wallOwners, false, true);
        StructuralSunDepthRd.ProbeBypass = false;

        var tallOwners = new HashSet<ulong>();
        int wallHeight = _game.UO.FileManager.TileData.StaticData[wallGraphic].Height;
        for (int y = -1; y <= 1; y++) for (int height = 0; height < 85; height += wallHeight)
            tallOwners.Add(Place(wallGraphic, X - 3, Y + y, _z + height).Serial);
        Mutated();
        using var tallFirst = await Capture("tall-structure-first-published-frame");
        StructuralSunDepthRd.ProbeBypass = true;
        using var tallBypass = await Capture("tall-structure-bypass");
        Compare("tall-structure-to-upper-floor", tallBypass, tallFirst, upperZ, tallOwners, true, false);
        StructuralSunDepthRd.ProbeBypass = false;

        var roofRecipe = FindRoof();
        var roofs = new List<Item>();
        for (int y = -1; y <= 1; y++) for (int x = -3; x <= -1; x++) roofs.Add(Place(roofRecipe.Graphic,
            X + x, Y + y, _z + 25 + (x + 3) * roofRecipe.Gx + (y + 1) * roofRecipe.Gy));
        // Freeze the normal fade at its endpoints; geometry still comes from the
        // live items. This isolates cutaway invariance from animation timing.
        foreach (var roof in roofs) roof.AlphaHue = 0;
        Mutated();
        using var roofHidden = await Capture("roof-hidden-first-published-frame");
        Check(Contains(roofHidden, roofs[0]), "native neighbour-qualified roof exists while cut away");
        Check(roofHidden.Snapshot.SourceFingerprint != wallFirst.Snapshot.SourceFingerprint
            && roofHidden.Snapshot.Key.Generation > wallFirst.Snapshot.Key.Generation, "roof addition advances first physical generation");
        _profile.DrawRoofs = true; foreach (var roof in roofs) roof.AlphaHue = 255; Mutated();
        using var roofVisible = await Capture("roof-visible");
        SurfaceDistribution("roof-visible", roofVisible);
        Check(ReferenceEquals(roofHidden.Snapshot, roofVisible.Snapshot)
            && roofHidden.Snapshot.SourceFingerprint == roofVisible.Snapshot.SourceFingerprint
            && roofHidden.DepthHash == roofVisible.DepthHash, "roof draw option cannot alter physical sun depth");
        int visibleRoofPixels = 0, hiddenRoofPixels = 0;
        int CountRoof(Shot shot) {
            int count = 0;
            for (int y = 0; y < shot.Surface.GetHeight(); y++) for (int x = 0; x < shot.Surface.GetWidth(); x++) {
                var p = shot.Surface.GetPixel(x, y); int packed = (int)MathF.Round(p.A);
                if ((packed & 15) != 5 || (packed & 256) == 0) continue;
                float wx = p.R + shot.Origin.X, wy = p.G + shot.Origin.Y;
                if (wx >= X - 3 && wx < X - 1 && wy >= Y - 1 && wy < Y + 1) count++;
            }
            return count;
        }
        visibleRoofPixels = CountRoof(roofVisible); hiddenRoofPixels = CountRoof(roofHidden);
        Check(visibleRoofPixels > 3 && hiddenRoofPixels == 0, "roof visibility really changes while physical depth stays identical");
        _rows.Add(new { test = "native-roof-receiver", visibleRoofPixels, hiddenRoofPixels });
        StructuralSunDepthRd.ProbeBypass = true;
        using var roofBypass = await Capture("roof-visible-structural-bypass");
        Compare("tall-structure-to-native-roof", roofBypass, roofVisible, 0, tallOwners, true, false, true);
        StructuralSunDepthRd.ProbeBypass = false;
        _profile.DrawRoofs = false;
        foreach (var roof in roofs) roof.Destroy(); Mutated();
        using var roofRemoved = await Capture("roof-removed-first-published-frame");
        Check(roofs.All(roof => !Contains(roofRemoved, roof))
            && roofRemoved.Snapshot.Key.Generation > roofVisible.Snapshot.Key.Generation
            && roofRemoved.Snapshot.SourceFingerprint != roofVisible.Snapshot.SourceFingerprint,
            "removed roof retires on first changed generation");

        // Source invalidation for a real native door; unknown openings are recorded
        // explicitly. This is not called a completed point-light shadow proof.
        ushort door = 0;
        for (ushort id = 1; id < 0x4000; id++) if (_game.UO.FileManager.TileData.StaticData[id].IsDoor
            && _game.UO.Arts.GetArt(id).Texture != null) { door = id; break; }
        Require(door != 0, "native door witness");
        var dynamicDoor = Place(door, X - 1, Y + 2, _z); Mutated();
        using var doorClosed = await Capture("door-in-place-first-published-frame");
        dynamicDoor.SetInWorldTile(X, Y + 2, (sbyte)_z); Mutated();
        using var doorMoved = await Capture("door-moved-first-published-frame");
        Check(doorMoved.Snapshot.Key.Generation > doorClosed.Snapshot.Key.Generation
            && doorMoved.Snapshot.SourceFingerprint != doorClosed.Snapshot.SourceFingerprint, "live door move invalidates first physical generation");
        _rows.Add(new { test = "door-source-invalidation", graphic = door, serial = dynamicDoor.Serial,
            oldCell = doorMoved.Snapshot.UnknownAt(X - 1, Y + 2).ToString(), newCell = doorMoved.Snapshot.UnknownAt(X, Y + 2).ToString(),
            localLightVisibility = "not-tested" });

        await RunLampCases();
        await RunWallReceiverCases();
        await RunShelterCases();
        if (System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_EMITTED_WEATHER") == "1")
            await RunEmittedWeatherCases();
        await RunResidentReceiverCases();

        _profile.LightingSpeedMode = false;
        using var offBefore = await Capture("off-before", false);
        _profile.LightingSpeedMode = true;
        using var on = await Capture("on-again");
        _profile.LightingSpeedMode = false;
        using var offAfter = await Capture("off-after", false);
        Check(offBefore.World.AsSpan().SequenceEqual(offAfter.World), "exact parent-disabled native world parity");
        Check(offBefore.Presented.AsSpan().SequenceEqual(offAfter.Presented), "exact parent-disabled final presentation parity");
        _profile.LightingSpeedMode = true; OrderedWorldCapture.InjectSpeedLightFailure = true;
        using var failed = await Capture("injected-light-failure", false);
        Check(offBefore.World.AsSpan().SequenceEqual(failed.World), "whole-light failure restores exact native baseline");
        Check(offBefore.Presented.AsSpan().SequenceEqual(failed.Presented), "whole-light failure restores exact final presentation");
        OrderedWorldCapture.InjectSpeedLightFailure = false;
        _rows.Add(new { test = "fixture", x = X, y = Y, z = _z, floor = 0x0495, roof = roofRecipe.Graphic,
            roofRecipe.Gx, roofRecipe.Gy, wall = wallGraphic, upperZ, resolution = "1280x720", soften = 0 }); Save();
#endif
    }
}

internal static class PhysicalProbeTriangleExtensions
{
    internal static bool ContainsOwner(this ReadOnlySpan<PhysicalTriangle> triangles, PhysicalOwnerKey owner)
    {
        foreach (var triangle in triangles) if (triangle.Owner == owner) return true;
        return false;
    }
}
