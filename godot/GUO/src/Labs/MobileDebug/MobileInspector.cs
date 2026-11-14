// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Configuration;

namespace GUO.Labs.MobileDebug;

internal partial class MobileInspector : CanvasLayer
{
    private static MobileInspector _instance;
    private World _world;
    private uint _serial;
    private ulong _drawFrame, _presentFrame;
    private int _pendingPages;
    private PacketSample _packet;
    private readonly Dictionary<int, LayerSample> _layers = new();
    internal readonly MobileHistory History = new();
    internal Window Panel;
    private OptionButton _mobiles;
    private Label _values, _status, _exportStatus;
    private HSlider _timeline;
    private MobileMarkers _markers;
    private double _refresh;
    private bool _updatingControls;
    internal static bool IsOpen => _instance != null;

    internal static void Open(World world, uint serial = 0)
    {
        if (_instance != null && IsInstanceValid(_instance))
        { if (serial != 0) _instance.Select(serial); _instance.Panel.Show(); return; }
        if (world?.Player == null) return;
        _instance = new MobileInspector { _world = world, _serial = serial == 0 ? world.Player.Serial : serial };
        Client.Game.AddChild(_instance);
    }

    // Called from the existing diagnostic seam; disabled Labs builds contain no calls.
    internal static void Observe(Mobile mobile, Item item, ushort animation, byte action,
        byte direction, byte frame, int count, string result)
    {
        var view = _instance;
        if (view == null || view._world != mobile.World || view._serial != mobile.Serial || view.History.Frozen) return;
        ulong now = Engine.GetProcessFrames();
        if (view._drawFrame != now) { view._layers.Clear(); view._drawFrame = now; }
        int layer = item == null ? 0 : (int)item.Layer;
        view._layers[layer] = new(layer, item?.Serial ?? 0, animation, action, direction, frame, count, result);
    }
    internal static void Movement(uint serial, ushort x, ushort y, sbyte z, byte direction)
    {
        if (_instance != null && _instance._serial == serial)
            _instance._packet = new(GUO.Time.Ticks, x, y, z, direction);
    }
    internal static void Presented(int pending)
    {
        if (_instance == null) return;
        _instance._presentFrame = Engine.GetProcessFrames();
        _instance._pendingPages = pending;
    }

