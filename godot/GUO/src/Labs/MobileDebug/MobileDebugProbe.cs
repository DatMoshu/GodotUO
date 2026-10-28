// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace GUO.Labs.MobileDebug;
public partial class MobileDebugProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            var layers = new[] { new LayerSample(0, 0, 400, 2, 3, 7, 10, "submit"),
                new LayerSample(22, 0x40000001, 987, 2, 3, 7, 10, "submit") };
            var sample = new MobileSample(100, 1000, 42, 1434, 1697, 0, 12, 6, 0, 2, 3, 7,
                true, true, false, 0, new PacketSample(950, 1435, 1698, 0, 131), 300, 300, 312, 306, layers);
            var history = new MobileHistory();
            history.Add(sample);
            layers[0] = layers[0] with { Result = "changed" };
            Require(history.Current.Layers[0].Result == "submit", "snapshot copies layer arrays");
            for (ulong i = 0; i < 305; i++) history.Add(sample with { ProcessFrame = i });
            Require(history.Count == 300 && history.Snapshot()[0].ProcessFrame == 5, "bounded oldest-first history");
            history.Seek(10); var frozen = history.Current;
            history.Add(sample);
            Require(history.Current == frozen && history.Count == 300, "freeze does not roll history");
            history.Seek(-99); Require(history.Index == 0, "oldest frame clamp");
            history.Seek(999); Require(history.Index == 299, "newest frame clamp");
            history.Live(); history.Add(sample); Require(!history.Frozen && history.Current.ProcessFrame == 100, "resume capture");
            var exported = JsonSerializer.Deserialize<MobileSample[]>(JsonSerializer.Serialize(history.Snapshot()));
            Require(exported.Length == 300 && exported[^1].Serial == 42 && exported[^1].Packet.Tick == 950,
                "JSON preserves actor/frame/packet values");
            history.Clear(); Require(history.Count == 0 && history.Current == null && !history.Frozen, "selection clear");
            var inspector = new MobileInspector(); AddChild(inspector);
            inspector.History.Add(sample with { Layers = new[] { new LayerSample(0, 0, 400, 2, 3, 7, 10, "submit"), new LayerSample(22, 0x40000001, 987, 2, 3, 7, 10, "submit") } });
            inspector.History.Freeze(); inspector.Refresh();
            for (int i = 0; i < 8; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            string path = System.Environment.GetEnvironmentVariable("GUO_MOBILE_DEBUG_PROOF");
            if (!string.IsNullOrEmpty(path))
            {
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Proof path must be absolute");
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = inspector.Panel.GetTexture().GetImage();
                if (image.SavePng(path) != Error.Ok) throw new IOException("Could not save inspector proof");
            }
            inspector.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("[mobile-debug] PASS history bounds/copies/freeze/seek/resume/clear and inspector lifecycle");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("[mobile-debug] FAIL " + e); GetTree().Quit(1); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
#endif
