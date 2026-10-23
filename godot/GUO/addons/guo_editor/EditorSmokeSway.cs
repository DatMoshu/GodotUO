#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Editor;

public partial class EditorSmoke
{
    public const string SwayProofFlag = "--guo-editor-sway-proof";
    private int _swayProofFrames, _swayProofAt, _swayProofMode;
    private readonly List<double> _swayOffCosts = new(), _swayOnCosts = new();

    private void StartSwayProof()
    {
        _swayProofFrames = int.Parse(ArgValue(SwayProofFlag));
        _swayProofAt = _swayProofMode = 0;
        _world.Host.MeasureDrawCost = true;
        // This is the editor's transient profile; never save player settings.
        UoPlusOptions.DisableAll(ProfileManager.CurrentProfile);
        ProfileManager.CurrentProfile.RealtimeLights = false;
        Directory.CreateDirectory(Path.Combine(_out, "sway-off"));
        Directory.CreateDirectory(Path.Combine(_out, "sway-on"));
        var catalogue = new List<object>();
        for (int id = 0; id < _data.Files.TileData.StaticData.Length; id++)
        {
            byte kind = GUO.Renderer.Sway.SwayRender.Classify(id);
            if (kind == 0) continue;
            ref var tile = ref _data.Files.TileData.StaticData[id];
            catalogue.Add(new { id = $"0x{id:X4}", tile.Name, kind, tile.IsWall, tile.IsRoof, tile.IsWet });
        }
        File.WriteAllText(Path.Combine(_out, "sway-catalogue-audit.json"), JsonSerializer.Serialize(catalogue));
    }

    private void StepSwayProof()
    {
        // Warm caches for each mode before recording CPU submission time.
        if (_swayProofAt < 90) { _swayProofAt++; return; }
        bool on = _swayProofMode == 1;
        int frame = _swayProofAt - 90;
        if (frame < 120)
        {
            (on ? _swayOnCosts : _swayOffCosts).Add(_world.Host.DrawCostMs);
        }
        else if (frame < 120 + _swayProofFrames)
        {
            int video = frame - 120;
            _world.Host.CaptureTicks = (uint)(video * 1000 / 30);
            using var image = _world.Capture();
            if (image == null || image.IsEmpty()) { _failures.Add("sway: no rendered frame"); Finish(); return; }
            string name = Path.Combine(_out, on ? "sway-on" : "sway-off", $"{video:D4}.png");
            image.SavePng(name);
        }
        _swayProofAt++;
        if (frame < 120 + _swayProofFrames) return;
        if (!on)
        {
            _swayProofMode = 1;
            _swayProofAt = 0;
            _world.Host.CaptureTicks = null;
            UoPlusOptions.Set(ProfileManager.CurrentProfile, UoPlusFeature.Sway, true, 50);
            return;
        }
        _world.Host.CaptureTicks = null;
        var offCpuSamples = _swayOffCosts.ToArray();
        var onCpuSamples = _swayOnCosts.ToArray();
        double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
        double off = Median(_swayOffCosts), enabled = Median(_swayOnCosts);
        var result = new { offCpuMs = off, onCpuMs = enabled, deltaCpuMs = enabled - off,
            samples = _swayOnCosts.Count, offCpuSamples, onCpuSamples, frames = _swayProofFrames, density = 50,
            note = "Editor World view CPU draw submission; screenshot IO excluded; shared-machine measurement, GPU cost not isolated" };
        File.WriteAllText(Path.Combine(_out, "sway-cost.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[sway-cost] editor CPU off={off:F3} ms on={enabled:F3} ms delta={enabled-off:F3} ms samples={_swayOnCosts.Count}");
        SaveWorldShot();
        Finish();
    }
}
#endif
