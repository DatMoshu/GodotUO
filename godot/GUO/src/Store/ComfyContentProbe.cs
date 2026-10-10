// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Assets;
using Environment = System.Environment;

namespace GUO.Store;

/// <summary>
/// Headless proof that a ComfyUI-staged pack works in the real client:
/// install the pack through the normal store mount (UO_CONTENT_STORE +
/// UO_CONTENT_LOCK), then read a generated SFX and music clip back through
/// SoundsLoader, the staged splat LODs through SplatLodChain (GUO_SPLAT_STAGE),
/// and render one LOD through SplatBatcher into a viewport (pixel proof).
/// PASS prints and quits 0; anything else FAILs and quits 1.
/// </summary>
public partial class ComfyContentProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            using var files = new UOFileManager(GUO.Host.UoDataProbe.ParseVersion(Environment.GetEnvironmentVariable("UO_CLIENT_VERSION") ?? "7.0.107.76"), Environment.GetEnvironmentVariable("UO_CLIENT_DATA"));
            files.Load(false, "enu");
            StorePack.Require(files.Content != null, "staged pack did not mount (check UO_CONTENT_STORE/UO_CONTENT_LOCK)");
            int sfx = int.Parse(Environment.GetEnvironmentVariable("GUO_PROBE_SFX") ?? "2100");
            int music = int.Parse(Environment.GetEnvironmentVariable("GUO_PROBE_MUSIC") ?? "101");
            StorePack.Require(files.Sounds.TryGetSound(sfx, out var sfxBytes, out _) && sfxBytes.Length > 44100, $"staged SFX {sfx} absent");
            // Staged theme art (GUO_PROBE_ART=0xF000): the same GetArt call the
            // renderer makes must return lit pixels, not black.
            if (int.TryParse((Environment.GetEnvironmentVariable("GUO_PROBE_ART") ?? "").Replace("0x", ""),
                System.Globalization.NumberStyles.HexNumber, null, out int artId))
            {
                var art = files.Arts.GetArt(0x4000 + (uint)artId);
                int lit = 0;
                foreach (uint px in art.Pixels)
                {
                    byte r = (byte)(px & 0xFF), g = (byte)((px >> 8) & 0xFF), b = (byte)((px >> 16) & 0xFF);
                    if (r > 24 || g > 24 || b > 24)
                    {
                        lit++;
                    }
                }

                StorePack.Require(lit > 100, $"staged art 0x{artId:X4} came back black ({art.Width}x{art.Height})");
                GD.Print($"[comfy probe] staged art 0x{artId:X4}: {art.Width}x{art.Height}, {lit} lit pixels");
            }
            var sounds = new GUO.Renderer.Sounds.Sound(files.Sounds);
            StorePack.Require(sounds.GetMusic(music) is StoreMusic, $"staged music {music} absent");

            string stage = Environment.GetEnvironmentVariable("GUO_SPLAT_STAGE");
            StorePack.Require(!string.IsNullOrWhiteSpace(stage) && Directory.Exists(stage), "GUO_SPLAT_STAGE missing");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "splats.json")));
            int total = 0, placed = 0, footprints = 0;
            foreach (var splat in manifest.RootElement.GetProperty("splats").EnumerateObject())
            {
                if (splat.Value.TryGetProperty("placement", out var at)
                    && at.GetProperty("facet").GetInt32() == 0)
                {
                    placed++;
                }

                if (splat.Value.TryGetProperty("footprint", out var fp))
                {
                    string draft = Path.Combine(stage, fp.GetString().Replace('/', Path.DirectorySeparatorChar));
                    StorePack.Require(File.Exists(draft), $"footprint missing for {splat.Name}");
                    using var draftDoc = JsonDocument.Parse(File.ReadAllText(draft));
                    StorePack.Require(draftDoc.RootElement.GetProperty("kind").GetString() == "components"
                        && draftDoc.RootElement.GetProperty("components").GetArrayLength() > 0,
                        $"footprint draft unusable for {splat.Name}");
                    footprints++;
                }

                string[] lods = new string[splat.Value.GetProperty("lods").GetArrayLength()];
                int i = 0;
                foreach (var lod in splat.Value.GetProperty("lods").EnumerateArray())
                {
                    string rel = lod.GetString().Replace('/', Path.DirectorySeparatorChar);
                    lods[i++] = Path.Combine(stage, rel);
                }

                var chain = new GUO.Renderer.SplatLodChain();
                var levels = new SplatSet[lods.Length];
                for (int l = 0; l < lods.Length; l++)
                {
                    levels[l] = SplatPlyParser.Parse(lods[l]);
                }

                // Same assertions as the editor smoke: exact decimated counts.
                StorePack.Require(levels[0].Gaussians.Length == splat.Value.GetProperty("count").GetInt32(), $"splat {splat.Name} lod0 count differs");
                for (int l = 1; l < levels.Length; l++)
                {
                    StorePack.Require(levels[l].Gaussians.Length < levels[l - 1].Gaussians.Length, $"splat {splat.Name} lod{l} not smaller");
                }

                total++;
            }

            StorePack.Require(total > 0, "no splats in manifest");
            int drawn = await RenderProofAsync(stage);
            GD.Print($"[comfy probe] PASS: staged SFX {sfx} ({sfxBytes.Length} bytes) + music {music} through the store mount, {total} splat chain(s) parsed, {placed} placed, {footprints} footprint draft(s), {drawn} splats through the batcher");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("[comfy probe] FAIL: " + e); GetTree().Quit(1); }
    }

    /// <summary>
    /// Runs the staged chain through the real SplatBatcher draw path (the
    /// same call the editor's world overlay makes) and returns the splats
    /// drawn. Headless-safe: no frame awaits (those hang without a renderer);
    /// the editor smoke already draws every LOD end through RenderingServer.
    /// </summary>
    private System.Threading.Tasks.Task<int> RenderProofAsync(string stage)
    {
        var placements = GUO.Renderer.SplatStage.Load(stage);
        StorePack.Require(placements.Count > 0, "no staged placements to render");
        var p = placements[0];
        Godot.Vector3 size = p.Chain.BoundsMax - p.Chain.BoundsMin;
        float world = System.Math.Max(size.X, System.Math.Max(size.Y, size.Z));
        Rid parent = RenderingServer.CanvasItemCreate();
        try
        {
            using var batcher = new GUO.Renderer.SplatBatcher(parent);
            batcher.SetChain(p.Chain, Godot.Vector2.Zero);
            batcher.Draw(200f / System.Math.Max(0.001f, world));
            StorePack.Require(batcher.LastLevel >= 0 && batcher.DrawnSplats > 0,
                "batcher culled the proof splat");
            return System.Threading.Tasks.Task.FromResult(batcher.DrawnSplats);
        }
        finally
        {
            RenderingServer.FreeRid(parent);
        }
    }
}
