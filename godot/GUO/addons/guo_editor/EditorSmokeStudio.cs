#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The smoke stage for StaticStudio (pick a static, generate with ComfyUI,
/// save a theme variant, place a splat, apply a zone). Runs against the same
/// loopback stub the Art stage uses: no outside program, no real service.
/// What it proves: binding, the image job through the Art dock's provider,
/// variant save (atlas PNG + theme entry + sidecar), staged placement
/// JSON, and theme activate/clear.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _studioReport = new();
    private int _studioPhase;
    private ArtStubServer _studioStub;
    private Task<ImageResult> _studioDirect, _studioDock;
    private string _studioRoot, _studioSavedSplatStage;
    private Stopwatch _studioClock = new();
    private string _studioWorkflow;
    private Task _studioLifetime;

    /// <summary>The StaticStudio dock the plugin made.</summary>
    internal StaticStudioDock Studio { get; set; }

    private void StudioCheck(string name, bool ok, string detail = "")
    {
        _studioReport[name] = ok;
        if (!ok)
        {
            _studioReport["ok"] = false;
            _failures.Add($"StaticStudio: {name} {detail}".Trim());
            GD.Print($"[GUO editor] smoke StaticStudio FAIL: {name} {detail}");
        }
    }

    private bool StepStudio()
    {
        if (Studio == null)
        {
            return true;
        }

        if (_studioPhase == 0 && !AiFeatures.Enabled)
        {
            _report["studio"] = new Dictionary<string, object> { ["ok"] = true, ["skipped"] = "AI features disabled; manual authoring remains available" };
            return true;
        }

        if (_studioPhase == 0)
        {
            _report["studio"] = _studioReport;
            _studioReport["ok"] = true;
            _studioClock.Restart();
            try
            {
                StudioSync();
                StudioStartServices();
            }
            catch (Exception ex)
            {
                StudioCheck("threw", false, $"{ex.GetType().Name}: {ex.Message}");
                StudioCleanup();
                return true;
            }

            _studioPhase = 1;
            return false;
        }

        if (_studioClock.Elapsed.TotalSeconds > 60)
        {
            StudioCheck("finished_in_time", false, "the stage did not finish within 60 s");
            StudioCleanup();
            return true;
        }

        try
        {
            if (_studioPhase == 1 && _studioDirect.IsCompleted)
            {
                StudioCheckDirect();
                _studioPhase = 2;
            }
            else if (_studioPhase == 2 && _studioDock.IsCompleted)
            {
                StudioCheckGallery();
                StudioCheckDock();
                _studioLifetime = StudioCheckLifetimeAsync();
                _studioPhase = 3;
            }
            else if (_studioPhase == 3 && _studioLifetime.IsCompleted)
            {
                _studioLifetime.GetAwaiter().GetResult();
                StudioCleanup();
                return true;
            }
        }
        catch (Exception ex)
        {
            StudioCheck("threw", false, $"{ex.GetType().Name}: {ex.Message}");
            StudioCleanup();
            return true;
        }

        return false;
    }

    private void StudioSync()
    {
        _studioRoot = Path.Combine(_out, $"studio_project{Suffix}");
        if (Directory.Exists(_studioRoot))
        {
            Directory.Delete(_studioRoot, recursive: true);
        }

        WorldProject.OpenOrCreate(_studioRoot, _data.ClientData, _data.ClientVersion).Dispose();
        _data.OpenAssets(_studioRoot);
        _studioSavedSplatStage = System.Environment.GetEnvironmentVariable("GUO_SPLAT_STAGE");
    }

    private void StudioStartServices()
    {
        _studioStub = new ArtStubServer(FixtureStaticImage().SavePngToBuffer());
        string wfDir = Path.Combine(ArtExchange.Root, "workflows");
        Directory.CreateDirectory(wfDir);
        _studioWorkflow = Path.Combine(wfDir, "stub_studio_img2img.json");
        File.WriteAllText(_studioWorkflow, ArtStubServer.WorkflowJson);

        Studio.BindTarget(AssetKind.Static, FixtureStatic, FixtureStaticImage());
        ImageRequest req = Studio.BuildRequest("mossy ring", _studioWorkflow);
        StudioCheck("request_bound", req != null && req.InputPng.Length > 50 && req.InputName == "client:static:0x0E75",
            req?.InputName ?? "null");
        _studioDirect = Task.Run(() => new ComfyUiProvider(_studioStub.Url).RunAsync(req, null, CancellationToken.None));
    }

    private void StudioCheckDirect()
    {
        ImageResult r = _studioDirect.Result;
        StudioCheck("comfy_returned_image", r.Error == null && r.Pngs.Count == 1, r.Error ?? "");
        _studioReport["seed"] = r.Seed;

        // The dock path: run (fills the gallery) and save the variant to a theme.
        ImageRequest req = Studio.BuildRequest("mossy ring", _studioWorkflow);
        // Start on the editor thread and poll on later frames so the awaited
        // gallery update retains Godot's synchronization context.
        _studioDock = Studio.RunImageAsync(new ComfyUiProvider(_studioStub.Url), req);
    }

    private void StudioCheckGallery()
    {
        ImageResult docked = _studioDock.GetAwaiter().GetResult();
        StudioCheck("dock_gallery", docked.Error == null && Studio.GalleryCount == 1, docked.Error ?? "");
        string savedVariantDir = System.Environment.GetEnvironmentVariable("GUO_VARIANT_DIR");
        string variantDir = Path.Combine(_out, $"studio_variants{Suffix}");
        System.Environment.SetEnvironmentVariable("GUO_VARIANT_DIR", variantDir);
        string said = Studio.SaveVariant(0, "smoke-studio");
        StudioCheck("variant_saved", said.StartsWith("saved"), said);
        string themePath = Path.Combine(variantDir, "smoke-studio.theme.json");
        StudioCheck("theme_file", File.Exists(themePath), themePath);
        JsonNode theme = File.Exists(themePath) ? JsonNode.Parse(File.ReadAllText(themePath)) : null;
        StudioCheck("theme_entry", (int?)theme?["entries"]?[0]?["match"]?[0] == FixtureStatic
            && (string)theme?["entries"]?[0]?["image"] == "static_0x0E75.png", theme?.ToJsonString() ?? "none");
        string variantPng = Path.Combine(variantDir, "smoke-studio", "static_0x0E75.png");
        StudioCheck("atlas_variant_png", File.Exists(variantPng), variantPng);
        if (File.Exists(variantPng))
            File.Copy(variantPng, Path.Combine(_out, $"guo_R4b_staticstudio-variant_still_{DateTime.UtcNow:yyyyMMdd-HHmm}{Suffix}.png"), true);
        JsonNode sidecar = File.Exists(variantPng + ".json") ? JsonNode.Parse(File.ReadAllText(variantPng + ".json")) : null;
        StudioCheck("variant_provenance", (string)sidecar?["tool"] == "comfyui"
            && (string)sidecar?["input"] == "client:static:0x0E75", sidecar?.ToJsonString() ?? "none");
        System.Environment.SetEnvironmentVariable("GUO_VARIANT_DIR", savedVariantDir);
    }

    private void StudioCheckDock()
    {
        // A staged placement for a stub splat entry, then a zone apply immune
        // to world contents (empty rect repaints nothing, mechanism intact).
        string stage = Path.Combine(_out, $"studio_stage{Suffix}");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "splats.json"),
            "{\"format\":\"guo/comfy-splats@1\",\"splats\":{\"smoke-splat\":{\"lods\":[]}}}");
        System.Environment.SetEnvironmentVariable("GUO_SPLAT_STAGE", stage);
        string said = Studio.PlaceSplatAt("smoke-splat", 100, 100, 0);
        StudioCheck("splat_placed", said.StartsWith("placed"), said);
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(stage, "splats.json")));
        StudioCheck("placement_json", (int?)manifest?["splats"]?["smoke-splat"]?["placement"]?["x"] == 100
            && (int?)manifest?["splats"]?["smoke-splat"]?["placement"]?["y"] == 100,
            manifest?.ToJsonString() ?? "none");

        // Apply the theme saved by StudioCheckDirect, not an unrelated theme
        // from the developer's environment or the world project's themes folder.
        string savedVariantDir = System.Environment.GetEnvironmentVariable("GUO_VARIANT_DIR");
        try
        {
            System.Environment.SetEnvironmentVariable("GUO_VARIANT_DIR", Path.Combine(_out, $"studio_variants{Suffix}"));
            string applied = Studio.ApplyZone("smoke-studio", "0,0,0,0,0");
            StudioCheck("zone_applied", applied.StartsWith("theme smoke-studio"), applied);
            StudioCheck("theme_active", GUO.Game.Managers.ThemeManager.Active.Count == 1,
                GUO.Game.Managers.ThemeManager.Active.Count.ToString());
            string cleared = Studio.ClearThemes();
            StudioCheck("themes_cleared", cleared.StartsWith("themes cleared")
                && GUO.Game.Managers.ThemeManager.Active.Count == 0, cleared);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("GUO_VARIANT_DIR", savedVariantDir);
        }
    }

    private async Task StudioCheckLifetimeAsync()
    {
        var provider = new StudioPendingProvider();
        ImageRequest req = Studio.BuildRequest("gate fixture", _studioWorkflow);
        Task<ImageResult> pending = Studio.RunImageAsync(provider, req);
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            StudioCheck("preview_enabled", Studio.SetPreview(true).StartsWith("preview"));
            AiFeatures.Apply(false);
            Studio.ApplyAiFeatures();
            StudioCheck("disabled_controls_hidden", !Studio.AiControlsVisible && Studio.WorkflowCount == 0);
            StudioCheck("disabled_cancels_job", provider.Token.IsCancellationRequested);
            StudioCheck("disabled_clears_gallery", Studio.GalleryCount == 0);
            StudioCheck("disabled_clears_preview", GUO.Assets.VariantAtlas.Resolve(null, 0, 0,
                GUO.Assets.VariantKind.Static, FixtureStatic) == null);
            Studio.RefreshWorkflows();
            StudioCheck("disabled_no_workflow_discovery", Studio.WorkflowCount == 0);
            StudioCheck("disabled_no_request_pixels", Studio.BuildRequest("blocked", _studioWorkflow) == null);
            int calls = provider.Calls;
            ImageResult blocked = await Studio.RunImageAsync(provider, req);
            StudioCheck("disabled_no_provider_call", blocked.Error == AiFeatures.DisabledMessage && provider.Calls == calls);
            StudioCheck("disabled_manual_placement", Studio.PlaceSplatAt("smoke-splat", 101, 102, 0).StartsWith("placed"));
            StudioCheck("disabled_manual_restore", Studio.ClearThemes().StartsWith("themes cleared"));
            AiFeatures.Apply(true);
            Studio.ApplyAiFeatures();
            StudioCheck("reenabled_controls", Studio.AiControlsVisible);
            // Deliberately ignore cancellation: a late successful provider result
            // must not populate the new lifetime's gallery after re-enable.
            provider.Complete.TrySetResult(new ImageResult { Pngs = new() { FixtureStaticImage().SavePngToBuffer() } });
            ImageResult late = await pending;
            StudioCheck("late_result_discarded", late.Error?.Contains("cancelled") == true && Studio.GalleryCount == 0);
        }
        finally
        {
            provider.Complete.TrySetResult(new ImageResult { Error = "fixture cleanup" });
            AiFeatures.Apply(true);
            Studio.ApplyAiFeatures();
        }

        var rebound = new StudioPendingProvider();
        Task<ImageResult> rebinding = Studio.RunImageAsync(rebound, req);
        try
        {
            await rebound.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Studio.BindTarget(AssetKind.Static, FixtureStatic + 1, FixtureStaticImage());
            StudioCheck("rebind_cancels_old_target", rebound.Token.IsCancellationRequested);
            rebound.Complete.TrySetResult(new ImageResult { Pngs = new() { FixtureStaticImage().SavePngToBuffer() } });
            ImageResult oldTarget = await rebinding;
            StudioCheck("rebind_no_wrong_target_gallery", oldTarget.Error?.Contains("cancelled") == true && Studio.GalleryCount == 0);
        }
        finally
        {
            rebound.Complete.TrySetResult(new ImageResult { Error = "fixture cleanup" });
            Studio.BindTarget(AssetKind.Static, FixtureStatic, FixtureStaticImage());
        }

        var failure = new StudioPendingProvider();
        failure.Complete.SetResult(new ImageResult { Error = "private fixture details" });
        ImageResult failed = await Studio.RunImageAsync(failure, req);
        StudioCheck("failure_names_doctor", failed.Error?.Contains("launchers/comfy/doctor") == true
            && !failed.Error.Contains("private fixture"));

        var closed = new StaticStudioDock();
        AddChild(closed);
        var shutdown = new StudioPendingProvider();
        Task<ImageResult> shuttingDown = closed.RunImageAsync(shutdown, req);
        try
        {
            await shutdown.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            closed.Shutdown();
            StudioCheck("shutdown_cancels_job", shutdown.Token.IsCancellationRequested);
            shutdown.Complete.TrySetResult(new ImageResult { Pngs = new() { FixtureStaticImage().SavePngToBuffer() } });
            ImageResult late = await shuttingDown;
            StudioCheck("shutdown_no_gallery_publish", late.Error?.Contains("cancelled") == true && closed.GalleryCount == 0);
            ImageResult refused = await closed.RunImageAsync(shutdown, req);
            StudioCheck("shutdown_blocks_new_jobs", refused.Error?.Contains("closed") == true && shutdown.Calls == 1);
        }
        finally
        {
            closed.Shutdown();
            shutdown.Complete.TrySetResult(new ImageResult { Error = "fixture cleanup" });
            closed.GetParent()?.RemoveChild(closed);
            closed.QueueFree();
        }
    }

    private sealed class StudioPendingProvider : IImageProvider
    {
        public string Id => "studio-fixture";
        public string Name => "Studio fixture";
        public bool NeedsKey => false;
        internal int Calls;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ImageResult> Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImageResult> RunAsync(ImageRequest request, Action<double> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Token = ct;
            Started.TrySetResult(true);
            return Complete.Task;
        }
    }

    private void StudioCleanup()
    {
        _studioStub?.Dispose();
        _studioStub = null;
        // The stub workflow is for the stub server above, not real ComfyUI:
        // leaving it in the shared workflows folder got it queued for real
        // (LATENT into SaveImage) and failed validation there.
        try
        {
            if (_studioWorkflow != null && File.Exists(_studioWorkflow))
            {
                File.Delete(_studioWorkflow);
            }
        }
        catch (Exception)
        {
        }

        System.Environment.SetEnvironmentVariable("GUO_SPLAT_STAGE", _studioSavedSplatStage);
    }
}
#endif
