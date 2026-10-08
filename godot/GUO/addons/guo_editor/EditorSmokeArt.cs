#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The smoke stage for the art pipeline (ADR-0029). Nothing here starts an outside program or reaches a
/// real service: Pixelorama and Pinta are only detected (and their launch is a dry run), ComfyUI and
/// Retro Diffusion run against stub HTTP servers this stage starts itself. Images are drawn here, or
/// are the client's own art read at test time and kept under build/, never committed.
/// What it proves: export to the exchange folder, a scripted "save" copied back and imported by the
/// watcher into the overlay (trimmed, masked, reduced to UO colour), provenance written, the
/// derived-from-client-art flag, a refusal, the Pinta in-place path, hues.json, then ComfyUI (upload,
/// prompt, websocket progress, history, view) and the Retro Diffusion stub through the dock.
/// </summary>
public partial class EditorSmoke
{
    private readonly Dictionary<string, object> _artReport = new();
    private int _artPhase;
    private ArtStubServer _artStub;
    private Task<ImageResult> _artDirect;
    private readonly List<double> _artProgress = new();
    private string _artRoot, _artSavedExchange;
    private Stopwatch _artClock = new();
    private string _artWorkflow;

    private void ArtCheck(string name, bool ok, string detail = "")
    {
        _artReport[name] = ok;
        if (!ok)
        {
            _artReport["ok"] = false;
            _failures.Add($"Art: {name} {detail}".Trim());
            GD.Print($"[GUO editor] smoke Art FAIL: {name} {detail}");
        }
    }

    private bool StepArt()
    {
        if (_artPhase == 0)
        {
            _report["art"] = _artReport;
            _artReport["ok"] = true;
            _artClock.Restart();
            try
            {
                ArtSync();
                if (!AiFeatures.Enabled)
                {
                    _artReport["ai_skipped"] = "AI features disabled";
                    ArtCleanup();
                    return true;
                }
                ArtStartServices();
            }
            catch (Exception ex)
            {
                ArtCheck("threw", false, $"{ex.GetType().Name}: {ex.Message}");
                ArtCleanup();
                return true;
            }

            _artPhase = 1;
            return false;
        }

        if (_artClock.Elapsed.TotalSeconds > 60)
        {
            ArtCheck("finished_in_time", false, "the stage did not finish within 60 s");
            ArtCleanup();
            return true;
        }

        try
        {
            if (_artPhase == 1 && _artDirect.IsCompleted)
            {
                ArtCheckDirect();
                _artPhase = 2;
            }
            else if (_artPhase == 2 && _art2Done())
            {
                ArtCheckDock();
                ArtCleanup();
                return true;
            }
        }
        catch (Exception ex)
        {
            ArtCheck("threw", false, $"{ex.GetType().Name}: {ex.Message}");
            ArtCleanup();
            return true;
        }

        return false;
    }

    private bool _art2Done() => Art == null || (_artDock != null && _artDock.IsCompleted && Art.GalleryCount > 0);

    private Task<ImageResult> _artDock;

    // --- part one: no network ------------------------------------------------------

