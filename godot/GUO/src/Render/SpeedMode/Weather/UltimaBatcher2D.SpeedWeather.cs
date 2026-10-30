// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    internal readonly record struct WeatherLines(Rid Item, long Generation);
    private Rid _speedWeatherLines;
    private long _speedWeatherGeneration;
    internal RenderTarget2D SpeedWeatherTarget => _currentTarget;
    internal Transform2D SpeedWeatherInverseView => _viewTransform.AffineInverse();
    internal Texture2D SpeedWeatherSurface => _orderedWorld?.SurfaceTexture;
    internal Texture2D SpeedWeatherColor => _orderedWorld?.Texture;

    // Record classic lines first. The same-frame light callback elects their
    // visibility only after the speed pass succeeds; simulation always ran.
    internal WeatherLines BeginSpeedWeatherLines()
    {
        FlushRun();
        _speedWeatherGeneration++;
        _speedWeatherLines = NewItem(CurrentParent, Transform2D.Identity);
        _clipStack.Add(_speedWeatherLines);
        Cut();
        return new WeatherLines(_speedWeatherLines, _speedWeatherGeneration);
    }

    internal void EndSpeedWeatherLines()
    {
        FlushRun();
        _clipStack.RemoveAt(_clipStack.Count - 1);
        Cut();
    }

    internal void ShowSpeedWeatherLines(WeatherLines ticket, bool visible)
    {
        // A delayed invalidation must never modify an item reused by the pool.
        if (ticket.Item.IsValid && ticket.Item == _speedWeatherLines
            && ticket.Generation == _speedWeatherGeneration)
            RenderingServer.CanvasItemSetVisible(ticket.Item, visible);
    }

    internal void RetireSpeedWeatherLines() => ResetSpeedWeatherLines();

    private void ResetSpeedWeatherLines()
    {
        if (_speedWeatherLines.IsValid) RenderingServer.CanvasItemSetVisible(_speedWeatherLines, true);
        _speedWeatherLines = default;
        _speedWeatherGeneration++;
    }
}
