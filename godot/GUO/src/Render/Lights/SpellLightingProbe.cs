// SPDX-License-Identifier: BSD-2-Clause
// Synthetic UO+ spell-event and field proof: no shard, art or credentials.
using System;
using System.Reflection;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;

namespace GUO.Renderer.Lights;

public partial class SpellLightingProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        // Keep the test independent of saved player profiles. Reflection is confined
        // to this probe: install its profile and invoke the actual arrival seam.
        PropertyInfo current = typeof(ProfileManager).GetProperty(nameof(ProfileManager.CurrentProfile));
        Profile previous = ProfileManager.CurrentProfile;
        // World constructs ContainerManager, which writes a default config if
        // absent. Treat it as a temporary integration fixture, preserving all
        // pre-existing files and directories.
        string dataDir = Path.Combine(CUOEnviroment.ExecutablePath, "Data");
        string clientDir = Path.Combine(dataDir, "Client");
        string containerFile = Path.Combine(clientDir, "containers.txt");
        bool hadDataDir = Directory.Exists(dataDir), hadClientDir = Directory.Exists(clientDir);
        bool hadContainerFile = File.Exists(containerFile);
        try
        {
            var p = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            Require(!p.SpellLighting && !SpellLighting.Enabled(p), "old profile defaults off");
            p.SpellLighting = true;
            p = JsonSerializer.Deserialize(JsonSerializer.Serialize(p, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Require(p.SpellLighting && !SpellLighting.Enabled(p), "toggle persists; needs Realtime lights");
            Require(SpellLighting.Emit(p, 1000, GraphicEffectType.Moving, 0x36D4, 8, 8, 0) == 0, "parent off emits nothing");
            p.RealtimeLights = true;
            UoPlusOptions.Set(p, UoPlusFeature.SpellLights, true, 100);
            p.SpellLighting = false;
            current.SetValue(null, p);
            RealtimeLightEffects.Clear();

            var world = new World();
            var manager = new EffectManager(world);
            Create(manager, GraphicEffectType.Moving, 0x36D4);
            Require(RealtimeLightEffects.Count == 0 && manager.Items is MovingEffect, "off creates classic effect without light");
            manager.Clear();

            p.SpellLighting = true;
            uint now = Time.Ticks;
            Create(manager, GraphicEffectType.Moving, 0x36D4);
            var moving = (MovingEffect)manager.Items;
            var sources = new RealtimeLightField.Source[RealtimeLightEffects.FrameLightCap + 20];
            Require(RealtimeLightEffects.Collect(now, sources, 0) == 1, "cast emits once");
            Require(sources[0].X == 8 && sources[0].Y == 8 && sources[0].Z == 3
                && sources[0].Color.R > sources[0].Color.B && sources[0].Curve == LightCurve.C, "fire cast tile, colour, falloff");

            // A live target moved after the packet: the arrival hook must use its
            // current tile, not the original packet target (12,8,0).
            var target = new Mobile(world) { X = 18, Y = 8, Z = 5 };
            moving.SetTarget(target);
            typeof(GameEffect).GetMethod("CreateExplosionEffect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(moving, null);
            Require(RealtimeLightEffects.Collect(now, sources, 0) == 2, "arrival emits without an explosion sprite");
            Require(sources[1].X == 18 && sources[1].Y == 8 && sources[1].Z == 5 && sources[1].Radius == 4, "impact at resolved target tile");

            var field = new RealtimeLightField();
            field.Begin(0, 0, 32, 24);
            field.Compute(0.0625f, sources.AsSpan(0, 2));
            Color ambient = RealtimeLightField.AmbientFor(0.0625f);
            Require(field.Cell(8, 8).R > ambient.R + 0.1f && field.Cell(18, 8).R > ambient.R + 0.1f, "cast and impact light expected cells");
            GD.Print(field.DumpAscii(sources.AsSpan(0, 2)));
            // The actual arrival source enters the same face/shadow field as a lamp.
            field.Begin(0, 0, 32, 24);
            for (int x = 0; x < 32; x++) field.AddEdge(x, 7, true, 0.5f, 20);
            field.Compute(0.0625f, sources.AsSpan(0, 2));
            Require(field.FineAt(18.5f, 7.85f).R > ambient.R + 0.1f,
                "spell impact lights the wall face toward it");
            Require(Math.Abs(field.FineAt(18.5f, 7.1f).R - ambient.R) < 0.03f
                && Math.Abs(field.FineAt(18.5f, 6.5f).R - ambient.R) < 0.03f,
                "wall blocks spell impact on its far face and behind it");
            Require(field.FineAt(18.5f, 7.85f).R > field.FineAt(21.5f, 7.85f).R + 0.05f,
                "spell intensity falls off along the wall face");
            GD.Print("[Spell lighting] wall-face/shadow PASS actual-arrival-source=18,8,5");
            Require(RealtimeLightEffects.Collect(now + 1500, sources, 0) == 0, "cast and impact expire");
            manager.Clear();

            Create(manager, GraphicEffectType.FixedFrom, 0x376A);
            now = Time.Ticks;
            Require(RealtimeLightEffects.Collect(now, sources, 0) == 1 && sources[0].Color.G > sources[0].Color.R, "heal green glow at recipient");
            field.Begin(0, 0, 24, 24);
            field.Compute(0.0625f, sources.AsSpan(0, 1));
            Require(field.Cell(8, 8).G > field.Cell(8, 8).R && field.Cell(8, 8).G > ambient.G + 0.1f, "heal lights recipient cell");
            float full = sources[0].Energy;
            RealtimeLightEffects.Collect(now + 850, sources, 0);
            Require(sources[0].Energy > 0 && sources[0].Energy < full * 0.5f, "heal fades before expiry");
            Require(RealtimeLightEffects.Collect(now + 900, sources, 0) == 0, "heal expires at duration");
            manager.Clear();

            Require(SpellLighting.Emit(p, now, GraphicEffectType.DragEffect, 0x36D4, 8, 8, 0) == 0
                && SpellLighting.Emit(p, now, GraphicEffectType.Moving, 0x0F42, 8, 8, 0) == 0, "drag and mundane graphics unlit");
            for (int i = 0; i < 200; i++) SpellLighting.Emit(p, now, GraphicEffectType.Moving, 0x36D4, i, 8, 0);
            Require(RealtimeLightEffects.Count == 16, "storm capped at 16 live spell bursts");
            Require(RealtimeLightEffects.Collect(now, sources, 99) == 1 && sources[99].X == 184, "last slot filled by oldest remaining burst");
            Require(RealtimeLightEffects.Collect(now, sources, 100) == 0, "full world budget admits no bursts even with larger buffer");
            RealtimeLightEffects.Add(now, 1, 1, 0, 2, Colors.White, 1000);
            Require(RealtimeLightEffects.Collect(now, sources, 0, includeSpells: false) == 1
                && RealtimeLightEffects.Count == 1 && sources[0].X == 1, "disable clears only spell bursts");
            RealtimeLightEffects.Clear();
            SpellLighting.Emit(p, now, GraphicEffectType.Moving, 0x36D4, 8, 8, 0);
            manager.Clear();
            Require(RealtimeLightEffects.Count == 0, "logout clears bursts");
            SpellLighting.Emit(p, now, GraphicEffectType.Moving, 0x36D4, 8, 8, 0);
            // Same seam called by MapIndex before constructing a new map.
            typeof(World).GetMethod("InternalMapChangeClear", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(world, new object[] { true });
            Require(RealtimeLightEffects.Count == 0, "map change clears bursts");
            for (int i = 0; i < RealtimeLightEffects.MaxLights; i++)
                RealtimeLightEffects.Add(now, i, 0, 0, 2, Colors.White, 1000);
            Require(SpellLighting.Emit(p, now, GraphicEffectType.Moving, 0x36D4, 8, 8, 0) == 0
                && RealtimeLightEffects.Count == RealtimeLightEffects.MaxLights, "spell cannot evict a full table of generic hooks");
            RealtimeLightEffects.Clear();
            uint wrap = uint.MaxValue - 200;
            SpellLighting.Emit(p, wrap, GraphicEffectType.Moving, 0x36D4, 8, 8, 0);
            Require(RealtimeLightEffects.Collect(unchecked(wrap + 300), sources, 0) == 1
                && RealtimeLightEffects.Collect(unchecked(wrap + 500), sources, 0) == 0, "expiry survives client tick wrap");
            GD.Print("[Spell lighting] PASS tiles=cast:8,8,3 impact:18,8,5 heal:8,8,3 spell-cap=16 total-cap=100");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Spell lighting] FAIL {e}");
            return false;
        }
        finally
        {
            RealtimeLightEffects.Clear();
            current.SetValue(null, previous);
            if (!hadContainerFile && File.Exists(containerFile)) File.Delete(containerFile);
            if (!hadClientDir && Directory.Exists(clientDir) && Directory.GetFileSystemEntries(clientDir).Length == 0)
                Directory.Delete(clientDir);
            if (!hadDataDir && Directory.Exists(dataDir) && Directory.GetFileSystemEntries(dataDir).Length == 0)
                Directory.Delete(dataDir);
        }
    }

    private static void Create(EffectManager manager, GraphicEffectType type, ushort graphic) =>
        manager.CreateEffect(type, 0, 0, graphic, 0, 8, 8, 3, 12, 8, 0, 5, 20,
            false, false, false, GraphicEffectBlendMode.Normal);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
