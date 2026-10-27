// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Security.Cryptography;

namespace GUO.Host;

internal static class PerfRuntimeIdentity
{
    // Once after a sample closes, before restoring its pacing. Reflection is
    // diagnostic only; do not add frame-loop allocation or change target ownership.
    internal static object SceneSnapshot()
    {
        var game = Client.Game;
        var targets = game == null ? null : typeof(GameController)
            .GetField("_renderTargets", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)?.GetValue(game) as GUO.Renderer.RenderTargets;
        object Size(GUO.Renderer.RenderTarget2D target) => target == null || target.IsDisposed
            ? null : new { width = target.Width, height = target.Height };
        return new
        {
            version = 1,
            depth_world_selected = GUO.Renderer.DepthWorldProbe.Enabled,
            resident_admitted_last_frame = GUO.Renderer.DepthWorldProbe.ResidentEnabled,
            chunk_reuse = GUO.Game.Scenes.GameScene.ChunkReuse,
            world = Size(targets?.WorldRenderTarget),
            light = Size(targets?.LightRenderTarget),
            ui = Size(targets?.UiRenderTarget),
            engine_max_fps = Godot.Engine.MaxFps,
            vsync = Godot.DisplayServer.WindowGetVsyncMode().ToString(),
            active = game?.IsActive ?? false
        };
    }

    // Report generation only, outside all sample windows. No credentials/config contents.
    internal static object Capture()
    {
        if (Environment.GetEnvironmentVariable("GUO_PERF_MANIFEST") != "1") return null;
        string Hash(string path)
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }
        var client = typeof(PerfProbe).Assembly;
        var api = typeof(Godot.GodotObject).Assembly;
        return new
        {
            executable_sha256 = Hash(Environment.ProcessPath),
            client_sha256 = Hash(client.Location), client_mvid = client.ManifestModule.ModuleVersionId,
            godot_api_sha256 = Hash(api.Location), godot_api_mvid = api.ManifestModule.ModuleVersionId,
            renderer = Godot.RenderingServer.GetCurrentRenderingMethod(),
            effective_flags = new
            {
                depth_world = GUO.Renderer.DepthWorldProbe.Enabled,
                depth_world_chunk_reuse = GUO.Game.Scenes.GameScene.ChunkReuse,
                batched_world = GUO.Renderer.UltimaBatcher2D.BatchedWorld,
                merged_cover = GUO.Renderer.UltimaBatcher2D.MergedCover,
                merged_land = GUO.Renderer.MergedLand.Enabled,
                merged_land_ordered = GUO.Renderer.MergedLand.Ordered,
                merged_land_array = GUO.Renderer.MergedLand.Array,
                gpu_world = GUO.Renderer.GpuWorld.Enabled,
                retained_canvas = GUO.Renderer.UltimaBatcher2D.RetainCanvas,
                reclaim_empty_retained_canvas = GUO.Renderer.UltimaBatcher2D.ReclaimEmptyRetainedCanvas,
                retained_cover_candidates = GUO.Game.Scenes.RenderLists.RetainCoverCandidates,
                shared_cover = GUO.Renderer.UltimaBatcher2D.SharedCover,
                shared_cover_shader = GUO.Renderer.UltimaBatcher2D.SharedCoverShader,
                shared_cover_reference = GUO.Renderer.UltimaBatcher2D.SharedCoverReference,
                multimesh_sprites = GUO.Renderer.UltimaBatcher2D.MultiMeshSprites,
                clear_used_canvas = GUO.Renderer.UltimaBatcher2D.ClearUsedCanvas
            },
            parameters = new { multimesh_min_run = GUO.Renderer.UltimaBatcher2D.MultiMeshMinRun },
            engine_version = Godot.Engine.GetVersionInfo()["string"].AsString(),
            dotnet_runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        };
    }
}
