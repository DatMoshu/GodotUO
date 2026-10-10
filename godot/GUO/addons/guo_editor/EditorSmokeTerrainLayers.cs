#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Renderer;

/// <summary>
/// The smoke for the Layers dock: add, update, remove and reload a terrain
/// overlay in a throwaway layer folder, through the dock's own entry points.
/// GUO_LAYER_DIR is pointed at the fixture for the steps and restored after,
/// so the machine's real layers.json is never touched.
/// </summary>
public partial class EditorSmoke
{
    /// <summary>The Layers dock the plugin made, for the terrain-layer checks.</summary>
    public LayersDock Layers { get; set; }

    private void AddTerrainLayerSteps()
    {
        string dir = Path.Combine(_out, "terrain_layers");
        _steps.Add((1, () =>
        {
            if (Layers == null)
            {
                Expect(false, "terrain_layers_dock_missing");
                return;
            }

            Directory.CreateDirectory(Path.Combine(dir, "overlays"));
            var shot = Image.CreateEmpty(16, 16, false, Image.Format.Rgb8);
            shot.Fill(new Color(0.8f, 0.1f, 0.1f));
            Expect(shot.SavePng(Path.Combine(dir, "overlays", "smoke.png")) == Error.Ok, "terrain_layers_fixture_png");
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", dir);
            Layers.Refresh();
            Expect(Layers.LayerIds.Count == 0, "terrain_layers_start_empty");
        }));
        _steps.Add((1, () =>
        {
            Layers.SetForm("smoke_disc", "Smoke disc", false, 0, 10, 20, 15, 25, "overlays/smoke.png", "smoke", 0.5f);
            string added = Layers.AddFromUi();
            Expect(added.StartsWith("Added smoke_disc"), "terrain_layers_add");
            Expect(File.Exists(Path.Combine(dir, "layers.json")), "terrain_layers_manifest_written");
            Layers.Refresh();
            Layers.Select("smoke_disc");
            Expect(Layers.LayerIds.Contains("smoke_disc"), "terrain_layers_listed");
            var reloaded = TerrainLayers.Load(out _);
            var back = reloaded.FirstOrDefault(l => l.Id == "smoke_disc");
            Expect(back != null && back.X0 == 10 && back.Y1 == 25 && Math.Abs(back.Opacity - 0.5f) < 1e-6
                && back.Kind == LayerKind.Overlay && back.Prompt == "smoke", "terrain_layers_round_trip");
            Expect(Layers.Preview != null, "terrain_layers_preview");
        }));
        _steps.Add((1, () =>
        {
            Layers.SetForm("smoke_disc", "Smoke disc renamed", true, 1, 11, 21, 16, 26, "overlays/smoke.png", "smoke2", 1f);
            string updated = Layers.UpdateSelected();
            var back = TerrainLayers.Load(out _).FirstOrDefault(l => l.Id == "smoke_disc");
            Expect(updated.StartsWith("Updated smoke_disc") && back != null && back.Kind == LayerKind.Underlay
                && back.Facet == 1 && back.Name == "Smoke disc renamed", "terrain_layers_update");
        }));
        _steps.Add((1, () =>
        {
            string removed = Layers.RemoveSelected();
            var back = TerrainLayers.Load(out LayerDefaults _);
            Expect(removed.StartsWith("Removed smoke_disc") && !back.Any(l => l.Id == "smoke_disc"), "terrain_layers_remove");
            System.Environment.SetEnvironmentVariable("GUO_LAYER_DIR", null);
            Layers.Refresh();
        }));
    }
}
#endif
