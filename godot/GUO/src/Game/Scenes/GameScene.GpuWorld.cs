// SPDX-License-Identifier: BSD-2-Clause

using System;
using Godot;
using GUO.Configuration;
using GUO.Renderer;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// PORT DEVIATION (GUO): --gpu-world (ADR-0007, GPU-01..04). The scene's
    /// side of the world depth pass: the pass under the world target, the
    /// frame's key origins from the tile walk's viewport, and the reasons a
    /// frame stays on the canvas. Nothing here runs unless the switch is on.
    /// </summary>
    internal partial class GameScene
    {
        private Transform2D _gpuView;
        private Vector2I _gpuSize;
        private bool _gpuMirroring;

        /// <summary>
        /// Before FillGameObjectList: the pass exists as a child of the world
        /// target's viewport, so it renders before the target samples it, and
        /// is the target's size. Off, a leftover pass is released.
        /// </summary>
        private void PrepareGpuWorld(UltimaBatcher2D batcher, in Transform2D matrix, RenderTargets renderTargets)
        {
            if (!GpuWorld.Enabled)
            {
                if (GpuWorld.Pass != null)
                {
                    GpuWorld.Destroy();
                }

                return;
            }

            RenderTarget2D target = renderTargets.WorldRenderTarget;
            _gpuView = matrix;
            _gpuSize = new Vector2I(target.Width, target.Height);
            _gpuMirroring = batcher.Mirroring;

            if (GpuWorld.Pass != null && !ReferenceEquals(GpuWorld.Pass.Viewport.GetParent(), target.ViewportNode))
            {
                GpuWorld.Destroy(); // the target was recreated (a resize)
            }

            if (GpuWorld.Pass == null)
            {
                GpuWorld.Create(target.ViewportNode, _gpuSize);
            }
        }

        /// <summary>
        /// After GetViewPort: the frame's origins come from the tile walk's
        /// own cull, so the key covers exactly what the walk can queue, or
        /// the frame is abandoned with the reason.
        /// </summary>
        private void BeginGpuFrame()
        {
            WorldDepthPass pass = GpuWorld.Pass;
            if (pass == null || !GpuWorld.Enabled)
            {
                return;
            }

            int offX = _offset.X;
            int offY = _offset.Y;

            // The walk culls on RealScreenPosition, which is the tile's world
            // pixel ((x - y) * 22 - 22, (x + y) * 22 - 4z - 22) less the draw
            // offset; the shader culls the same tile pixel against this.
            var cull = new Vector4(_minPixel.X + offX, _minPixel.Y + offY, _maxPixel.X + offX, _maxPixel.Y + offY);

            // The first diagonal the top edge admits: s * 22 - 4z - 22 - offY >= minY
            // with z >= -128. The last one the bottom edge admits, z <= 127.
            int firstDiagonal = Math.Max(0, (int)Math.Floor((_minPixel.Y + offY + 22 - 512) / 22.0));
            int lastDiagonal = (int)Math.Ceiling((_maxPixel.Y + offY + 22 + 512) / 22.0) + 1;

            // The smallest x - y the left edge admits: diff * 22 - 22 - offX >= minX; less one.
            int minDiff = (int)Math.Floor((_minPixel.X + offX + 22) / 22.0) - 1;

            pass.Begin(new WorldDepthPass.Frame
            {
                View = _gpuView,
                LandView = _gpuView,
                Size = _gpuSize,
                DepthOrigin = WorldDepthKey.DepthOrigin(firstDiagonal),
                MinDiff = minDiff,
                Cull = cull,
                DrawOffset = new Vector2(offX, offY),
            });

            string why = null;
            if (_gpuMirroring)
            {
                why = "the id mirror is on";
            }
            else if (ProfileManager.CurrentProfile.EnhancedHD)
            {
                why = "Enhanced HD art is on";
            }
            else if (firstDiagonal < WorldDepthKey.FirstExactDiagonal)
            {
                why = "the void corner is in view";
            }
            else if ((lastDiagonal - firstDiagonal) * 100 + 700 > WorldDepthKey.DepthRange)
            {
                why = "the view spans more diagonals than the depth field";
            }

            if (why != null)
            {
                pass.Abandon(why);
            }
        }
    }
}
