// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class WaterWorkbenchProbe
{
    private void CheckPreviewFiles(WaterPanel panel, string data)
    {
        using var image = Image.CreateEmpty(5, 3, false, Image.Format.Rgba8);
        image.Fill(new Color(.2f, .4f, .6f, .5f)); image.SetPixel(1, 2, Colors.Red);
        string png = Path.Combine(data, "cpu-preview.png");
        WaterAuthoring.SavePreview(image, png);
        using var decoded = Image.LoadFromFile(png);
        Require(decoded.GetWidth() == 5 && decoded.GetHeight() == 3
            && decoded.GetData().AsSpan().SequenceEqual(image.GetData()), "PNG round trip preserves original resolution, texels and alpha");
        byte[] saved = File.ReadAllBytes(png);
        try { WaterAuthoring.SavePreview(null, png); Require(false, "null capture must fail"); }
        catch (InvalidDataException) { Require(File.ReadAllBytes(png).AsSpan().SequenceEqual(saved), "missing capture preserves existing PNG bytes"); }
        using var empty = new Image();
        try { WaterAuthoring.SavePreview(empty, png); Require(false, "empty capture must fail"); }
        catch (InvalidDataException) { Require(File.ReadAllBytes(png).AsSpan().SequenceEqual(saved), "empty capture preserves existing PNG bytes"); }
        string draft = Path.Combine(data, "png-guard.json"); File.WriteAllText(draft, "keep draft");
        Require(!panel.SavePreviewPath(draft) && File.ReadAllText(draft) == "keep draft"
            && panel.PreviewExportStatus.Contains(".png"), "preview export refuses other file types with local feedback and preserves their bytes");
        Require(!panel.SavePreviewPath(ProjectSettings.GlobalizePath("res://water-preview-test.png"))
            && panel.PreviewExportStatus.Contains("outside"), "preview export refuses the Godot project before capture or writing");
        string install = EditorData.Setting("UO_CLIENT_DATA", "");
        if (!string.IsNullOrWhiteSpace(install))
            Require(!panel.SavePreviewPath(Path.Combine(install, "water-preview-test.png"))
                && panel.PreviewExportStatus.Contains("outside"), "preview export refuses the client install before capture or writing");
        string directory = Path.Combine(data, "directory.png"); Directory.CreateDirectory(directory);
        try { WaterAuthoring.SavePreview(image, directory); Require(false, "directory replacement must fail"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Require(Directory.Exists(directory) && !Directory.EnumerateFiles(data, "*.pending-*").Any(), "failed replacement preserves the destination and removes temporary PNG files"); }
        if (DisplayServer.GetName() == "headless")
            Require(!panel.SavePreviewPath(png) && panel.PreviewExportStatus.Contains("renderer")
                && File.ReadAllBytes(png).AsSpan().SequenceEqual(saved)
                && Descendants(panel).OfType<Button>().Single(b => b.Text == "Save preview PNG…").Disabled,
                "headless export explains renderer recovery and preserves the existing image");
    }

    private async Task CheckPreviewExport(WaterPanel panel, string data)
    {
        panel.LoadStyle(WaterAuthoring.Presets[1]); panel.KeepReference();
        panel.Change(panel.Style with { Wave = .7f, Tint = new[] { .5f, .1f, .2f } });
        panel.ReplayTest.Select(2); panel.ReplayTime.Value = 1.5; panel.ReplayPreview();
        byte[] draft = WaterAuthoring.Serialize(panel.Style), reference = WaterAuthoring.Serialize(panel.Preview.ReferenceStyle);
        int history = panel.HistoryPosition; string path = panel.DraftPath;
        float seconds = panel.Preview.Seconds;
        foreach (bool classic in new[] { false, true })
        {
            panel.ClassicToggle.ButtonPressed = classic; panel.Preview.Advance(0);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var before = panel.Preview.Capture();
            string png = Path.Combine(data, classic ? "classic-preview.png" : "compared-preview.png");
            File.WriteAllText(png, "previous image");
            Require(panel.SavePreviewPath(png), "save a rendered " + (classic ? "Classic" : "split comparison") + " preview over an existing destination");
            using var loaded = Image.LoadFromFile(png);
            before.Convert(Image.Format.Rgba8); loaded.Convert(Image.Format.Rgba8);
            Require(loaded.GetWidth() == before.GetWidth() && loaded.GetHeight() == before.GetHeight()
                && loaded.GetData().AsSpan().SequenceEqual(before.GetData()), "exported PNG matches the most recently drawn synthetic pixels exactly");
        }
        Require(panel.HistoryPosition == history && panel.DraftPath == path && panel.Dirty
            && panel.Preview.Paused && panel.FreezeToggle.ButtonPressed && panel.Preview.Seconds == seconds
            && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(draft)
            && WaterAuthoring.Serialize(panel.Preview.ReferenceStyle).AsSpan().SequenceEqual(reference),
            "PNG export preserves edited draft, reference, identity, history and frozen simulation pose");
        panel.ClassicToggle.ButtonPressed = false; panel.ClearReference();
    }
}
#endif