    private void ArtSync()
    {
        string root = Path.Combine(_out, $"art_exchange{Suffix}");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        _artRoot = Path.Combine(_out, $"art_project{Suffix}");
        if (Directory.Exists(_artRoot))
        {
            Directory.Delete(_artRoot, recursive: true);
        }

        _artSavedExchange = System.Environment.GetEnvironmentVariable("UO_ART_EXCHANGE");
        System.Environment.SetEnvironmentVariable("UO_ART_EXCHANGE", root);
        WorldProject.OpenOrCreate(_artRoot, _data.ClientData, _data.ClientVersion).Dispose();
        AssetOverlay assets = _data.OpenAssets(_artRoot);
        Dictionary<string, DateTime> stamp = AssetStamp();
        ExternalTools.DryRun = true;
        _artReport["exchange"] = "build/editor_smoke/.../art_exchange";

        // Detection: the one-line outputs the Art dock shows.
        string describe = ExternalTools.Describe();
        _artReport["detect"] = describe;
        _artReport["pinta_found"] = ExternalTools.FindPinta() != null ? "yes" : "no";
        _artReport["pixelorama_found"] = ExternalTools.FindPixelorama() != null ? "yes" : "no";
        string pintaWhy = AssetActions.EditIn(_data, AssetKind.Static, FixtureStatic, FixtureStaticImage(), pinta: true);
        ArtCheck("pinta_missing_gives_install_hint", ExternalTools.FindPinta() != null ? pintaWhy == null : pintaWhy == ExternalTools.PintaHint, pintaWhy ?? "");

        // 1. Export the client's own static to the exchange folder.
        uint staticIndex = EditorData.LandCount + FixtureStatic;
        Image client = _data.ArtImage(staticIndex);
        if (client == null)
        {
            ArtCheck("client_static_present", false);
            return;
        }

        string why = AssetActions.EditIn(_data, AssetKind.Static, FixtureStatic, client, pinta: false);
        string png = Path.Combine(root, "out", $"static_0x{FixtureStatic:X4}.png");
        string json = Path.ChangeExtension(png, ".json");
        ArtCheck("pixelorama_launch_path", ExternalTools.FindPixelorama() != null ? why == null : why != null, why ?? "");
        ArtCheck("export_png_and_sidecar", File.Exists(png) && File.Exists(json));
        JsonNode side = JsonNode.Parse(File.ReadAllText(json));
        ArtCheck("sidecar_fields", (string)side["kind"] == "static" && (int)side["id"] == FixtureStatic
                                   && (int)side["size"][0] == client.GetWidth() && (bool)side["provenance"]["derived_from_client_art"]);
        string hues = Path.Combine(root, "hues.json");
        JsonNode hj = File.Exists(hues) ? JsonNode.Parse(File.ReadAllText(hues)) : null;
        int hueCount = hj?["hues"] is JsonArray ha ? ha.Count : 0;
        _artReport["hues_json_count"] = hueCount;
        ArtCheck("hues_json_from_user_table", hueCount > 1000 && ((JsonArray)hj["hues"])[0]["colors"].AsArray().Count == 32);

        // 2. The scripted "save": what the Pixelorama extension does, with a drawn replacement that
        //    has empty rows at the top (to be trimmed) and half-transparent pixels (to be keyed).
        Image drawn = Image.CreateEmpty(30, 46, false, Image.Format.Rgba8);
        for (int y = 6; y < 46; y++)
        {
            for (int x = 3; x < 27; x++)
            {
                drawn.SetPixel(x, y, Color.Color8((byte)(x * 8 + 3), (byte)(y * 5), 77, (byte)(x == 3 ? 100 : 255)));
            }
        }

        string inbox = Path.Combine(root, "in");
        Directory.CreateDirectory(inbox);
        JsonObject saved = (JsonObject)JsonNode.Parse(File.ReadAllText(json));
        saved["size"] = new JsonArray(drawn.GetWidth(), drawn.GetHeight());
        saved["provenance"]["tool"] = "pixelorama";
        File.WriteAllText(Path.Combine(inbox, $"static_0x{FixtureStatic:X4}.json"), saved.ToJsonString());
        drawn.SavePng(Path.Combine(inbox, $"static_0x{FixtureStatic:X4}.png"));
        List<ArtExchange.Outcome> got = ArtExchange.Poll(_data);
        ArtCheck("watcher_imported_save", got.Count == 1 && got[0].Imported, got.Count > 0 ? got[0].Why ?? "" : "nothing seen");
        ArtCheck("overlay_has_static", assets.Has(AssetKind.Static, FixtureStatic));
        Image back = assets.Load(AssetKind.Static, FixtureStatic);
        _artReport["static_after_import"] = back == null ? null : $"{back.GetWidth()}x{back.GetHeight()}";
        // 30x46, 6 empty rows on top, 3 empty columns each side: trimmed to 24x40.
        ArtCheck("trimmed_to_content", back != null && back.GetWidth() == 24 && back.GetHeight() == 40, _artReport["static_after_import"] as string ?? "");
        ArtCheck("transparency_keyed", back != null && back.GetPixel(0, 0).A8 == 0, "the half-transparent column should be transparent");
        ArtCheck("loader_sees_import", ImagesEqual(_data.ArtImage(staticIndex), back));
        ArtCheck("moved_to_done", File.Exists(Path.Combine(root, "done", $"static_0x{FixtureStatic:X4}.png")) && !File.Exists(Path.Combine(inbox, $"static_0x{FixtureStatic:X4}.png")));
        string rel = assets.RelativePathOf(AssetKind.Static, FixtureStatic);
        ArtProvenance prov = new AssetProvenance(assets).Get(rel);
        _artReport["provenance"] = prov == null ? null : prov.ToJson().ToJsonString();
        ArtCheck("provenance_recorded", prov != null && prov.Tool == "pixelorama" && prov.DerivedFromClientArt && prov.Inputs.Count == 1);
        ArtCheck("derived_listed_for_the_store", new AssetProvenance(assets).Derived().Contains(rel));

        // Regression: native Pixelorama Save output (when supplied) traverses the actual
        // watcher and loaders twice. Fixtures contain original pixels and ID 70 parsed by Godot.
        string proof = System.Environment.GetEnvironmentVariable("GUO_PIXELORAMA_SAVE_PROOF");
        if (!string.IsNullOrEmpty(proof))
        {
            for (int iteration = 1; iteration <= 2; iteration++)
            {
                string source = Path.Combine(proof, $"save{iteration}", "static_0x0046");
                string to = Path.Combine(inbox, "static_0x0046");
                File.Copy(source + ".json", to + ".json", true); File.Copy(source + ".png", to + ".png", true);
                var applied = ArtExchange.Poll(_data);
                ArtCheck($"native_pixelorama_save{iteration}_applied", applied.Count == 1 && applied[0].Imported,
                    applied.FirstOrDefault()?.Why ?? "");
                Image expected = UoPostProcess.Run(Image.LoadFromFile(source + ".png"), AssetKind.Static).Image;
                ArtCheck($"native_pixelorama_save{iteration}_loader_refreshed", ImagesEqual(_data.ArtImage(EditorData.LandCount + 70), expected));
                ArtCheck($"native_pixelorama_save{iteration}_consumed_once", ArtExchange.Poll(_data).Count == 0);
            }
        }
        CheckAnimationWorkbench();

        // 3. Land: a 44x44 save with colour outside the diamond; the mask removes it.
        Image landImg = FixtureLandImage();
        File.WriteAllText(Path.Combine(inbox, "land_0x0244.json"), new ArtSidecar { Kind = "land", Id = FixtureLand, Width = 44, Height = 44, Stem = "land_0x0244", Provenance = new ArtProvenance { Tool = "pixelorama" } }.ToJson().ToJsonString());
        landImg.SavePng(Path.Combine(inbox, "land_0x0244.png"));
        ArtExchange.Poll(_data);
        Image landBack = assets.Load(AssetKind.Land, FixtureLand);
        ArtCheck("land_imported_masked", landBack != null && landBack.GetWidth() == 44 && landBack.GetPixel(0, 0).A8 == 0 && landBack.GetPixel(22, 10).A8 == 255);
        ArtCheck("original_land_not_derived", new AssetProvenance(assets).Get(assets.RelativePathOf(AssetKind.Land, FixtureLand))?.DerivedFromClientArt == false);

        // 4. A refusal: nothing imported, the pair is moved to rejected/ with its reason.
        File.WriteAllText(Path.Combine(inbox, "static_0x0E76.json"), new ArtSidecar { Kind = "static", Id = 0x0E76, Width = 1100, Height = 8, Stem = "static_0x0E76", Provenance = new ArtProvenance { Tool = "pixelorama" } }.ToJson().ToJsonString());
        Image.CreateEmpty(1100, 8, false, Image.Format.Rgba8).SavePng(Path.Combine(inbox, "static_0x0E76.png"));
        ArtExchange.Poll(_data);
        ArtCheck("oversize_refused", !assets.Has(AssetKind.Static, 0x0E76) && File.Exists(Path.Combine(root, "rejected", "static_0x0E76.reason.txt")));

        // 5. Pinta works in place: an edited PNG newer than its sidecar is imported once.
        string pintaPng = ArtExchange.Export(_data, AssetKind.Gump, FixtureGump, FixtureGumpImage(), "pinta");
        ArtCheck("pinta_unedited_not_imported", ArtExchange.Poll(_data).Count == 0);
        FixtureGumpImage().SavePng(pintaPng);
        File.SetLastWriteTimeUtc(pintaPng, DateTime.UtcNow.AddSeconds(5));
        List<ArtExchange.Outcome> pinta = ArtExchange.Poll(_data);
        ArtCheck("pinta_edit_imported", pinta.Count == 1 && pinta[0].Imported && assets.Has(AssetKind.Gump, FixtureGump), pinta.Count > 0 ? pinta[0].Why ?? "" : "nothing seen");
        ArtCheck("pinta_edit_imported_once", ArtExchange.Poll(_data).Count == 0);
        ArtCheck("pinta_provenance", new AssetProvenance(assets).Get(assets.RelativePathOf(AssetKind.Gump, FixtureGump))?.Tool == "pinta");

        // 6. The shared post-process on its own.
        Image half = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        half.SetPixel(1, 1, new Color(0.2f, 0.4f, 0.6f, 1));
        half.SetPixel(2, 2, new Color(0.2f, 0.4f, 0.6f, 0.3f));
        UoPostProcess.Result pp = UoPostProcess.Run(half, AssetKind.Gump);
        Color c = pp.Image.GetPixel(1, 1);
        ArtCheck("postprocess_quantises_5bit", pp.Error == null && pp.Image.GetPixel(2, 2).A8 == 0 && c.R8 == HuesHelperChannel(51) && c.A8 == 255);
        ArtCheck("postprocess_refuses_odd_land", UoPostProcess.Run(Image.CreateEmpty(40, 40, false, Image.Format.Rgba8), AssetKind.Land).Error != null);
        ArtCheck("postprocess_scales_land_nearest", UoPostProcess.Run(Image.CreateEmpty(88, 88, false, Image.Format.Rgba8), AssetKind.Land).Image?.GetWidth() == 44);

        // 7. Reverting drops the provenance with the image.
        assets.Revert(AssetKind.Land, FixtureLand);
        new AssetProvenance(assets).Remove(assets.RelativePathOf(AssetKind.Land, FixtureLand));
        ArtCheck("revert_drops_provenance", new AssetProvenance(assets).Get(assets.RelativePathOf(AssetKind.Land, FixtureLand)) == null);
        _data.ReapplyAssets();

        ArtAnimationSync(assets);

        bool untouched = true;
        foreach (var (name, when) in AssetStamp())
        {
            untouched &= stamp.TryGetValue(name, out DateTime before) && before == when;
        }

        ArtCheck("install_untouched", untouched);
    }

