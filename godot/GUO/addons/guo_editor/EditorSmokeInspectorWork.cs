#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Godot;

public partial class EditorSmoke
{
    private void MeasureInspectorPreviewWork()
    {
        var cases = new Dictionary<string, object>();
        _report["inspector_preview_work"] = cases;
        foreach (int zoom in new[] { 1, 2, 8 })
        foreach (string mode in new[] { "same_size", "variable_size", "variable_format", "variable_mipmaps", "paused", "hidden", "reparented" })
        {
            using var first = Image.CreateEmpty(4, 3, false, Image.Format.Rgba8);
            using var second = Image.CreateEmpty(mode == "variable_size" ? 6 : 4, mode == "variable_size" ? 2 : 3, false, Image.Format.Rgba8);
            first.Fill(Colors.Red); second.Fill(Colors.Blue);
            first.SetPixel(0, 0, Colors.Transparent); second.SetPixel(0, 0, Colors.White);
            if (mode == "variable_format") second.Convert(Image.Format.Rgb8);
            if (mode == "variable_mipmaps") second.GenerateMipmaps();
            var dock = new InspectorDock();
            var host = new VBoxContainer();
            AddChild(dock); AddChild(host);
            dock.SetProcess(false);
            try
            {
                var clip = new Inspection { Frames = new[] { first, second }, Fps = 8 };
                dock.ShowInspection(clip);
                T Field<T>(string name) => (T)typeof(InspectorDock).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dock)!;
                var option = Field<OptionButton>("_zoom");
                var slider = Field<HSlider>("_frame");
                var preview = Field<TextureRect>("_preview");
                var play = Field<Button>("_play");
                option.Selected = zoom - 1;
                option.EmitSignal(OptionButton.SignalName.ItemSelected, (long)(zoom - 1));
                if (mode == "paused") play.EmitSignal(Button.SignalName.Pressed);
                if (mode is "hidden" or "reparented")
                {
                    dock.MountContent(host);
                    dock.Visible = false;
                    host.Visible = mode == "reparented";
                }
                var textures = new HashSet<ulong> { dock.Texture.GetInstanceId() };
                Texture2D retained = dock.Texture;
                byte[] retainedPixels;
                using (Image snapshot = retained.GetImage()) retainedPixels = snapshot.GetData();
                long allocated = 0, ticks = 0;
                bool pixelsMatch = true, framesMatch = true;
                for (int i = 0; i < 12; i++)
                {
                    if (i == 6) first.SetPixel(1, 1, Colors.Green);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    dock._Process(0.126);
                    ticks += Stopwatch.GetTimestamp() - start;
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    int expectedFrame = mode == "paused" ? 0 : (i + 1) % 2;
                    framesMatch &= (int)slider.Value == expectedFrame;
                    textures.Add(dock.Texture.GetInstanceId());
                    using Image actual = dock.Texture.GetImage();
                    Image expected = clip.Frames[expectedFrame];
                    bool dimensions = actual.GetWidth() == expected.GetWidth() * zoom && actual.GetHeight() == expected.GetHeight() * zoom;
                    pixelsMatch &= dimensions;
                    if (dimensions)
                        for (int y = 0; y < actual.GetHeight(); y++)
                        for (int x = 0; x < actual.GetWidth(); x++)
                        {
                            Color pixel = expected.GetPixel(x / zoom, y / zoom);
                            if (mode == "paused" && x / zoom == 1 && y / zoom == 1) pixel = Colors.Red;
                            pixelsMatch &= actual.GetPixel(x, y) == pixel;
                        }
                }
                bool nearest = preview.TextureFilter == CanvasItem.TextureFilterEnum.Nearest;
                bool visible = preview.IsVisibleInTree();
                bool visibilityMatches = visible == (mode != "hidden");
                using Image retainedAfter = retained.GetImage();
                bool snapshotPreserved = retainedAfter.GetData().AsSpan().SequenceEqual(retainedPixels);
                bool burstPreserved = CheckInspectorBurst(dock, slider, clip, zoom);
                dock.ShowInspection(new Inspection());
                bool cleared = dock.Texture == null;
                dock.ShowInspection(Inspection.Still("Synthetic", "replacement", second, ""));
                using Image replacement = dock.Texture.GetImage();
                bool replaced = replacement.GetPixel(zoom, zoom) == Colors.Blue && first.GetWidth() == 4;
                bool ok = pixelsMatch && framesMatch && nearest && cleared && replaced && snapshotPreserved && visibilityMatches && burstPreserved;
                cases[$"{mode}_{zoom}x"] = new Dictionary<string, object>
                {
                    ["ok"] = ok, ["unique_texture_objects"] = textures.Count, ["ticks"] = 12,
                    ["process_allocated_bytes"] = allocated, ["process_ms"] = ticks * 1000.0 / Stopwatch.Frequency,
                    ["pixels_match"] = pixelsMatch, ["frames_match"] = framesMatch, ["nearest"] = nearest,
                    ["preview_visible"] = visible, ["cleared"] = cleared, ["replaced"] = replaced,
                    ["retained_texture_unchanged"] = snapshotPreserved,
                    ["visibility_matches"] = visibilityMatches,
                    ["burst_textures_preserved"] = burstPreserved,
                    ["on_render_thread"] = RenderingServer.IsOnRenderThread(),
                };
                if (!ok) _failures.Add($"Inspector preview {mode}/{zoom}: frame, pixel or replacement mismatch");
            }
            finally
            {
                dock.MountContent(dock);
                host.Free(); dock.Free();
            }
        }
    }

    private static bool CheckInspectorBurst(InspectorDock dock, HSlider slider, Inspection clip, int zoom)
    {
        var pending = new List<(Texture2D Texture, Image Expected)>();
        try
        {
            // No texture readback, ForceSync or draw boundary during the burst.
            for (int i = 0; i < 6; i++)
            {
                int frame = i % 2;
                clip.Frames[frame].SetPixel(2, 1, new Color(i / 5f, 0, 1));
                Image expected = (Image)clip.Frames[frame].Duplicate();
                if (zoom > 1) expected.Resize(expected.GetWidth() * zoom, expected.GetHeight() * zoom, Image.Interpolation.Nearest);
                slider.SetValueNoSignal(frame);
                slider.EmitSignal(Godot.Range.SignalName.ValueChanged, (double)frame);
                pending.Add((dock.Texture, expected));
            }

            bool matches = true;
            foreach (var (texture, expected) in pending)
            {
                using Image actual = texture.GetImage();
                bool dimensions = actual.GetWidth() == expected.GetWidth() && actual.GetHeight() == expected.GetHeight();
                matches &= dimensions;
                if (dimensions)
                    for (int y = 0; y < actual.GetHeight(); y++)
                    for (int x = 0; x < actual.GetWidth(); x++)
                        matches &= actual.GetPixel(x, y) == expected.GetPixel(x, y);
            }
            return matches;
        }
        finally
        {
            foreach (var entry in pending) entry.Expected.Dispose();
        }
    }
}
#endif
