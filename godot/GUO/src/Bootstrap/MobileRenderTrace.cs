// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GUO.Game.GameObjects;
using System.Collections.Generic;

namespace GUO.Host;

/// <summary>Opt-in, bounded debug evidence for tools/mobile_sync. Never records packet payloads.</summary>
internal static class MobileRenderTrace
{
#if DEBUG
    private static StreamWriter _writer;
    private static bool _opened;
    private static int _rows;
    internal static int StaleFrames;
    internal static int PixelMismatches;
    private static int _pixelChecks;
    internal static uint PeerSerial;
    internal static Godot.Node CaptureHost;
    private static int _captures;
    private static readonly List<(Godot.Texture2D texture, Godot.Rect2I region, uint serial, uint item)> _pending = new();
#endif

    [Conditional("DEBUG")]
    internal static void Sprite(Mobile mobile, Item item, Godot.Texture2D texture, GUO.Compat.Rectangle uv)
    {
#if DEBUG
        if (_writer == null || mobile.Serial != PeerSerial || item == null || texture == null || _pixelChecks >= 256 || _pending.Count >= 8) return;
        if (GUO.Renderer.TextureAtlas.IsPending(texture))
            _pending.Add((texture, new Godot.Rect2I(uv.X, uv.Y, uv.Width, uv.Height), mobile.Serial, item.Serial));
#endif
    }

    [Conditional("DEBUG")]
    internal static void Frame(Mobile mobile, Item item, ushort animation, byte action,
        byte direction, byte frame, int count, string result)
    {
#if GUO_LABS
        GUO.Labs.MobileDebug.MobileInspector.Observe(mobile, item, animation, action, direction, frame, count, result);
#endif
#if DEBUG
        if (!_opened)
        {
            _opened = true;
            string path = Environment.GetEnvironmentVariable("GUO_MOBILE_TRACE");
            if (!string.IsNullOrEmpty(path))
            {
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("GUO_MOBILE_TRACE must be absolute");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                _writer = new StreamWriter(path, false, System.Text.Encoding.UTF8, 65536);
            }
        }
        if (_writer == null || !mobile.IsHuman || _rows >= 200000) return;
        _writer.WriteLine(JsonSerializer.Serialize(new {
            utc = DateTime.UtcNow.ToString("O"), tick = Time.Ticks,
            processFrame = Godot.Engine.GetProcessFrames(), serial = mobile.Serial,
            local = mobile == mobile.World.Player, x = mobile.X, y = mobile.Y, z = mobile.Z,
            ox = mobile.Offset.X, oy = mobile.Offset.Y, steps = mobile.Steps.Count,
            animIndex = mobile.AnimIndex, direction, action, animation, frame, count,
            layer = item == null ? 0 : (int)item.Layer, item = item?.Serial ?? 0,
            graphic = item?.Graphic ?? mobile.Graphic, result
        }));
        if (++_rows % 256 == 0) _writer.Flush();
#endif
    }

    [Conditional("DEBUG")]
    internal static void EmptyFrame(int animation, int action, int direction, int index, int number, int width, int height)
    {
#if DEBUG
        _writer?.WriteLine(JsonSerializer.Serialize(new { kind = "empty_source_frame", animation, action, direction, index, number, width, height }));
#endif
    }

    [Conditional("DEBUG")]
    internal static void Movement(uint serial, ushort x, ushort y, sbyte z, byte direction)
    {
#if GUO_LABS
        GUO.Labs.MobileDebug.MobileInspector.Movement(serial, x, y, z, direction);
#endif
#if DEBUG
        if (_writer == null) return;
        _writer.WriteLine(JsonSerializer.Serialize(new {
            kind = "movement", tick = Time.Ticks, processFrame = Godot.Engine.GetProcessFrames(),
            serial, x, y, z, direction
        }));
#endif
    }

    [Conditional("DEBUG")]
    internal static void CompleteFrame()
    {
#if GUO_LABS
        if (GUO.Labs.MobileDebug.MobileInspector.IsOpen)
            GUO.Labs.MobileDebug.MobileInspector.Presented(GUO.Renderer.TextureAtlas.PendingPages);
#endif
#if DEBUG
        if (_writer == null) return;
        int pending = GUO.Renderer.TextureAtlas.PendingPages;
        if (pending != 0) StaleFrames++;
        _writer.WriteLine(JsonSerializer.Serialize(new {
            kind = "present", processFrame = Godot.Engine.GetProcessFrames(), pending
        }));
        foreach (var sprite in _pending)
        {
            if (!GUO.Renderer.TextureAtlas.TryGetPage(sprite.texture, out var page, out _)) continue;
            using var cpu = page.GetRegion(sprite.region);
            using var gpuPage = sprite.texture.GetImage();
            using var gpu = gpuPage.GetRegion(sprite.region);
            byte[] expected = cpu.GetData(), actual = gpu.GetData();
            bool equal = expected.AsSpan().SequenceEqual(actual);
            if (!equal) PixelMismatches++;
            _pixelChecks++;
            _writer.WriteLine(JsonSerializer.Serialize(new {
                kind = "pixel_check", processFrame = Godot.Engine.GetProcessFrames(),
                sprite.serial, sprite.item, equal
            }));
        }
        if (_pending.Count > 0 && _captures < 32 && CaptureHost != null)
            Capture(Godot.Engine.GetProcessFrames());
        _pending.Clear();
#endif
    }

#if DEBUG
    private static async void Capture(ulong processFrame)
    {
        _captures++;
        try
        {
            await CaptureHost.ToSignal(Godot.RenderingServer.Singleton, Godot.RenderingServer.SignalName.FramePostDraw);
            using var image = CaptureHost.GetViewport().GetTexture().GetImage();
            string trace = Environment.GetEnvironmentVariable("GUO_MOBILE_TRACE");
            image.SavePng(Path.Combine(Path.GetDirectoryName(trace), $"{Path.GetFileNameWithoutExtension(trace)}-late-{processFrame}.png"));
        }
        catch (Exception e) { Godot.GD.PrintErr($"[mobile-sync] capture failed: {e.Message}"); }
    }
#endif

    [Conditional("DEBUG")]
    internal static void Close()
    {
#if DEBUG
        _writer?.Dispose();
        _writer = null;
#endif
    }
}