    private void ArtAnimationSync(AssetOverlay assets)
    {
        const int body = 0x0190;
        Image first = Image.CreateEmpty(4, 6, false, Image.Format.Rgba8);
        Image second = Image.CreateEmpty(6, 4, false, Image.Format.Rgba8);
        first.Fill(new Color(0.9f, 0.1f, 0.2f, 1));
        first.SetPixel(0, 0, new Color(1, 1, 1, 127f / 255));
        second.Fill(new Color(0.1f, 0.2f, 0.9f, 1));
        var source = new OverlayAnimationClip { Action = 0, Direction = 1, Fps = 12,
            Frames = new[] { first, second }, Centers = new[] { new Vector2I(2, -1), new Vector2I(3, 1) } };
        string why = assets.ImportAnimation(body, source, new ArtProvenance { Tool = "smoke-original" });
        ArtCheck("animation_imported", why == null, why ?? "");
        if (why != null) return;
        ArtCheck("animation_kind_listed", assets.Has(AssetKind.Animation, body) && assets.Ids(AssetKind.Animation).Contains(body));
        OverlayAnimationClip loaded = new AssetOverlay(_artRoot).LoadAnimation(body, 0, 1, out why);
        ArtCheck("animation_reopened", loaded != null && why == null, why ?? "");
        if (loaded == null) return;
        ArtCheck("animation_frames_centres_fps", loaded.Frames.Length == 2 && loaded.Frames[0].GetWidth() == 4
            && loaded.Frames[0].GetHeight() == 6 && loaded.Frames[1].GetWidth() == 6 && loaded.Frames[1].GetHeight() == 4
            && loaded.Centers.SequenceEqual(source.Centers) && loaded.Fps == 12);
        ArtCheck("animation_alpha_colour", loaded.Frames[0].GetPixel(0, 0).A8 == 0
            && loaded.Frames[0].GetPixel(1, 1).R8 == HuesHelperChannel(first.GetPixel(1, 1).R8));
        Image[] preview = loaded.PreviewFrames();
        ArtCheck("animation_foot_alignment", preview.All(f => f.GetSize() == new Vector2I(6, 6))
            && preview[0].GetPixel(2, 1) == loaded.Frames[0].GetPixel(1, 1)
            && preview[1].GetPixel(1, 1) == loaded.Frames[1].GetPixel(1, 1));
        ArtAnimationExchangeSync(assets, body, loaded);
        ArtCheck("animation_missing_direction_fallback", assets.LoadAnimation(body, 0, 2, out why) == null && why == null);
        source.Direction = 7;
        ArtCheck("animation_second_direction", assets.ImportAnimation(body, source) == null
            && assets.LoadAnimation(body, 0, 7, out _) != null && assets.LoadAnimation(body, 0, 1, out _) != null);
        source.Direction = 1;
        ArtCheck("animation_retained_provenance", assets.ImportAnimation(body, source, new ArtProvenance { Tool = "smoke-original" }) == null
            && new AssetProvenance(assets).Get(assets.RelativePathOf(AssetKind.Animation, body)).DerivedFromClientArt);
        source.Direction = 8;
        ArtCheck("animation_bad_direction_refused", assets.ImportAnimation(body, source) != null);
        source.Direction = 1;
        source.Centers = new[] { new Vector2I(2, -1) };
        ArtCheck("animation_unpaired_centres_refused", assets.ImportAnimation(body, source) != null);
        source.Centers = loaded.Centers;
        source.Fps = double.NaN;
        ArtCheck("animation_bad_fps_refused", assets.ImportAnimation(body, source) != null);
        source.Fps = 12;
        source.Centers = new[] { new Vector2I(5000, 0), new Vector2I(3, 1) };
        ArtCheck("animation_bad_centre_refused", assets.ImportAnimation(body, source) != null);
        source.Frames = Enumerable.Repeat(first, 5).ToArray();
        source.Centers = new[] { Vector2I.Zero, new Vector2I(2000, 2000), Vector2I.Zero, Vector2I.Zero, Vector2I.Zero };
        ArtCheck("animation_preview_budget_refused", assets.ImportAnimation(body, source) != null);

        string path = assets.PathOf(AssetKind.Animation, body);
        string valid = File.ReadAllText(path);
        JsonNode corrupt = JsonNode.Parse(valid);
        corrupt["clips"][0]["frames"][0]["image"] = "../provenance.json";
        File.WriteAllText(path, corrupt.ToJsonString());
        ArtCheck("animation_path_escape_refused", assets.LoadAnimation(body, 0, 1, out why) == null && why != null);
        corrupt = JsonNode.Parse(valid);
        corrupt["clips"].AsArray().Add(corrupt["clips"][0].DeepClone());
        File.WriteAllText(path, corrupt.ToJsonString());
        ArtCheck("animation_duplicate_clip_refused", assets.LoadAnimation(body, 0, 1, out why) == null && why != null);
        File.WriteAllText(path, valid);

        AnimationPanel panel = _assets.Panels.OfType<AnimationPanel>().FirstOrDefault();
        int? picked = panel?.Search("0x0190");
        Inspection shown = _inspector.Current;
        ArtCheck("animation_panel_uses_overlay", picked == body && shown?.Frames.Length == 2 && shown.Fps == 12
            && shown.Text.Contains("editor overlay"));
        if (shown?.Frames.Length == 2)
        {
            Image before = _inspector.Texture.GetImage();
            before.SavePng(Path.Combine(_out, $"animation_overlay_frame_0{Suffix}.png"));
            _inspector._Process(1.0 / shown.Fps + 0.001);
            Image after = _inspector.Texture.GetImage();
            after.SavePng(Path.Combine(_out, $"animation_overlay_frame_1{Suffix}.png"));
            ArtCheck("animation_inspector_played", !before.GetData().SequenceEqual(after.GetData()));
        }

        ArtCheck("animation_revert", assets.Revert(AssetKind.Animation, body)
            && assets.LoadAnimation(body, 0, 1, out why) == null && why == null);
        new AssetProvenance(assets).Remove(assets.RelativePathOf(AssetKind.Animation, body));
        panel?.Search("0x0190");
        ArtCheck("animation_panel_reverts_to_client", _inspector.Current?.Frames.Length > 2
            && !_inspector.Current.Text.Contains("editor overlay"));
    }

