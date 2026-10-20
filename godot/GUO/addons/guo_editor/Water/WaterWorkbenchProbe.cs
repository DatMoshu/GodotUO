// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;
using GUO.Store;

/// <summary>Water authoring contracts plus offscreen, real-renderer native UI captures. No install required.</summary>
public partial class WaterWorkbenchProbe : Node
{
    private int _checks;
    private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        { yield return child; foreach (Node next in Descendants(child)) yield return next; }
    }
    private static void Press(WaterPanel panel, string text)
        => Descendants(panel).OfType<Button>().Single(b => b.Text == text).EmitSignal(BaseButton.SignalName.Pressed);
    private void Require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _checks++; GD.Print("[water authoring] PASS " + name);
    }
    private static int DifferentPixels(Image a, Image b, int from, int to)
    {
        int count = 0;
        for (int y = 0; y < a.GetHeight(); y++)
            for (int x = from; x < to; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) count++;
        return count;
    }
    public override async void _Ready()
    {
        WaterPanel panel = null;
        WaterPreview large = null;
        try
        {
            GetWindow().Unfocusable = true;
            string output = System.Environment.GetEnvironmentVariable("GUO_WATER_WORKBENCH_OUT")
                ?? Path.Combine(EditorData.BuildDirectory, "water", "probe");
            Directory.CreateDirectory(output);
            string data = Path.Combine(output, "data", "water-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
            Directory.CreateDirectory(data);
            var viewport = new SubViewport { Size = new Vector2I(1100, 800),
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            AddChild(viewport);
            var back = new ColorRect { Color = new Color(.12f, .13f, .15f), Size = new Vector2(1100, 800) };
            viewport.AddChild(back);
            var split = new HBoxContainer { Position = new Vector2(16, 16), Size = new Vector2(1068, 768) };
            split.AddThemeConstantOverride("separation", 20); viewport.AddChild(split);
            panel = new WaterPanel { CustomMinimumSize = new Vector2(365, 0) }; split.AddChild(panel);
            panel.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            var previewColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            split.AddChild(previewColumn);
            previewColumn.AddChild(new Label { Text = "Water preview" });
            var caption = new Label { Text = "Original texels on the left • current style on the right", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            previewColumn.AddChild(caption);
            large = new WaterPreview { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            previewColumn.AddChild(large);
            previewColumn.AddChild(new Label { Text = "Synthetic surface • same water shader as the client\nNo client data or shard connection", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (StoreWater preset in WaterAuthoring.Presets)
            {
                byte[] raw = WaterAuthoring.Serialize(preset);
                Require(WaterAuthoring.Serialize(StoreWater.Parse(raw)).AsSpan().SequenceEqual(raw), preset.Name + " schema round trip");
                string draft = Path.Combine(data, preset.Model + "-" + Array.IndexOf(WaterAuthoring.Presets, preset) + ".json");
                WaterAuthoring.Save(preset, draft);
                Require(WaterAuthoring.Serialize(WaterAuthoring.Read(draft)).AsSpan().SequenceEqual(raw), preset.Name + " disk round trip");
                string zip = Path.Combine(data, "preset-" + Array.IndexOf(WaterAuthoring.Presets, preset) + ".zip");
                WaterAuthoring.Export(preset, zip, "water-test", "1.0.0", "Water authoring test", "CC0-1.0");
                string unpack = zip + "-unpacked"; Directory.CreateDirectory(unpack);
                StoreManifest manifest = StorePack.Extract(zip, unpack);
                Require(manifest.Components.Single().Type == "water" && manifest.Files.Count == 2, preset.Name + " validated data-only pack");
                Require(WaterAuthoring.Serialize(WaterAuthoring.Read(Path.Combine(unpack, "water.json"))).AsSpan().SequenceEqual(raw), preset.Name + " pack preserves style");
            }
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            float before = panel.Style.Wave;
            panel.NumberInput("Wave strength").Value = .47;
            Require(Math.Abs(panel.Style.Wave - .47f) < .001 && panel.Dirty, "numeric UI edits the draft");
            panel.Undo(); Require(panel.Style.Wave == before && !panel.Dirty, "undo restores loaded preset");
            panel.Redo(); Require(Math.Abs(panel.Style.Wave - .47f) < .001, "redo restores edit");
            panel.Undo(); panel.NumberInput("Glints").Value = 0;
            int history = panel.HistoryPosition;
            panel.Redo(); Require(panel.HistoryPosition == history && panel.Style.Glint == 0, "new edit discards redo branch");
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            int beforeReference = panel.HistoryPosition;
            Press(panel, "Keep reference");
            Require(panel.CompareToggle.ButtonPressed && panel.Preview.ReferenceStyle.Name == panel.Style.Name
                && !ReferenceEquals(panel.Preview.ReferenceStyle.Tint, panel.Style.Tint)
                && !panel.Dirty && panel.HistoryPosition == beforeReference, "keeping a reference snapshots settings without editing or saving the draft");
            byte[] referenceBytes = WaterAuthoring.Serialize(panel.Preview.ReferenceStyle);
            panel.NumberInput("Wave strength").Value = .82;
            panel.LoadStyle(WaterAuthoring.Presets[6]); panel.Undo(); panel.Redo();
            Require(WaterAuthoring.Serialize(panel.Preview.ReferenceStyle).AsSpan().SequenceEqual(referenceBytes),
                "reference survives edits, preset loads and undo independently");
            byte[] beforeClear = WaterAuthoring.Serialize(panel.Style);
            Press(panel, "Clear reference");
            Require(panel.Preview.ReferenceStyle == null && panel.CompareToggle.ButtonPressed
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(beforeClear), "clearing reference restores Classic comparison and preserves draft");
            panel.LoadStyle(WaterAuthoring.Presets[0]);
            Require(!panel.NumberInput("Glints").Editable && !panel.NumberInput("Wave scale").Editable
                && !panel.NumberInput("Palette levels").Editable && panel.NumberInput("Pixel size").Editable,
                "UO+ exposes only the numeric controls its shader uses");
            var modelInput = Descendants(panel).OfType<OptionButton>().Single(o => o.ItemCount == 5 && o.GetItemText(0) == "UO realtime");
            var nativeStyle = panel.Style;
            modelInput.Select(3); modelInput.EmitSignal(OptionButton.SignalName.ItemSelected, 3L);
            Require(panel.Style.Model == "realistic" && panel.NumberInput("Glints").Editable
                && !panel.NumberInput("Palette levels").Editable && panel.Style.Glint == nativeStyle.Glint,
                "switching models restores applicable controls and preserves their values");
            modelInput.Select(0); modelInput.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            Require(panel.Style.Model == "uo" && panel.NumberInput("Palette levels").Editable,
                "UO realtime enables palette authoring");
            panel.DraftFolderOverride = Path.Combine(data, "draft-library");
            Directory.CreateDirectory(panel.DraftFolderOverride);
            string draftA = Path.Combine(panel.DraftFolderOverride, "a.json"), draftB = Path.Combine(panel.DraftFolderOverride, "b.json");
            WaterAuthoring.Save(WaterAuthoring.Presets[0], draftA);
            WaterAuthoring.Save(WaterAuthoring.Presets[1], draftB);
            byte[] savedB = File.ReadAllBytes(draftB);
            panel.LoadStyle(WaterAuthoring.Read(draftA), draftA);
            panel.LoadStyle(WaterAuthoring.Read(draftB), draftB);
            panel.Undo();
            Require(panel.DraftPath == draftA && !panel.Dirty, "undo load restores draft identity and baseline");
            panel.NumberInput("Wave strength").Value = .43;
            Press(panel, "Save draft");
            Require(WaterAuthoring.Read(draftA).Wave == .43f && File.ReadAllBytes(draftB).AsSpan().SequenceEqual(savedB),
                "saving after undo cannot overwrite the other draft");
            panel.Duplicate(); Press(panel, "Save draft");
            string duplicatePath = panel.DraftPath;
            byte[] duplicateBytes = File.ReadAllBytes(duplicatePath);
            panel.Undo();
            Require(panel.DraftPath == draftA && !panel.Dirty, "undo duplicate restores original saved document");
            panel.NumberInput("Wave strength").Value = .61;
            Press(panel, "Save draft");
            Require(File.ReadAllBytes(duplicatePath).AsSpan().SequenceEqual(duplicateBytes), "saving original preserves saved duplicate");
            panel.Undo(); Require(panel.Dirty, "undo saved edit compares with the current saved baseline");
            panel.Redo(); Require(!panel.Dirty, "redo saved edit restores unmodified state");
            string malformed = Path.Combine(data, "malformed.json"), oversized = Path.Combine(data, "oversized.json");
            File.WriteAllText(malformed, "{broken"); File.WriteAllText(oversized, new string('x', 16385));
            byte[] beforeImport = WaterAuthoring.Serialize(panel.Style);
            foreach (string rejected in new[] { malformed, oversized })
            {
                panel.ImportPath(rejected);
                Require(panel.StatusText.StartsWith("Could not complete water action:")
                    && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(beforeImport), "invalid import reports feedback and preserves draft");
            }
            File.Copy(malformed, Path.Combine(panel.DraftFolderOverride, "broken.json"));
            panel.RefreshPresets();
            Require(panel.DraftCount == 3 && panel.StatusText.StartsWith("Skipped 1"), "corrupt library item does not hide valid drafts");
            var nameInput = Descendants(panel).OfType<LineEdit>().Single(e => e.PlaceholderText == "Preset name");
            nameInput.Text = ""; nameInput.EmitSignal(LineEdit.SignalName.TextSubmitted, "");
            Require(panel.StatusText.StartsWith("Could not complete water action:")
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(beforeImport), "blank name validation stays inside UI boundary");
            nameInput.Text = panel.Style.Name;
            var packId = Descendants(panel).OfType<LineEdit>().Single(e => e.Text == "my-water-style");
            packId.Text = "../bad"; Press(panel, "Export water pack…");
            Require(panel.StatusText.StartsWith("Could not complete water action:"), "invalid pack ID reports UI feedback");
            packId.Text = "my-water-style"; Press(panel, "Export water pack…");
            Require(panel.StatusText.Contains("Enter an author"), "missing author reports UI feedback");
            string forbidden = ProjectSettings.GlobalizePath("res://water-probe-forbidden.json");
            panel.LoadStyle(panel.Style, forbidden); Press(panel, "Save draft");
            Require(panel.StatusText.StartsWith("Could not complete water action:") && !File.Exists(forbidden),
                "forbidden destination reports UI feedback without writing");
            string oldWorkspace = GUO.Workspace.Workspace.RootOverride;
            string oldBuild = System.Environment.GetEnvironmentVariable("UO_BUILD_DIR");
            string libraryOverride = panel.DraftFolderOverride;
            try
            {
                string workspace = Path.Combine(data, "user-workspace");
                GUO.Workspace.Workspace.RootOverride = workspace; panel.DraftFolderOverride = null;
                panel.LoadStyle(WaterAuthoring.Presets[1]); Press(panel, "Save draft");
                string durable = panel.DraftPath;
                Require(Path.GetDirectoryName(durable) == Path.Combine(workspace, "editor", "water", "drafts"),
                    "saved drafts belong to the configured user workspace");
                System.Environment.SetEnvironmentVariable("UO_BUILD_DIR", Path.Combine(data, "new-build-output"));
                panel.RefreshPresets();
                Require(panel.DraftCount == 1 && File.Exists(durable), "changing build output preserves and discovers saved drafts");
                byte[] external = WaterAuthoring.Serialize(panel.Style with { Wave = .72f });
                File.WriteAllBytes(durable, external);
                panel.NumberInput("Wave strength").Value = .29;
                Press(panel, "Save draft");
                Require(panel.StatusText.Contains("changed or was removed") && panel.Dirty
                    && File.ReadAllBytes(durable).AsSpan().SequenceEqual(external),
                    "save refuses an externally changed draft and retains local edits");
                panel.Duplicate(); Press(panel, "Save draft");
                Require(panel.DraftPath != durable && WaterAuthoring.Read(panel.DraftPath).Wave == .29f
                    && File.ReadAllBytes(durable).AsSpan().SequenceEqual(external),
                    "duplicate resolves a save conflict without replacing external edits");
            }
            finally
            {
                GUO.Workspace.Workspace.RootOverride = oldWorkspace;
                System.Environment.SetEnvironmentVariable("UO_BUILD_DIR", oldBuild);
                panel.DraftFolderOverride = libraryOverride;
            }
            bool bad = false;
            try { WaterAuthoring.Serialize(panel.Style with { Speed = 5 }); } catch (InvalidDataException) { bad = true; }
            Require(bad, "out-of-range style refused");
            bad = false;
            try { WaterAuthoring.CheckDestination(ProjectSettings.GlobalizePath("res://water.json")); } catch (InvalidDataException) { bad = true; }
            Require(bad, "game resource destination refused");
            bad = false;
            try { WaterAuthoring.Export(panel.Style, Path.Combine(output, "bad.zip"), "../bad", "1.0.0", "Test", "CC0-1.0"); } catch (InvalidDataException) { bad = true; }
            Require(bad && !File.Exists(Path.Combine(output, "bad.zip")), "unsafe pack identity leaves no ZIP");
            string attributed = Path.Combine(data, "attributed.zip");
            WaterAuthoring.Export(panel.Style, attributed, "water-attributed", "1.0.0", "Test author", "CC-BY-4.0");
            string attributedDir = attributed + "-unpacked"; Directory.CreateDirectory(attributedDir);
            Require(StorePack.Extract(attributed, attributedDir).Files.ContainsKey("LICENSE.txt"), "attributed pack includes licence");

            if (DisplayServer.GetName() != "headless")
            {
                string stills = Path.Combine(output, "stills"); Directory.CreateDirectory(stills);
                string renderer = RenderingServer.GetCurrentRenderingMethod().ToString().Replace('_', '-');
                foreach (int preset in new[] { 0, 1, 5, 6, 8 })
                {
                    var style = WaterAuthoring.Presets[preset];
                    panel.LoadStyle(style); panel.FreezeToggle.ButtonPressed = true;
                    large.SetStyle(style); large.SetScene(1); large.Compare = true;
                    large.Paused = false; large.Splash();
                    for (int frame = 0; frame < 30; frame++) large.Advance(.05f);
                    large.Paused = true; large.Advance(0);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var image = viewport.GetTexture().GetImage();
                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
                    string name = style.Name.ToLowerInvariant().Replace('+', 'p').Replace(' ', '-');
                    Require(image.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_{name}-{renderer}_still_{stamp}.png")) == Error.Ok, "capture " + style.Name);
                }
                large.Compare = false; large.Advance(0);
                caption.Text = "Frozen styled water • compare is off";
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var frozenA = large.Capture();
                large.Advance(1);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var frozenB = large.Capture();
                Require(frozenA.GetData().AsSpan().SequenceEqual(frozenB.GetData()), "freeze preserves rendered bytes");
                Require(frozenB.GetPixel(frozenB.GetWidth() - 4, frozenB.GetHeight() - 4).R < .4f,
                    "preview fills its resized viewport");
                var reference = WaterAuthoring.Presets[1];
                large.SetStyle(reference); large.SetScene(0);
                large.Compare = false; large.Paused = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var frozenBeforePin = large.Capture();
                large.SetReference(reference); large.Compare = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var frozenAfterPin = large.Capture();
                int middle = frozenAfterPin.GetWidth() / 2;
                Require(DifferentPixels(frozenBeforePin, frozenAfterPin, 0, middle - 2) == 0,
                    "keeping a reference while frozen preserves the current wave phase");
                large.Compare = false; large.Paused = false; large.Boat = true; large.Splash();
                for (int i = 0; i < 30; i++) large.Advance(.05f);
                large.Paused = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var singleStyle = large.Capture();
                large.Compare = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var matched = large.Capture();
                Require(DifferentPixels(singleStyle, matched, 0, middle - 2) == 0
                    && DifferentPixels(singleStyle, matched, middle + 2, matched.GetWidth()) == 0,
                    "identical reference and draft retain pixel-identical synchronized splash and hull fields");
                large.SetStyle(reference with { Tint = new[] { .65f, .08f, .12f }, Wave = .85f });
                large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var compared = large.Capture();
                Require(DifferentPixels(matched, compared, 0, middle - 2) == 0
                    && DifferentPixels(matched, compared, middle + 2, matched.GetWidth()) > 100,
                    "editing draft changes right side while pinned left side stays byte-identical");
                panel.LoadStyle(reference); Press(panel, "Keep reference");
                panel.Change(reference with { Tint = new[] { .65f, .08f, .12f }, Wave = .85f });
                panel.ScrollVertical = 0;
                caption.Text = "Left: Pixel water reference\nRight: edited draft; identical time, lighting and stimuli";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var comparisonShot = viewport.GetTexture().GetImage();
                Require(comparisonShot.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_reference-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
                    "capture reference comparison controls and synchronized split");
                large.Classic = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var classicWithReference = large.Capture();
                large.SetReference(null); large.Compare = false; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var classicWithoutReference = large.Capture();
                Require(classicWithReference.GetData().AsSpan().SequenceEqual(classicWithoutReference.GetData()),
                    "Classic bypasses both pinned and draft styles");
                var stillReference = reference with { Speed = 0, RippleStrength = 0 };
                large.Classic = false; large.Boat = false; large.Paused = false; large.Compare = true;
                large.SetStyle(stillReference with { RippleStrength = 1 }); large.SetReference(stillReference);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var beforeSplash = large.Capture();
                large.Splash();
                for (int i = 0; i < 30; i++) large.Advance(.05f);
                large.Paused = true; large.Advance(0);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var afterSplash = large.Capture();
                Require(DifferentPixels(beforeSplash, afterSplash, 0, middle - 2) == 0
                    && DifferentPixels(beforeSplash, afterSplash, middle + 2, afterSplash.GetWidth()) > 100,
                    "reference and draft apply independent ripple strength to the same splash input");
                large.SetReference(null); large.Compare = false;
                Press(panel, "Clear reference");
                large.Classic = false; large.Advance(0);
                caption.Text = "Frozen styled water • compare is off";
                panel.ScrollVertical = 10000;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var footer = viewport.GetTexture().GetImage();
                string footerStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
                Require(footer.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_save-share-{renderer}_still_{footerStamp}.png")) == Error.Ok, "capture save and share controls");
            }
            GD.Print($"[water authoring] PASS: {_checks} checks");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[water authoring] FAIL: " + ex);
            GetTree().Quit(1);
        }
        finally { panel?.Shutdown(); large?.Shutdown(); }
    }
}
#endif