    public override void _Ready()
    {
        Layer = 100;
        ProcessPriority = 100; // Sample after GameController Update/Draw, including suppressed draws.
        _markers = new MobileMarkers();
        AddChild(_markers);
        Panel = new Window { Visible = false, ForceNative = true,
            Title = "GUO Labs — Mobile frame inspector", Size = new(620, 620), MinSize = new(520, 400) };
        AddChild(Panel);
        Panel.CloseRequested += () => QueueFree();
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 12);
        Panel.AddChild(margin);
        var column = new VBoxContainer(); margin.AddChild(column);
        column.AddChild(new Label { Text = "MOBILE FRAME INSPECTOR  /  LABS", Modulate = new Color(0.45f, 0.85f, 1) });
        column.AddChild(new Label { Text = "Freeze stops recording values. The connected game keeps running." });
        _mobiles = new OptionButton(); column.AddChild(_mobiles);
        _mobiles.ItemSelected += index => Select((uint)_mobiles.GetItemMetadata((int)index).AsInt64());
        var controls = new HBoxContainer(); column.AddChild(controls);
        Button AddButton(string text, Action action)
        {
            var button = new Button { Text = text }; button.Pressed += action; controls.AddChild(button); return button;
        }
        AddButton("Freeze", () => { History.Freeze(); Refresh(); });
        AddButton("< Frame", () => { History.Seek(History.Index - 1); Refresh(); });
        AddButton("Frame >", () => { History.Seek(History.Index + 1); Refresh(); });
        AddButton("Live", () => { History.Live(); Refresh(); });
        AddButton("Export JSON", Export);
        _timeline = new HSlider { MinValue = 0, MaxValue = 0, Step = 1 };
        _timeline.ValueChanged += value => { if (!_updatingControls) { History.Seek((int)value); Refresh(); } };
        column.AddChild(_timeline);
        _status = new Label { Text = "Waiting for a mobile...", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_status);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _values = new Label { Text = "No captured frames.", MouseFilter = Control.MouseFilterEnum.Ignore };
        scroll.AddChild(_values);
        _exportStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_exportStatus);
        column.AddChild(new Label { Text = "Cyan cross: tile anchor   Yellow circle: interpolated feet\nOrange line: interpolation offset. Frozen markers are historical." });
        RefreshMobiles();
        Panel.Show();
    }

    private void Select(uint serial)
    {
        _serial = serial; History.Clear(); _layers.Clear(); _packet = null;
        _drawFrame = _presentFrame = 0; _markers.Sample = null; Refresh();
    }
    private void RefreshMobiles()
    {
        if (_mobiles.GetPopup().Visible) return;
        _mobiles.Clear();
        if (_world == null) { _mobiles.AddItem("Synthetic proof fixture"); return; }
        bool found = false;
        foreach (var mobile in _world.Mobiles.Values.OrderBy(m => m.Serial != _serial).ThenBy(m => m.Serial).Take(256))
        {
            int index = _mobiles.ItemCount;
            _mobiles.AddItem($"0x{mobile.Serial:X8}  {mobile.Name} {(mobile == _world.Player ? "(you)" : "")}");
            _mobiles.SetItemMetadata(index, (long)mobile.Serial);
            if (mobile.Serial == _serial) { _mobiles.Select(index); found = true; }
        }
        if (!found)
        {
            int index = _mobiles.ItemCount;
            _mobiles.AddItem($"0x{_serial:X8} (left world)");
            _mobiles.SetItemMetadata(index, (long)_serial); _mobiles.Select(index);
        }
    }
    public override void _Process(double delta)
    {
        if (_world == null) return;
        if (!_world.InGame) { QueueFree(); return; }
        _refresh += delta;
        if (_refresh >= 1) { _refresh = 0; RefreshMobiles(); }
        var mobile = _world.Mobiles.Get(_serial);
        if (History.Frozen) return;
        if (mobile == null) { _status.Text = "Selected mobile left the world. History retained; choose another mobile."; _markers.Sample = null; _markers.QueueRedraw(); return; }
        if (Client.Game.Scene is not GameScene scene) return;
        ulong now = Engine.GetProcessFrames();
        var tile = scene.Camera.WorldToScreen(new GUO.Compat.Point(mobile.RealScreenPosition.X + 22, mobile.RealScreenPosition.Y + 19), true);
        var render = scene.Camera.WorldToScreen(new GUO.Compat.Point(mobile.RealScreenPosition.X + 22 + (int)mobile.Offset.X,
            mobile.RealScreenPosition.Y + 19 + (int)(mobile.Offset.Y - mobile.Offset.Z)), true);
        float scale = Client.Game.DpiScale; // Includes ScreenScale; RenderTargets applies it once.
        History.Add(new(now, GUO.Time.Ticks, _serial, mobile.X, mobile.Y, mobile.Z,
            mobile.Offset.X, mobile.Offset.Y, mobile.Offset.Z, mobile.Steps.Count, (int)mobile.Direction,
            mobile.AnimIndex, _drawFrame == now, Client.Game.IsActive, ProfileManager.CurrentProfile.ReduceFPSWhenInactive,
            _presentFrame == now ? _pendingPages : -1, _packet,
            tile.X * scale, tile.Y * scale, render.X * scale, render.Y * scale,
            _drawFrame == now ? _layers.Values.OrderBy(s => s.Layer).ToArray() : Array.Empty<LayerSample>()));
        Refresh();
    }

    internal void Refresh()
    {
        _updatingControls = true;
        _timeline.MaxValue = Math.Max(0, History.Count - 1);
        _timeline.SetValueNoSignal(Math.Max(0, History.Index));
        _updatingControls = false;
        var frame = History.Current;
        _markers.Sample = frame; _markers.QueueRedraw();
        if (frame == null) { _values.Text = "No captured frames."; _status.Text = "Waiting for selected mobile..."; return; }
        _status.Text = $"{(History.Frozen ? "FROZEN — game still live" : "LIVE")}  |  {History.Index + 1}/{History.Count} frames  |  process {frame.ProcessFrame}  tick {frame.Tick}";
        var text = new StringBuilder();
        text.AppendLine($"Mobile 0x{frame.Serial:X8}    Position {frame.X}, {frame.Y}, {frame.Z}");
        text.AppendLine($"Offset {frame.OffsetX:0.00}, {frame.OffsetY:0.00}, {frame.OffsetZ:0.00}    Steps queued {frame.Steps}");
        text.AppendLine($"Direction {frame.Direction}    Animation index {frame.AnimIndex}");
        text.AppendLine($"Draw observed: {frame.Drawn}    Active: {frame.Active}    Idle reduction: {frame.IdleReduction}");
        text.AppendLine($"Atlas pending at presentation: {(frame.PendingPages < 0 ? "no presentation observed" : frame.PendingPages.ToString())}");
        if (frame.Packet is { } packet)
            text.AppendLine($"Last received movement: {packet.X}, {packet.Y}, {packet.Z} dir {packet.Direction}\nPacket age: {unchecked(frame.Tick - packet.Tick)} ms (arrival age, NOT network latency)");
        else text.AppendLine("No received movement update observed for this selection.");
        text.AppendLine("\nDRAW-SELECTED LAYERS  (submission is not proof of visible pixels)");
        foreach (var layer in frame.Layers)
            text.AppendLine($"L{layer.Layer,2}  0x{layer.Item:X8}  anim {layer.Animation} action {layer.Action} dir {layer.Direction}\n      frame {layer.Frame}/{layer.Count}  {layer.Result}");
        _values.Text = text.ToString().Replace("\r", "");
    }
    internal static void CaptureProof(string path)
    {
        if (_instance == null) throw new InvalidOperationException("Mobile inspector was not opened");
        if (_instance.History.Count == 0) throw new InvalidOperationException("Mobile inspector captured no frames");
        using var image = _instance.Panel.GetTexture().GetImage();
        if (image.SavePng(path) != Error.Ok) throw new IOException("Inspector capture failed");
        if (_instance.ExportHistory() == null) throw new IOException("Inspector history export failed");
    }
    private void Export() => ExportHistory();
    private string ExportHistory()
    {
        try
        {
            string root = System.Environment.GetEnvironmentVariable("UO_BUILD_DIR");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("Set an external UO_BUILD_DIR; run launchers/dev/mobile_debug_doctor.bat.");
            root = Path.GetFullPath(root);
            string checkout = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", ".."));
            string relative = Path.GetRelativePath(checkout, root);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidOperationException("UO_BUILD_DIR must be outside the checkout; run mobile_debug_doctor.");
            string directory = Path.Combine(root, "mobile_debug"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"mobile-{_serial:X8}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema = "guo/mobile-debug@1", frames = History.Snapshot() }, new JsonSerializerOptions { WriteIndented = true }));
            _exportStatus.Text = "Saved " + path;
            return path;
        }
        catch (Exception e) { _exportStatus.Text = "Export failed: " + e.Message; return null; }
    }
    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
        _layers.Clear(); History.Clear();
    }
}

internal partial class MobileMarkers : Node2D
{
    internal MobileSample Sample;
    public override void _Draw()
    {
        if (Sample is not { } sample) return;
        var tile = new Vector2(sample.TileScreenX, sample.TileScreenY);
        var render = new Vector2(sample.RenderScreenX, sample.RenderScreenY);
        DrawLine(tile - new Vector2(8, 0), tile + new Vector2(8, 0), Colors.Cyan, 2);
        DrawLine(tile - new Vector2(0, 8), tile + new Vector2(0, 8), Colors.Cyan, 2);
        DrawLine(tile, render, Colors.Orange, 2);
        DrawArc(render, 11, 0, Mathf.Tau, 24, Colors.Yellow, 2);
        DrawString(ThemeDB.FallbackFont, render + new Vector2(14, -14), $"0x{sample.Serial:X8}  f{sample.ProcessFrame}", fontSize: 14);
    }
}
#endif