    private void ArtAnimationExchangeSync(AssetOverlay assets, int body, OverlayAnimationClip clip)
    {
        string png = ArtExchange.ExportAnimation(_data, body, clip);
        string json = Path.ChangeExtension(png, ".json");
        ArtSidecar side = ArtSidecar.FromJson(JsonNode.Parse(File.ReadAllText(json)));
        _artReport["animation_exchange_sidecar"] = side.ToJson().ToJsonString();
        ArtCheck("animation_exchange_export", File.Exists(png) && side.Kind == "animation" && side.Animation["frames"].AsArray().Count == 2
            && (int)side.Animation["action"] == 0 && (int)side.Animation["direction"] == 1 && (double)side.Animation["fps"] == 12);
        ArtCheck("animation_exchange_original_provenance", !side.Provenance.DerivedFromClientArt && side.Provenance.Inputs[0].StartsWith("overlay:animation:"));
        Image edited = Image.LoadFromFile(png);
        JsonArray rect = side.Animation["frames"][0]["rect"].AsArray();
        edited.SetPixel((int)rect[0] + 1, (int)rect[1] + 1, Colors.Green);
        side.Provenance.Tool = "pixelorama";
        string incoming = Path.Combine(ArtExchange.Sub("in"), Path.GetFileName(png));
        File.WriteAllText(Path.ChangeExtension(incoming, ".json"), side.ToJson().ToJsonString());
        edited.SavePng(incoming);
        var outcomes = ArtExchange.Poll(_data);
        ArtCheck("animation_exchange_watcher", outcomes.Any(o => o.Stem == side.Stem && o.Imported));
        OverlayAnimationClip saved = assets.LoadAnimation(body, clip.Action, clip.Direction, out _);
        ArtCheck("animation_exchange_centres_order_fps", saved != null && saved.Fps == clip.Fps
            && saved.Centers.SequenceEqual(clip.Centers) && saved.Frames[1].GetData().SequenceEqual(clip.Frames[1].GetData()));
        ArtCheck("animation_exchange_pixels", saved?.Frames[0].GetPixel(1, 1) == Colors.Green);
        ArtProvenance p = new AssetProvenance(assets).Get(assets.RelativePathOf(AssetKind.Animation, body));
        ArtCheck("animation_exchange_provenance", p?.Tool == "pixelorama" && !p.DerivedFromClientArt);
        side.Animation["frames"][0]["rect"][2] = 9999;
        string bad = Path.Combine(ArtExchange.Sub("in"), "animation_bad_rect.png");
        File.WriteAllText(Path.ChangeExtension(bad, ".json"), side.ToJson().ToJsonString());
        edited.SavePng(bad);
        ArtCheck("animation_exchange_bad_rect_refused", ArtExchange.Poll(_data).Any(o => o.Stem == "animation_bad_rect" && !o.Imported));
        side = ArtSidecar.FromJson(JsonNode.Parse(File.ReadAllText(json)));
        edited.SetPixel(0, 5, Colors.White);
        bad = Path.Combine(ArtExchange.Sub("in"), "animation_bad_padding.png");
        File.WriteAllText(Path.ChangeExtension(bad, ".json"), side.ToJson().ToJsonString());
        edited.SavePng(bad);
        ArtCheck("animation_exchange_padding_refused", ArtExchange.Poll(_data).Any(o => o.Stem == "animation_bad_padding" && !o.Imported));
        string native = System.Environment.GetEnvironmentVariable("GUO_PIXELORAMA_ANIMATION_SAVE");
        if (!string.IsNullOrEmpty(native))
        {
            string target = Path.Combine(ArtExchange.Sub("in"), Path.GetFileName(native));
            File.Copy(Path.ChangeExtension(native, ".json"), Path.ChangeExtension(target, ".json"), true);
            File.Copy(native, target, true);
            var real = ArtExchange.Poll(_data);
            ArtCheck("native_pixelorama_animation_watcher", real.Any(o => o.Stem == Path.GetFileNameWithoutExtension(native) && o.Imported));
            OverlayAnimationClip read = assets.LoadAnimation(body, clip.Action, clip.Direction, out _);
            ArtCheck("native_pixelorama_animation_frames", read != null && read.Frames.Length == 2 && read.Fps == clip.Fps
                && read.Centers.SequenceEqual(clip.Centers) && read.Frames[0].GetPixel(1, 1) == Colors.Green
                && read.Frames[1].GetData().SequenceEqual(clip.Frames[1].GetData()));
            ArtCheck("native_pixelorama_animation_provenance", new AssetProvenance(assets)
                .Get(assets.RelativePathOf(AssetKind.Animation, body))?.Tool == "pixelorama");
        }
        else
        {
            // Named, so a summary of 67 checks is not mistaken for the 70 a native run has.
            _artReport["skipped"] = "native Pixelorama animation save, 3 checks (GUO_PIXELORAMA_ANIMATION_SAVE not set)";
        }
    }

