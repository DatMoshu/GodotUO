#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;

/// <summary>PR33 interface review through real GUO controls; no generators or shard mutations.</summary>
public partial class EditorTour
{
    private bool ReviewVisible(Control control) => Hidden(control, control.GetGlobalRect()) == null;

    private async Task ReviewFrame(string caption)
    {
        Say(caption, top: true);
        await Shot(8, 12);
        await Secs(8); // Let the person watching read the same frame that the recording holds.
    }

    private async Task JarJarReview()
    {
        Node root = EditorInterface.Singleton.GetBaseControl();
        var regions = All<RegionsDock>(root).FirstOrDefault();
        var studio = All<StaticStudioDock>(root).FirstOrDefault();
        var art = All<ArtDock>(root).FirstOrDefault();
        var layers = All<LayersDock>(root).FirstOrDefault();
        Check(regions != null && studio != null && art != null && layers != null, "all four PR33 editor panels are registered");
        if (regions == null || studio == null || art == null || layers == null) return;
        Check(AiFeatures.Enabled, "AI surfaces enabled for this review");
        if (!AiFeatures.Enabled) return;

        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName); _world.GoTo(0, 1496, 1628);
        await Frames(40);
        await ReviewFrame("JarJar PR33, now on main: regional music/SFX, themed repaint, splats, voice and terrain decals. This is an interface tour, not a successful generation claim. ComfyUI model setup is still required.");

        // Fixture lives only in this tour's output; shared shard regions are never edited.
        string previousDist = System.Environment.GetEnvironmentVariable("UO_SHARD_DIST");
        Vector2 minRegions = regions.CustomMinimumSize, minStudio = studio.CustomMinimumSize, minArt = art.CustomMinimumSize, minLayers = layers.CustomMinimumSize;
        string fixture = Path.Combine(_out, "review_shard");
        Directory.CreateDirectory(Path.Combine(fixture, "Data"));
        File.WriteAllText(Path.Combine(fixture, "Data", "regions.json"), "[{\"Name\":\"Review fixture: Britain blacksmith\",\"$type\":\"TownRegion\",\"Map\":\"Felucca\",\"Priority\":50,\"Area\":[\"(1490, 1620, -128, 20, 20, 256)\"]}]");
        void ReloadRegions() => All<Button>(regions).First(b => b.Text == "Reload").EmitSignal(Button.SignalName.Pressed);
        try
        {
            regions.CustomMinimumSize = new Vector2(0, 600); regions.MakeVisible();
            await Frames(10);
            await ReviewFrame("Regions on a fresh setup: the dock expects a shard regions.json. When it is absent, there is no guided setup. The next frame loads a clearly labelled scratch fixture so we can inspect the form.");
            System.Environment.SetEnvironmentVariable("UO_SHARD_DIST", fixture); ReloadRegions();
            var list = All<ItemList>(regions).First(); list.Select(0); list.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
            await Frames(10);
            Check(regions.RegionCount == 1, "scratch region loaded through the normal Regions controls");
            Check(All<Button>(regions).Any(b => b.Text == "Generate"), "Regions exposes audio generation");
            await Frames(12);
            Check(ReviewVisible(All<Button>(regions).First(b => b.Text == "Generate")), "Regions Generate is visible and unclipped");
            Check(ReviewVisible(All<Button>(regions).First(b => b.Text.StartsWith("Save regions.json"))), "Regions Save is visible and unclipped");
            await ReviewFrame("Regions: search and facet filter, region bounds, music id and theme painting. Audio has prompt, duration and separate Music/SFX layers, plus preview controls. Generate is not pressed: required ComfyUI models are missing.");
            var audioLayer = All<OptionButton>(regions).First(b => b.ItemCount == 2 && b.GetItemText(0) == "Music");
            audioLayer.Select(1); audioLayer.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            await ReviewFrame("SFX is an independent ambient layer. Music replaces/crossfades the base track; SFX layers over it. This switches the real dropdown without starting generation or writing an audio manifest.");

            studio.CustomMinimumSize = new Vector2(0, 600); studio.MakeVisible();
            var asset = _assets.Panel<ArtPanel>(); asset.Search("0x0E75"); await Frames(8);
            if (_inspector.Current?.Image != null) studio.BindTarget(AssetKind.Static, 0x0E75, _inspector.Current.Image);
            Check(All<Button>(studio).Any(b => b.Text == "Place splat"), "Static Studio exposes splat placement");
            await Frames(12);
            Check(ReviewVisible(All<Button>(studio).First(b => b.Text == "Place splat")), "Static Studio Place splat is visible and unclipped");
            await ReviewFrame("Static Studio: bind inspected art, choose an img2img workflow, preview in World, remove a background or use the original mask, and save a named theme. Below: staged splat placement and zone theme application. No generator is invoked.");

            art.CustomMinimumSize = new Vector2(0, 600); art.MakeVisible();
            Check(All<Button>(art).Any(b => b.Text == "Save gallery image as theme variant"), "Art exposes named theme variants");
            await Frames(12);
            Check(ReviewVisible(All<Button>(art).First(b => b.Text == "Save audio/3D artifacts")), "Art artifact saving is visible and unclipped");
            await ReviewFrame("Art: provider/URL, workflow, prompt, seed, guidance, steps and size. JarJar added named theme variants and saving audio/3D artifacts. Pick an inspected asset as input, generate, then review the gallery before saving.");

            layers.CustomMinimumSize = new Vector2(0, 600); layers.MakeVisible();
            Check(All<Button>(layers).Any(b => b.Text.Contains("Add")), "Layers authoring controls exist");
            await ReviewFrame("Terrain Layers: register overlay/underlay imagery by facet and bounds, preview it, then reload World. This is the editor side of the decal workflow. The in-game Audio Zones, Repaint, Splat and Voice dialogs are separate client screens.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("UO_SHARD_DIST", previousDist);
            ReloadRegions();
            regions.CustomMinimumSize = minRegions; studio.CustomMinimumSize = minStudio;
            art.CustomMinimumSize = minArt; layers.CustomMinimumSize = minLayers;
        }
        (GetParent() as EditorPlugin)?.HideBottomPanel(); EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        await ReviewFrame("Review result: UI controls shown from the real editor; scratch fixture only. Regional audio is not out-of-box ready yet: endpoint/model checks, missing-region setup and prompt-failure handling need work. Screenshots and per-step checks are saved with this run.");
    }
}
#endif
