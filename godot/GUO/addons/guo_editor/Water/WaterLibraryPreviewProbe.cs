// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

public partial class WaterWorkbenchProbe
{
    private static void LibrarySearchFor(WaterPanel panel, string name)
    {
        panel.LibrarySearch.Text = name;
        panel.LibrarySearch.EmitSignal(LineEdit.SignalName.TextChanged, name);
    }

    private void CheckLibraryPreview(WaterPanel owner, string data)
    {
        var panel = new WaterPanel { DraftFolderOverride = Path.Combine(data, "audition-library") };
        owner.GetParent().AddChild(panel);
        try
        {
            Directory.CreateDirectory(panel.DraftFolderOverride);
            string path = Path.Combine(panel.DraftFolderOverride, "saved.json");
            var saved = WaterAuthoring.Presets[1] with { Name = "Saved audition" };
            WaterAuthoring.Save(saved, path);
            byte[] originalFile = File.ReadAllBytes(path);
            panel.LoadStyle(saved, path); panel.NumberInput("Wave strength").Value = .77;
            panel.SetReference(WaterAuthoring.Presets[6]); panel.CompareToggle.ButtonPressed = true;
            panel.Preview.SetScene(2); panel.Preview.SetBrightness(.2f); panel.Preview.Replay(0, .65f);
            float workingTime = panel.Preview.Seconds;
            byte[] working = WaterAuthoring.Serialize(panel.Style);
            byte[] reference = WaterAuthoring.Serialize(panel.Preview.ReferenceStyle);
            int history = panel.HistoryPosition;
            void Preserved(string reason) => Require(panel.DraftPath == path && panel.Dirty
                && panel.HistoryPosition == history && panel.Preview.Seconds == workingTime
                && panel.Preview.Paused && panel.Preview.Compare && panel.Preview.Scene == 2
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working)
                && WaterAuthoring.Serialize(panel.Preview.ReferenceStyle).AsSpan().SequenceEqual(reference), reason);
            panel.ShowLibrary();
            foreach (var style in WaterAuthoring.Presets)
            {
                LibrarySearchFor(panel, style.Name);
                Require(panel.LibraryPreviewStyle.Name == style.Name && panel.LibraryPreview.Paused
                    && panel.LibraryPreview.Seconds == 1.5f && panel.LibraryPreview.Scene == 1
                    && !panel.LibraryPreview.Compare && !panel.LibraryPreview.Classic
                    && panel.LibraryPreview.ReferenceStyle == null
                    && !ReferenceEquals(panel.LibraryPreviewStyle.Tint, style.Tint),
                    "each starter audition uses an isolated, cloned style and the same fixed beach pose");
            }
            Preserved("auditioning every starter preserves dirty document, reference, scene and working clock");
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(originalFile), "auditions never write the saved draft");
            LibrarySearchFor(panel, saved.Name);
            Require(panel.LibraryPreviewStyle.Wave == saved.Wave, "saved selection previews its disk values rather than working edits");
            var changed = saved with { Wave = .22f };
            WaterAuthoring.Save(changed, path);
            panel.LibraryTree.EmitSignal(Tree.SignalName.ItemSelected);
            Require(panel.LibraryPreviewStyle.Wave == .22f, "reselecting a saved row rereads changed disk content");
            File.WriteAllText(path, "{broken");
            panel.LibraryTree.EmitSignal(Tree.SignalName.ItemSelected);
            Require(panel.LibraryPreview == null && panel.LibraryPreviewStyle == null
                && panel.LibraryInfo.StartsWith("Could not preview this preset"),
                "corrupt selected files remove the stale picture and report local recovery");
            Preserved("failed audition preserves working and reference state");
            File.Delete(path); panel.LibraryTree.EmitSignal(Tree.SignalName.ItemSelected);
            Require(panel.LibraryPreview == null && !File.Exists(path), "missing selected files are not recreated by audition");
            WaterAuthoring.Save(changed, path); panel.LibraryTree.EmitSignal(Tree.SignalName.ItemSelected);
            Require(panel.LibraryPreviewStyle.Wave == .22f, "a corrected selected file can be previewed again");
            var preview = panel.LibraryPreview;
            LibrarySearchFor(panel, "no-such-water-preset");
            Require(panel.LibraryPreview == null && !preview.IsProcessing(), "empty results stop and release the previous preview");
            LibrarySearchFor(panel, saved.Name);
            preview = panel.LibraryPreview;
            panel.Hide();
            Require(panel.LibraryOpen && panel.LibraryPreview == null && !preview.IsProcessing(),
                "hiding the authoring panel stops audition rendering while retaining the browser selection");
            panel.Show();
            Require(panel.LibraryPreviewStyle.Wave == .22f && panel.LibraryPreview.Seconds == 1.5f,
                "returning to the panel rebuilds the selected fixed pose");
            Preserved("hiding and rebuilding auditions preserve the working draft and comparison");
            preview = panel.LibraryPreview; panel.CloseLibrary();
            Require(panel.LibraryPreview == null && !preview.IsProcessing(), "closing the browser stops and releases audition resources");
            panel.ShowLibrary(); panel.LibraryAction(true);
            Require(!panel.LibraryOpen && panel.LibraryPreview == null && panel.Preview.ReferenceStyle.Wave == .22f
                && panel.DraftPath == path && panel.HistoryPosition == history
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working),
                "explicit reference action closes the audition and keeps the working document");
            panel.ShowLibrary();
            panel.LibraryPreview.Shutdown(); panel.Shutdown();
            Require(panel.LibraryPreview == null && panel.LibraryPreviewStyle == null,
                "audition teardown tolerates an already disposed child preview");
        }
        finally { panel.Shutdown(); panel.Free(); }
    }

    private async Task CheckLibraryPreviewRendering(WaterPanel panel, string stills, string renderer)
    {
        bool frozen = panel.FreezeToggle.ButtonPressed;
        panel.FreezeToggle.ButtonPressed = true;
        byte[] working = WaterAuthoring.Serialize(panel.Style);
        float seconds = panel.Preview.Seconds;
        int history = panel.HistoryPosition;
        async Task Draw()
        { for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); }
        try
        {
            panel.ShowLibrary();
            panel.LibraryModel.Select(0); panel.LibraryModel.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            panel.LibrarySource.Select(1); panel.LibrarySource.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            LibrarySearchFor(panel, WaterAuthoring.Presets[1].Name); await Draw();
            using var pixel = panel.LibraryPreview.Capture();
            Require(pixel != null && pixel.GetWidth() >= 240 && pixel.GetHeight() >= 120,
                "selected preset preview renders at a usable native size");
            Require(pixel.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_audition-pixel-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
                "capture the selected Pixel preset's actual synthetic shader output");
            LibrarySearchFor(panel, WaterAuthoring.Presets[3].Name); await Draw();
            using var realistic = panel.LibraryPreview.Capture();
            Require(realistic.GetSize() == pixel.GetSize() && DifferentPixels(pixel, realistic, 0, pixel.GetWidth()) > 100,
                "selecting a different preset changes actual rendered pixels without loading a draft");
            Require(realistic.SavePng(Path.Combine(stills, $"guo_WATER-EDITOR_audition-realistic-{renderer}_still_{DateTime.UtcNow:yyyyMMdd-HHmm}.png")) == Error.Ok,
                "capture the selected Realistic preset's actual shader output");
            LibrarySearchFor(panel, WaterAuthoring.Presets[1].Name); await Draw();
            using var again = panel.LibraryPreview.Capture();
            Require(pixel.GetData().AsSpan().SequenceEqual(again.GetData()),
                "returning to a preset restores byte-identical audition pixels within this renderer");
            Require(panel.HistoryPosition == history && panel.Preview.Seconds == seconds
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(working),
                "rendered preset auditions preserve working history, settings and frozen time");
            panel.CloseLibrary(); await Draw();
            var textureBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
            for (int i = 0; i < 5; i++)
            {
                panel.ShowLibrary(); await Draw();
                panel.CloseLibrary(); await Draw();
            }
            var after = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
            Require(after <= textureBytes,
                $"five rendered audition open/close cycles retain no additional texture memory ({textureBytes} to {after} bytes)");
        }
        finally
        {
            LibrarySearchFor(panel, "");
            panel.LibrarySource.Select(0); panel.LibrarySource.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            panel.CloseLibrary(); panel.FreezeToggle.ButtonPressed = frozen;
        }
    }
}
#endif
