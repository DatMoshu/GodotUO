// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned UO+ (non-parity), ADR-0042.
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Renderer;
using GUO.Renderer.Particles;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private AmbientParticles _ambientParticles;

    private void ResetAmbientParticles()
    {
        _ambientParticles?.Dispose();
        _ambientParticles = null;
    }

    private void DrawAmbientParticles(UltimaBatcher2D batcher)
    {
        var profile = ProfileManager.CurrentProfile;
        if (!AmbientParticles.Enabled(profile))
        {
            if (_ambientParticles != null) ResetAmbientParticles();
            return; // no tile walk, textures or batch submissions when disabled
        }
        if (!_world.InGame || _world.Player.IsDead) return;
        _ambientParticles ??= new AmbientParticles();
        _ambientParticles.Begin(profile, Time.Ticks / 1000.0);
        ref var statics = ref Client.Game.UO.FileManager.TileData.StaticData;
        foreach (Chunk chunk in _visibleChunks)
        {
            if (chunk == null || chunk.IsDestroyed) continue;
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                if (_ambientParticles.Full) return;
                Land land = null;
                bool foliage = false, covered = false;
                int objects = 0;
                GameObject first = chunk.Tiles[x, y];
                for (GameObject obj = first; obj != null; obj = obj.TNext)
                {
                    if (++objects > AmbientParticles.MaximumTileObjects)
                    {
                        covered = true;
                        objects = AmbientParticles.MaximumTileObjects;
                        break;
                    }
                    if (obj is Land ground) land = ground;
                }
                if (land != null)
                {
                    for (GameObject obj = first; obj != null && objects-- > 0; obj = obj.TNext)
                    {
                        if (obj is not (Static or Multi or Item) || obj.Graphic >= statics.Length) continue;
                        ref var data = ref statics[obj.Graphic];
                        foliage |= data.IsFoliage;
                        covered |= obj.Z >= land.AverageZ && (data.IsRoof || data.IsSurface && obj.Z >= land.AverageZ + 16);
                    }
                }
                var kind = land == null ? AmbientParticleKind.None : AmbientParticles.Classify(
                    land.OriginalGraphic, foliage, covered || !land.AllowedToDraw, land.TileData.IsWet);
                int tx = chunk.X * 8 + x, ty = chunk.Y * 8 + y;
                // Same projection and camera offset as native land. The active
                // world batch applies zoom and clips to WorldRenderTarget.
                int sx = (tx - ty) * 22 - _offset.X;
                int sy = (tx + ty) * 22 - ((land?.AverageZ ?? 0) << 2) - _offset.Y;
                Godot.Vector2 screen = Camera.ViewTransform * new Godot.Vector2(sx, sy);
                if (screen.X < -128 || screen.Y < -128 || screen.X > Camera.Bounds.Width + 128 || screen.Y > Camera.Bounds.Height + 128)
                    kind = AmbientParticleKind.None; // samples still count, invisible sprites do not spend emission cap
                if (!_ambientParticles.TrySample(tx, ty, _world.Map.Index, kind, out var particle)) continue;
                _ambientParticles.Draw(batcher, particle, sx, sy, MAX_LAYER_DEPTH - 2);
            }
        }
    }
}