    private void CheckAnimationWorkbench()
    {
        Image a = Image.CreateEmpty(3, 2, false, Image.Format.Rgba8); a.Fill(Colors.Red);
        Image b = Image.CreateEmpty(2, 3, false, Image.Format.Rgba8); b.Fill(Colors.Blue);
        var doc = new AnimationDocument(2);
        doc.Records[7] = AnimationWorkspace.FromImages(new[] { a, b }, new[] { ((short)-2, (short)3), ((short)4, (short)-5) });
        doc.Records[7].Extra = 345;
        var view = AnimationWorkspace.Open(doc, 70, 1, 2, this);
        string sheetPath = Path.Combine(_out, "animation_fixture.png");
        JsonObject side = view.ExportSheet(sheetPath);
        ArtCheck("animation_sheet_mapping", (int)side["action"] == 1 && (int)side["direction"] == 2 && (int)side["frames"][0]["center_x"] == -2 && (int)side["frames"][1]["center_y"] == -5);
        ushort[] palette = (ushort[])doc.Records[7].Palette.Clone();
        view.ImportMappedSheet(sheetPath, side, 7);
        var roundtrip = ClassicVd.Read(ClassicVd.Write(view.Document)).Records[7];
        ArtCheck("animation_sheet_centers_extra_palette", roundtrip.Frames[0].CenterX == -2 && roundtrip.Frames[1].CenterY == -5 && roundtrip.Extra == 345 && palette.SequenceEqual(roundtrip.Palette));
        ArtCheck("animation_sheet_pixels", ImagesEqual(AnimationWorkspace.ToImage(roundtrip, roundtrip.Frames[0]), a) && ImagesEqual(AnimationWorkspace.ToImage(roundtrip, roundtrip.Frames[1]), b));
        OverlayAnimationClip clip = view.ToClip();
        ArtCheck("animation_workspace_overlay_clip", clip.Action == 1 && clip.Direction == 5 && clip.Frames.Length == 2
            && clip.Centers[0] == new Vector2I(-2, 3) && clip.Centers[1] == new Vector2I(4, -5) && ImagesEqual(clip.Frames[0], a) && ImagesEqual(clip.Frames[1], b));
        if (DisplayServer.GetName() == "headless") view.QueueFree();
        else CaptureAnimationEvidence(view);
        GridPanel panel = _assets.Panels.OfType<ArtPanel>().First(); panel.Search("0x0E75");
        ItemList list = panel.GetChildren().OfType<ItemList>().Single(); int item = list.GetSelectedItems()[0];
        panel.OpenContext(item);
        PopupMenu menu = panel.GetChildren().OfType<PopupMenu>().Single();
        var labels = Enumerable.Range(0, menu.ItemCount).Select(menu.GetItemText).ToArray();
        ArtCheck("asset_context_single_actions", new[] { "Copy ID", "Properties", "Edit in Pixelorama", "Edit in the Pixelorama tab", "Open configured image editor (Pinta)", "Export PNG...", "Replace from image..." }.All(labels.Contains));
        menu.Hide(); list.Select(item == 0 ? 1 : 0, false); panel.OpenContext(item);
        labels = Enumerable.Range(0, menu.ItemCount).Select(menu.GetItemText).ToArray();
        ArtCheck("asset_context_multiple_actions", list.GetSelectedItems().Length == 2 && labels.Contains("Copy IDs") && labels.Contains("Export selected PNGs...") && !labels.Contains("Replace from image..."));
        menu.Hide();
    }

