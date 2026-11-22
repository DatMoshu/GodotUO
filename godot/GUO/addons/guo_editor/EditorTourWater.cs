// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer.Water;

public partial class EditorTour
{
    private async Task WaterSeg()
    {
        // A single-segment run does not have an earlier World segment to boot it.
        (GetParent() as GuoEditorPlugin)?.ShowInWorld(0, 1490, 1755);
        for (int i = 0; i < 900 && !WorldUp && _world.Error == null; i++) await Frames(1);
        Check(WorldUp, "the World view initialized for water authoring");
        if (!WorldUp) return;
        var panel = _world.WaterWorkspace;
        int tab = _world.SettingsTabs.CurrentTab;
        var previous = panel.Style;
        try
        {
            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
            _world.SettingsTabs.CurrentTab = 2;
            _world.GoTo(0, 1490, 1755);
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            panel.WorldToggle.ButtonPressed = true;
            await Frames(12);
            Check(panel.IsVisibleInTree() && panel.Size.X >= 240 && panel.Size.Y >= 150,
                "the Water workspace is visible at a usable size");
            Check(WaterRender.EditorWorld == _world.Host.World, "the draft previews only in this editor world");
            Say("Water has editable presets and a synthetic preview. Preview in World applies the same style to the map's wet tiles without saving a player setting.");
            await Shot(3);
            float before = panel.Style.Glint;
            panel.NumberInput("Glints").Value = 0;
            Check(panel.Style.Glint == 0 && panel.Dirty, "the numeric control updates the draft");
            panel.Undo(); Check(panel.Style.Glint == before, "water tuning is undoable");
            panel.Redo(); Check(panel.Style.Glint == 0, "water tuning is redoable");
            panel.Preview.Splash();
            Say("Test a spreading splash or a moving synthetic hull, then freeze the water or compare with Classic tiles. These tests need no shard.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = true;
            await Frames(8);
            Check(WaterRender.Resolve(GUO.Configuration.ProfileManager.CurrentProfile) == null, "Classic comparison bypasses the draft");
            Say("Classic comparison draws the original tiles. The draft remains available when comparison is turned off.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = false;
            string zip = Path.Combine(_out, "water-authoring.zip");
            WaterAuthoring.Export(panel.Style, zip, "water-tour", "1.0.0", "GUO tour", "CC0-1.0");
            Check(File.Exists(zip), "a data-only water pack was exported locally");
            Say("Save a local draft or export a validated Store water pack. A pack carries only style numbers and an original palette swatch; nothing is published or activated here.");
            await Shot(3);
        }
        finally
        {
            panel.WorldToggle.ButtonPressed = false; panel.ClassicToggle.ButtonPressed = false;
            panel.LoadStyle(previous); _world.SettingsTabs.CurrentTab = tab;
        }
    }

    /// <summary>
    /// Readiness proof for the Water panel's "Shallows and wakes" draft: at a real coast of a
    /// staged seabed (--guo-editor-data-stage; skipped without one), the same camera for each
    /// treatment, each control alone, a draft saved and reloaded, and the preview switched off.
    /// Stills of the World view go to shallows/ in the tour's folder, with shallows.csv
    /// (share of World view pixels that differ from the Defaults still).
    /// </summary>
    private async Task WaterShallowsSeg()
    {
        if (string.IsNullOrWhiteSpace(EditorSmoke.ArgValue("--guo-editor-data-stage")))
        {
            Skip("no staged seabed was given (--guo-editor-data-stage); the controls act on translucent water only");
            return;
        }
        int facet = int.TryParse(EditorSmoke.ArgValue("--guo-editor-tour-water-facet"), out int fc) ? fc : 1;
        (GetParent() as GuoEditorPlugin)?.ShowInWorld(facet, 3515, 2598);
        for (int i = 0; i < 900 && !WorldUp && _world.Error == null; i++) await Frames(1);
        Check(WorldUp, "the World view initialized on the staged seabed");
        if (!WorldUp) return;
        var panel = _world.WaterWorkspace;
        int tab = _world.SettingsTabs.CurrentTab;
        var previous = panel.Style;
        string dir = Path.Combine(_out, "shallows");
        Directory.CreateDirectory(dir);
        using var csv = new StreamWriter(Path.Combine(dir, "shallows.csv"));
        csv.WriteLine("still,changedShareVsDefaults");
        async Task<Image> Still(string name, Image reference)
        {
            await Frames(30);
            Image shot = _world.Capture();
            if (shot == null) return null;
            shot.SavePng(Path.Combine(dir, name + ".png"));
            csv.WriteLine(FormattableString.Invariant($"{name},{(reference == null ? 0 : Differ(reference, shot)):F5}"));
            csv.Flush();
            return shot;
        }
        try
        {
            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
            _world.SettingsTabs.CurrentTab = 2;
            _world.GoTo(facet, 3515, 2598);
            panel.LoadStyle(WaterAuthoring.Presets[3]);
            panel.FreezeToggle.ButtonPressed = true; // a still surface: two stills differ only by the controls
            panel.SetShallows(WaterShallows.Defaults);
            panel.WorldToggle.ButtonPressed = true;
            await Frames(120);
            Image defaults = await Still("defaults", null);
            Check(defaults != null, "the World view gives a still");
            if (defaults == null) return;
            Image again = await Still("defaults-again", defaults);
            float noise = Differ(defaults, again);
            Check(noise < 0.002f, $"two stills of the same draft are the same picture ({noise:P2} differ)");

            var treatments = new (string Name, WaterShallows Value)[]
            {
                ("harbor", new WaterShallows(6f, 10f, 12f, .25f, .7f, .7f)),
                ("open-sea", new WaterShallows(4f, 8f, 9f, .3f, 1.3f, 1.6f)),
                ("clear-lagoon", new WaterShallows(14f, 22f, 30f, .8f, .9f, 1f)),
            };
            Image lagoon = null;
            foreach (var t in treatments)
            {
                panel.SetShallows(t.Value);
                Image shot = await Still(t.Name, defaults);
                float d = Differ(defaults, shot);
                Check(d > 0.01f, $"{t.Name} looks different from the defaults ({d:P1} of the view)");
                if (t.Name == "clear-lagoon") lagoon = shot;
            }
            // Each control alone, from the defaults to one end of its range.
            var d0 = WaterShallows.Defaults;
            var single = new (string Name, WaterShallows Value)[]
            {
                ("only-clear-water-reach-3", d0 with { FadeStart = 3f }),
                ("only-deep-water-from-24", d0 with { FadeEnd = 24f }),
                ("only-seabed-darkening-40", d0 with { FloorReach = 40f }),
                ("only-sand-bars-0", d0 with { SandBars = 0f }),
                ("only-wake-foam-0", d0 with { WakeFoam = 0f }),
                ("only-wake-length-3", d0 with { WakeLength = 3f }),
            };
            foreach (var t in single)
            {
                panel.SetShallows(t.Value);
                Image shot = await Still(t.Name, defaults);
                float d = Differ(defaults, shot);
                GD.Print(FormattableString.Invariant($"[water shallows] {t.Name}: {d:P2} of the view differs from the defaults"));
            }
            // Save, go back to the defaults, reload: the lagoon again.
            string draft = Path.Combine(dir, "clear-lagoon.v2.json");
            WaterShallows.Save(panel.Style, treatments[2].Value, draft);
            panel.SetShallows(WaterShallows.Defaults);
            await Frames(20);
            panel.SetShallows(WaterShallows.Read(draft));
            Image reloaded = await Still("clear-lagoon-reloaded", defaults);
            float back = Differ(lagoon, reloaded);
            Check(back < 0.002f, $"a saved and reloaded draft looks the same ({back:P2} differ)");
            // Preview off: the draft is gone from the renderer; on again with the defaults, the first picture.
            panel.WorldToggle.ButtonPressed = false;
            await Frames(30);
            Check(WaterRender.EditorShallows == null && WaterRender.EditorWorld == null, "preview off clears the draft from the renderer");
            await Still("preview-off", defaults);
            panel.SetShallows(WaterShallows.Defaults);
            panel.WorldToggle.ButtonPressed = true;
            Image restored = await Still("defaults-after-preview-off", defaults);
            float rest = Differ(defaults, restored);
            Check(rest < 0.002f, $"after preview off and on, the defaults look as before ({rest:P2} differ)");
            Say("Shallows and wakes: how far see-through water stays clear, how the seabed darkens and the trail of a boat, as a water v2 draft shown in the World view.");
            await Shot(3);
        }
        finally
        {
            panel.WorldToggle.ButtonPressed = false; panel.FreezeToggle.ButtonPressed = false;
            panel.SetShallows(WaterShallows.Defaults);
            panel.LoadStyle(previous); _world.SettingsTabs.CurrentTab = tab;
        }
    }

    // Share of pixels (every second one each way) whose colour differs by more than 6/255 in a channel.
    private static float Differ(Image a, Image b)
    {
        if (a == null || b == null || a.GetSize() != b.GetSize()) return 1f;
        int w = a.GetWidth(), h = a.GetHeight(), n = 0, changed = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                n++;
                if (MathF.Abs(p.R - q.R) > 0.024f || MathF.Abs(p.G - q.G) > 0.024f || MathF.Abs(p.B - q.B) > 0.024f) changed++;
            }
        return n == 0 ? 0f : (float)changed / n;
    }
}
#endif
