// SPDX-License-Identifier: BSD-2-Clause
using GUO.Renderer.SpeedMode.PhysicalWorld;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private readonly NativePhysicalWorldCache _speedPhysicalCache = new();
    internal long SpeedPhysicalCacheHits => _speedPhysicalCache.CacheHits;
    internal long SpeedPhysicalCacheBuilds => _speedPhysicalCache.CacheBuilds;
    internal double SpeedPhysicalLastBuildMilliseconds => _speedPhysicalCache.LastBuildMilliseconds;
    // PORT DEVIATION (GUO): optional enhanced-lighting physical metadata. The caller
    // owns frame admission and keeps this immutable snapshot until its ticket retires.
    // No visibility, cutaway or _maxZ filter belongs in physical source enumeration.
    internal PhysicalWorldSnapshot CaptureSpeedPhysicalWorld(ulong worldIdentity, ulong generation, PhysicalRect bounds)
        => _speedPhysicalCache.Capture(_world,
            new PhysicalSnapshotKey(worldIdentity, generation, _world.Map.Index), bounds);
}