    private async void CaptureAnimationEvidence(AnimationWorkspace view)
    {
        for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(view)) return;
        Image screenshot = view.GetViewport().GetTexture().GetImage();
        screenshot.SavePng(Path.Combine(_out, "animation_workbench.png"));
        view.QueueFree();
    }

    private static byte HuesHelperChannel(int v8)
    {
        // 8-bit value to 5 bits and back through the client's own table.
        uint c = GUO.Utility.HuesHelper.Color16To32((ushort)((v8 >> 3) << 10));
        return (byte)(c & 0xFF);
    }

    private static bool ImagesEqual(Image a, Image b)
    {
        if (a == null || b == null || a.GetSize() != b.GetSize())
        {
            return false;
        }

        for (int y = 0; y < a.GetHeight(); y++)
        {
            for (int x = 0; x < a.GetWidth(); x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (p.A8 != q.A8 || (p.A8 != 0 && (p.R8 != q.R8 || p.G8 != q.G8 || p.B8 != q.B8)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // --- part two: the image services, against stubs ---------------------------------

    private void ArtStartServices()
    {
        Image reply = FixtureStaticImage();
        _artStub = new ArtStubServer(reply.SavePngToBuffer());
        string wfDir = Path.Combine(ArtExchange.Root, "workflows");
        Directory.CreateDirectory(wfDir);
        _artWorkflow = Path.Combine(wfDir, "stub_img2img.json");
        File.WriteAllText(_artWorkflow, ArtStubServer.WorkflowJson);
        ArtCheck("workflow_listed", ComfyUiProvider.Workflows(wfDir).Count == 1);

        var req = new ImageRequest
        {
            Prompt = "a mossy ring",
            WorkflowPath = _artWorkflow,
            InputPng = FixtureStaticImage().SavePngToBuffer(),
            InputName = "client:static:0x0E75",
            Width = 30,
            Height = 40,
            Seed = 1234,
        };
        var comfy = new ComfyUiProvider(_artStub.Url);
        _artDirect = Task.Run(() => comfy.RunAsync(req, p => { lock (_artProgress) { _artProgress.Add(p); } }, CancellationToken.None));
    }

    private void ArtCheckDirect()
    {
        ImageResult r = _artDirect.Result;
        _artReport["comfy_error"] = r.Error;
        ArtCheck("comfy_returned_image", r.Error == null && r.Pngs.Count == 1 && r.Pngs[0].Length > 50, r.Error ?? "");
        double[] progress;
        lock (_artProgress)
        {
            progress = _artProgress.ToArray();
        }

        _artReport["comfy_progress_events"] = progress.Length;
        ArtCheck("comfy_progress_over_websocket", progress.Any(p => p > 0 && p < 1) && progress.Last() == 1.0);
        ArtCheck("comfy_upload_received", _artStub.UploadBytes > 50);
        JsonNode sent = _artStub.LastPrompt;
        ArtCheck("comfy_prompt_bound", sent != null && (string)sent["2"]["inputs"]["text"] == "a mossy ring"
                                       && (string)sent["3"]["inputs"]["text"] == "ugly"
                                       && (long)sent["4"]["inputs"]["seed"] == 1234
                                       && (string)sent["1"]["inputs"]["image"] == _artStub.UploadedName
                                       && (int)sent["6"]["inputs"]["width"] == 30);
        ArtCheck("comfy_provenance_fields", r.Model == "stub.safetensors" && r.Workflow == "stub_img2img.json" && r.Seed == 1234);
        ArtCheck("comfy_returned_artifacts", r.Error == null && r.Files.Count == 3
            && r.Files.Count(f => f.Kind == ArtifactKind.Image && f.FileName == "guo_00001_.png") == 1
            && r.Files.Count(f => f.Kind == ArtifactKind.Audio && f.FileName == "sfx_test.mp3") == 1
            && r.Files.Count(f => f.Kind == ArtifactKind.Model && f.FileName == "splat_test.ply") == 1,
            string.Join(",", r.Files.Select(f => $"{f.Kind}:{f.FileName}:{f.Bytes.Length}")));
        ArtCheck("provenance_legacy_ai", ArtProvenance.FromJson(JsonNode.Parse("{\"tool\":\"comfyui\"}")).AiGenerated
                                         && ArtProvenance.FromJson(JsonNode.Parse("{\"tool\":\"retrodiffusion\"}")).AiGenerated);
        ArtCheck("provenance_non_ai", !ArtProvenance.FromJson(JsonNode.Parse("{\"tool\":\"pixelorama\"}")).AiGenerated
                                      && !ArtProvenance.FromJson(JsonNode.Parse("{\"tool\":\"comfyui\",\"ai\":false}")).AiGenerated);

        // Retro Diffusion: the stub only. No key means no call; with a key the header carries it.
        var noKey = new RetroDiffusionProvider(_artStub.Url, () => null);
        ImageResult none = Task.Run(() => noKey.RunAsync(new ImageRequest { Prompt = "x" }, null, CancellationToken.None)).GetAwaiter().GetResult();
        ArtCheck("retrodiffusion_needs_key", none.Error != null && none.Error.Contains("Retro Diffusion") && _artStub.RdCalls == 0);
        var rd = new RetroDiffusionProvider(_artStub.Url, () => "stub-key");
        ImageResult ok = Task.Run(() => rd.RunAsync(new ImageRequest { Prompt = "a ring", Width = 32, Height = 32 }, null, CancellationToken.None)).GetAwaiter().GetResult();
        ArtCheck("retrodiffusion_stub_image", ok.Error == null && ok.Pngs.Count == 1 && _artStub.RdToken == "stub-key" && _artStub.RdCalls == 1, ok.Error ?? "");

        if (Art != null)
        {
            // The same through the dock: input bound to the inspected asset, then "Import to overlay".
            var ins = Inspection.Still("Art", $"0x{FixtureStatic:X4}", FixtureStaticImage(), "");
            ins.ArtKind = AssetKind.Static;
            ins.ArtId = FixtureStatic;
            ImageRequest dockReq = Art.BuildRequest("a mossy ring", _artWorkflow, ins);
            dockReq.Seed = 99;
            _artDock = Art.RunAsync(new ComfyUiProvider(_artStub.Url), dockReq);
        }
    }

    private void ArtCheckDock()
    {
        if (Art == null)
        {
            _artReport["dock"] = "absent";
            return;
        }

        string said = Art.ImportSelected();
        _artReport["dock_import"] = said;
        AssetOverlay assets = _data.Assets;
        string rel = assets.RelativePathOf(AssetKind.Static, FixtureStatic);
        ArtProvenance p = new AssetProvenance(assets).Get(rel);
        ArtCheck("dock_gallery", Art.GalleryCount == 1 && Art.LastResult.Pngs.Count == 1);
        ArtCheck("dock_imported_result", said.StartsWith("imported"), said);
        ArtCheck("dock_provenance", p != null && p.Tool == "comfyui" && p.AiGenerated && p.Model == "stub.safetensors"
                                    && p.Workflow == "stub_img2img.json" && p.Seed == 99
                                    && p.Inputs.Count == 1 && p.Inputs[0] == "overlay:static:0x0E75" && p.DerivedFromClientArt,
            p?.ToJson().ToJsonString() ?? "none");
        _artReport["dock_provenance_json"] = p?.ToJson().ToJsonString();
        SplatCheck();

        string saved = Art.SaveArtifacts();
        string artDir = Path.Combine(ArtExchange.Root, "artifacts");
        string mp3Sidecar = Path.Combine(artDir, "sfx_test.mp3.json");
        string plySidecar = Path.Combine(artDir, "splat_test.ply.json");
        ArtCheck("dock_saved_artifacts", saved.StartsWith("saved 2 artifact(s)")
            && File.Exists(Path.Combine(artDir, "sfx_test.mp3"))
            && File.Exists(Path.Combine(artDir, "splat_test.ply")), saved);
        JsonNode mp3Prov = File.Exists(mp3Sidecar) ? JsonNode.Parse(File.ReadAllText(mp3Sidecar)) : null;
        JsonNode plyProv = File.Exists(plySidecar) ? JsonNode.Parse(File.ReadAllText(plySidecar)) : null;
        ArtCheck("dock_artifact_provenance", (string)mp3Prov?["kind"] == "audio" && (string)plyProv?["kind"] == "model"
            && (string)mp3Prov?["tool"] == "comfyui" && (string)plyProv?["workflow"] == "stub_img2img.json",
            $"{mp3Prov?.ToJsonString()} | {plyProv?.ToJsonString()}");
    }

    // --- part three: generated splat LODs through the runtime loader --------------

    /// <summary>
    /// The ComfyUI proofs leave LOD PLYs under build/comfy (gitignored, so
    /// absent on a fresh checkout): when they are there, parse all four
    /// levels, check the LOD selection and draw each end through SplatBatcher.
    /// When they are not, this reports the skip rather than failing.
    /// </summary>
    private void SplatCheck()
    {
        string comfy = FindComfyDir();
        string[] lods = { "armoire_lod0.ply", "armoire_lod1.ply", "armoire_lod2.ply", "armoire_lod3.ply" };
        string[] paths = lods.Select(f => comfy == null ? null : Path.Combine(comfy, "multi_lod", f)).ToArray();
        bool present = paths.All(p => p != null && File.Exists(p));
        _artReport["splat_lods_present"] = present;
        if (!present)
        {
            _artReport["splat"] = "skipped: no build/comfy/multi_lod (run tools/comfy first)";
            return;
        }

        var chain = GUO.Renderer.SplatLodChain.Load(paths);
        int[] counts = chain.Levels.Select(l => l.Gaussians.Length).ToArray();
        ArtCheck("splat_parse_counts", counts.SequenceEqual(new[] { 64800, 16200, 4050, 1012 }), string.Join("/", counts));
        ArtCheck("splat_bounds", chain.BoundsMax.X > chain.BoundsMin.X && chain.BoundsMax.Y > chain.BoundsMin.Y
            && chain.Levels[0].OpacityMass > chain.Levels[3].OpacityMass,
            $"{chain.BoundsMin} {chain.BoundsMax} mass {chain.Levels[0].OpacityMass:0}/{chain.Levels[3].OpacityMass:0}");
        ArtCheck("splat_select", chain.Select(1000) == 0 && chain.Select(200) == 1 && chain.Select(100) == 2
            && chain.Select(40) == 3 && chain.Select(10) == -1);

        Rid parent = RenderingServer.CanvasItemCreate();
        try
        {
            using var batcher = new GUO.Renderer.SplatBatcher(parent);
            batcher.SetChain(chain, Vector2.Zero);
            batcher.Draw(1000f / batcher.WorldSize); // radius 1000: lod0
            bool near = batcher.LastLevel == 0 && batcher.DrawnSplats == 64800;
            batcher.Draw(100f / batcher.WorldSize); // radius 100: lod2
            bool mid = batcher.LastLevel == 2 && batcher.DrawnSplats == 4050;
            batcher.Draw(0.001f); // far below the last threshold: culled
            bool far = batcher.LastLevel == -1 && batcher.DrawnSplats == 0;
            ArtCheck("splat_draw_lods", near && mid && far, $"near {batcher.LastLevel}/{batcher.DrawnSplats}");
        }
        finally
        {
            RenderingServer.FreeRid(parent);
        }
    }

    /// <summary>Repo build/comfy: up from the exchange folder to the config.bat marker.</summary>
    private static string FindComfyDir()
    {
        try
        {
            string dir = Path.GetFullPath(ArtExchange.Root);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "launchers", "_shared", "config.bat")))
                {
                    return Path.Combine(dir, "build", "comfy");
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private void ArtCleanup()
    {
        _artStub?.Dispose();
        _artStub = null;
        // Same as the studio stub: not for real ComfyUI, so remove it from
        // the shared workflows folder when the checks are done.
        try
        {
            if (_artWorkflow != null && File.Exists(_artWorkflow))
            {
                File.Delete(_artWorkflow);
            }
        }
        catch (Exception)
        {
        }

        ExternalTools.DryRun = false;
        System.Environment.SetEnvironmentVariable("UO_ART_EXCHANGE", _artSavedExchange);
        Art?.Shutdown();
    }
}

/// <summary>A stand-in for the two services, on a loopback port: ComfyUI's routes and Retro Diffusion's one.</summary>
internal sealed class ArtStubServer : IDisposable
{
    public const string WorkflowJson = @"{
  ""1"": {""class_type"": ""LoadImage"", ""inputs"": {""image"": ""example.png""}},
  ""2"": {""class_type"": ""CLIPTextEncode"", ""inputs"": {""text"": """", ""clip"": [""5"", 1]}},
  ""3"": {""class_type"": ""CLIPTextEncode"", ""inputs"": {""text"": ""ugly"", ""clip"": [""5"", 1]}},
  ""4"": {""class_type"": ""KSampler"", ""inputs"": {""seed"": 1, ""steps"": 4, ""positive"": [""2"", 0], ""negative"": [""3"", 0], ""latent_image"": [""6"", 0], ""model"": [""5"", 0]}},
  ""5"": {""class_type"": ""CheckpointLoaderSimple"", ""inputs"": {""ckpt_name"": ""stub.safetensors""}},
  ""6"": {""class_type"": ""EmptyLatentImage"", ""inputs"": {""width"": 512, ""height"": 512, ""batch_size"": 1}},
  ""7"": {""class_type"": ""VAEDecode"", ""inputs"": {""samples"": [""4"", 0], ""vae"": [""5"", 2]}},
  ""8"": {""class_type"": ""SaveImage"", ""inputs"": {""images"": [""7"", 0], ""filename_prefix"": ""guo""}}
}";

    private readonly HttpListener _http = new();
    private readonly byte[] _png;
    private readonly CancellationTokenSource _stop = new();
    private DateTime _queuedAt = DateTime.MaxValue;
    private readonly SemaphoreSlim _queued = new(0, 8);

    public string Url { get; }
    public JsonNode LastPrompt { get; private set; }
    public int UploadBytes { get; private set; }
    public string UploadedName { get; private set; } = "";
    public int RdCalls { get; private set; }
    public string RdToken { get; private set; } = "";

    public ArtStubServer(byte[] png)
    {
        _png = png;
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _http.Prefixes.Add(Url + "/");
        _http.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _http.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (ctx.Request.IsWebSocketRequest)
            {
                WebSocketContext wsc = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await _queued.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                for (int i = 1; i <= 3; i++)
                {
                    await Send(wsc.WebSocket, $"{{\"type\":\"progress\",\"data\":{{\"value\":{i},\"max\":4}}}}").ConfigureAwait(false);
                    await Task.Delay(150).ConfigureAwait(false);
                }

                await Send(wsc.WebSocket, "{\"type\":\"executing\",\"data\":{\"node\":null}}").ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);
                return;
            }

            if (path == "/upload/image")
            {
                byte[] body = await ReadAll(ctx).ConfigureAwait(false);
                string text = Encoding.Latin1.GetString(body);
                int at = text.IndexOf("filename=\"", StringComparison.Ordinal);
                UploadedName = at >= 0 ? text[(at + 10)..text.IndexOf('"', at + 10)] : "";
                UploadBytes = body.Length;
                await Json(ctx, $"{{\"name\":\"{UploadedName}\",\"subfolder\":\"\",\"type\":\"input\"}}").ConfigureAwait(false);
            }
            else if (path == "/prompt")
            {
                LastPrompt = JsonNode.Parse(Encoding.UTF8.GetString(await ReadAll(ctx).ConfigureAwait(false)))["prompt"];
                _queuedAt = DateTime.UtcNow;
                _queued.Release();
                await Json(ctx, "{\"prompt_id\":\"p1\",\"number\":1,\"node_errors\":{}}").ConfigureAwait(false);
            }
            else if (path.StartsWith("/history/"))
            {
                bool done = DateTime.UtcNow - _queuedAt > TimeSpan.FromMilliseconds(900);
                await Json(ctx, done
                    ? "{\"p1\":{\"outputs\":{\"7\":{\"images\":[{\"filename\":\"guo_00001_.png\",\"subfolder\":\"\",\"type\":\"output\"}]},\"8\":{\"audio\":[{\"filename\":\"sfx_test.mp3\",\"subfolder\":\"\",\"type\":\"output\"}]},\"9\":{\"models\":[{\"filename\":\"splat_test.ply\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"
                    : "{}").ConfigureAwait(false);
            }
            else if (path == "/view")
            {
                string file = ctx.Request.QueryString["filename"] ?? "";
                byte[] bytes = _png;
                string mime = "image/png";
                if (file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = System.Text.Encoding.ASCII.GetBytes("ID3stub-mp3-bytes");
                    mime = "audio/mpeg";
                }
                else if (file.EndsWith(".ply", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = System.Text.Encoding.ASCII.GetBytes("ply\nformat ascii 1.0\nstub-splat");
                    mime = "application/octet-stream";
                }

                ctx.Response.ContentType = mime;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            else if (path == "/v1/inferences")
            {
                RdCalls++;
                RdToken = ctx.Request.Headers["X-RD-Token"] ?? "";
                await ReadAll(ctx).ConfigureAwait(false);
                await Json(ctx, $"{{\"model\":\"rd_stub\",\"base64_images\":[\"{Convert.ToBase64String(_png)}\"]}}").ConfigureAwait(false);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task Send(WebSocket ws, string text) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

    private static async Task<byte[]> ReadAll(HttpListenerContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static async Task Json(HttpListenerContext ctx, string json)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Close();
    }
}
#endif
