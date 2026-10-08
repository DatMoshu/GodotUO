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
/// variant save (overlay PNG + theme entry + provenance), staged placement
/// JSON, and theme activate/clear.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _studioReport = new();
    private int _studioPhase;
    private ArtStubServer _studioStub;
    private Task<ImageResult> _studioDirect;
    private string _studioRoot, _studioSavedSplatStage;
    private Stopwatch _studioClock = new();
    private string _studioWorkflow;

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
            else if (_studioPhase == 2)
            {
                StudioCheckDock();
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
        ImageResult docked = Task.Run(() => Studio.RunImageAsync(new ComfyUiProvider(_studioStub.Url), req)).GetAwaiter().GetResult();
        StudioCheck("dock_gallery", docked.Error == null && Studio.GalleryCount == 1, docked.Error ?? "");
        string said = Studio.SaveVariant(0, "smoke-studio");
        StudioCheck("variant_saved", said.StartsWith("saved"), said);
        string themePath = Path.Combine(StaticStudioDock.ThemeDir(_data), "smoke-studio.theme.json");
        StudioCheck("theme_file", File.Exists(themePath), themePath);
        JsonNode theme = File.Exists(themePath) ? JsonNode.Parse(File.ReadAllText(themePath)) : null;
        StudioCheck("theme_entry", (int?)theme?["entries"]?[0]?["match"]?[0] == FixtureStatic
            && (int?)theme?["entries"]?[0]?["variant"] == 0xF000, theme?.ToJsonString() ?? "none");
        string overlayPng = Path.Combine(_studioRoot, "assets", "art", "statics", "0xF000.png");
        StudioCheck("overlay_variant_png", File.Exists(overlayPng), overlayPng);
        ArtProvenance p = new AssetProvenance(_data.Assets).Get("assets/art/statics/0xF000.png");
        StudioCheck("variant_provenance", p != null && p.Tool == "comfyui" && p.DerivedFromClientArt
            && p.Inputs.Count == 1 && p.Inputs[0] == "client:static:0x0E75", p?.ToJson().ToJsonString() ?? "none");
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

        string applied = Studio.ApplyZone("smoke-studio", "0,0,0,0,0");
        StudioCheck("zone_applied", applied.StartsWith("theme smoke-studio"), applied);
        StudioCheck("theme_active", GUO.Game.Managers.ThemeManager.Active.Count == 1,
            GUO.Game.Managers.ThemeManager.Active.Count.ToString());
        string cleared = Studio.ClearThemes();
        StudioCheck("themes_cleared", cleared.StartsWith("themes cleared")
            && GUO.Game.Managers.ThemeManager.Active.Count == 0, cleared);
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
