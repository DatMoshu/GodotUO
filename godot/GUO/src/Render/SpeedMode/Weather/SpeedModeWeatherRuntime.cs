// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Configuration;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>
/// Same-frame world overlays, before the separately composed UI. Arm after
/// classic weather updates; core publication elects speed output or classic
/// lines. Native canvas commands avoid a one-frame Node redraw delay.
/// </summary>
public partial class SpeedModeWeatherRuntime : Node
{
    private readonly SpeedModeRainPass _rain = new();
    private readonly SpeedModeWetPass _wet = new();
    private readonly SpeedModeFogPass _fog = new();
    private readonly Rid[] _items = new Rid[3];
    internal RenderTarget2D Target { get; private set; }
    private World _world;
    private Profile _profile;
    private SpeedModeWeather _state;
    private UltimaBatcher2D _batcher;
    private UltimaBatcher2D.WeatherLines _classic;
    private Texture2D _expectedSurface, _color;
    private Vector3 _camera;
    private float _artScale;
    private double _seconds;
    private uint _tick;
    private bool _armed, _released;
    internal bool Bound { get; private set; }
    internal long BoundSequence { get; private set; }

    internal void Initialize(RenderTarget2D target)
    {
        Target = target;
        AddChild(_rain); AddChild(_wet); AddChild(_fog);
        _rain.Visible = _wet.Visible = _fog.Visible = false;
        for (int i = 0; i < _items.Length; i++)
        {
            _items[i] = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_items[i], target.CanvasItem);
            RenderingServer.CanvasItemSetZIndex(_items[i], 100 + i);
            RenderingServer.CanvasItemSetDefaultTextureFilter(_items[i], RenderingServer.CanvasItemTextureFilter.Nearest);
            RenderingServer.CanvasItemSetVisible(_items[i], false);
        }
        SpeedModeBindings.LightReady += OnLightReady;
        SpeedModeBindings.Invalidated += OnInvalidated;
    }

    internal void ResetFrame()
    {
        // A scene-retirement callback may outlive this child native owner.
        if (_released) return;
        ClearOutputs();
        _armed = false;
        _world = null; _profile = null; _state = null;
        _expectedSurface = _color = null;
        _classic = default;
    }

    internal void Arm(World world, Profile profile, SpeedModeWeather state, UltimaBatcher2D batcher,
        UltimaBatcher2D.WeatherLines classic, Texture2D surface, Texture2D color,
        Transform2D view, Vector3 camera, float artScale, double seconds)
    {
        _world = world; _profile = profile; _state = state; _batcher = batcher;
        _classic = classic; _expectedSurface = surface; _color = color;
        _camera = camera; _artScale = artScale; _seconds = seconds; _tick = Time.Ticks;
        _armed = true;
        var inverse = view.AffineInverse();
        foreach (var item in _items) RenderingServer.CanvasItemSetTransform(item, inverse);
        // Also supports a producer that published synchronously before Arm.
        if (SpeedModeBindings.Current.LightTarget != null) OnLightReady(SpeedModeBindings.Current);
    }

    private void OnLightReady(SpeedModeBindings.Frame frame)
    {
        if (!_armed || _released) return;
        try
        {
            var size = new Vector2I(Target.Width, Target.Height);
            Bound = SpeedModeWeatherTargets.BindCurrent(_world, _profile, _state, _expectedSurface,
                frame.Sequence, _tick, _color, size, _camera, _artScale, _seconds, _rain, _wet, _fog, _batcher);
            BoundSequence = Bound ? frame.Sequence : 0;
            if (!Bound) { ResetFrame(); return; }
            bool replaceLines = Bound && _rain.Active;
            DrawPass(0, _wet, _wet.Visible, size);
            DrawPass(1, _rain, _rain.Visible, size);
            DrawPass(2, _fog, _fog.Visible, size);
            // The Nodes own materials only; native items carry the three draws.
            _rain.Visible = _wet.Visible = _fog.Visible = false;
            _batcher.ShowSpeedWeatherLines(_classic, !replaceLines);
        }
        catch (Exception error)
        {
            ClearOutputs();
            GD.PrintErr("[speed-weather] frame rejected: " + error.Message);
        }
    }

    private void DrawPass(int index, ColorRect pass, bool visible, Vector2I size)
    {
        var item = _items[index];
        RenderingServer.CanvasItemClear(item);
        RenderingServer.CanvasItemSetVisible(item, visible);
        if (!visible || pass.Material == null) return;
        RenderingServer.CanvasItemSetMaterial(item, pass.Material.GetRid());
        RenderingServer.CanvasItemAddRect(item, new Rect2(Vector2.Zero, size), Colors.White);
    }

    private void OnInvalidated(SpeedModeBindings.Frame frame)
    {
        if (_armed && ReferenceEquals(frame.Surface, _expectedSurface)) ResetFrame();
    }

    private void ClearOutputs()
    {
        Bound = false; BoundSequence = 0;
        SpeedModeWeatherTargets.Clear(_rain, _wet, _fog, _batcher);
        foreach (var item in _items)
            if (item.IsValid) { RenderingServer.CanvasItemClear(item); RenderingServer.CanvasItemSetVisible(item, false); }
        _batcher?.ShowSpeedWeatherLines(_classic, true);
    }

    internal void Release()
    {
        if (_released) return;
        _released = true;
        SpeedModeBindings.LightReady -= OnLightReady;
        SpeedModeBindings.Invalidated -= OnInvalidated;
        ClearOutputs();
        for (int i = 0; i < _items.Length; i++)
        {
            if (_items[i].IsValid) RenderingServer.FreeRid(_items[i]);
            _items[i] = default;
        }
        _armed = false; _classic = default; _batcher = null;
        _world = null; _profile = null; _state = null; _expectedSurface = _color = null;
    }

    public override void _ExitTree() => Release();
}
