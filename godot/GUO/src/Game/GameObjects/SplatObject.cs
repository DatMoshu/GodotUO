// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Compat;
using GUO.Renderer;

namespace GUO.Game.GameObjects
{
    /// <summary>
    /// A staged gaussian splat (ComfyUI multi) as a real world object: it joins
    /// the render lists at its <see cref="GameObject.CalculateDepthZ"/>, so roofs,
    /// walls and mobiles occlude it and it occludes them, and clicks pick it.
    /// Never in a chunk or a world collection: GameScene.Splats owns the
    /// instances and queues them every frame. Nothing upstream knows this type.
    /// </summary>
    internal sealed class SplatObject : GameObject
    {
        public SplatPlacement Placement;
        public readonly ArrayMesh Mesh = new();

        /// <summary>Tile-space click rect from the lod0 bounds, set at queue time.</summary>
        public Rectangle PickRect;

        /// <summary>Camera zoom at queue time.</summary>
        public float DrawZoom = 1f;

        /// <summary>
        /// At the camera's closest zoom the splat draws lod0 (every gaussian):
        /// fully zoomed in means full fidelity. Set with <see cref="DrawZoom"/>
        /// from the queue path, which knows the camera floor.
        /// </summary>
        public bool FullDetail;

        /// <summary>Level and splats drawn by the last <see cref="Draw"/> (the smoke check).</summary>
        public int LastLevel = -2;
        public int LastDrawn;

        private float _bakedX = float.NaN, _bakedY = float.NaN;
        private float _bakedScale = float.NaN, _bakedYaw = float.NaN;
        private int _bakedLevel = -1;
        private float[] _basePos;
        private int _baseQuads;

        public SplatObject(World world, SplatPlacement placement) : base(world)
        {
            Placement = placement;
            Graphic = 0x0001;
            X = (ushort)placement.X;
            Y = (ushort)placement.Y;
            Z = (sbyte)placement.Z;
            PriorityZ = (sbyte)placement.Z;
            AllowedToDraw = true;
            // PushToRenderQueue skips AlphaHue 0; splats are opaque (their
            // translucency lives in the vertex colours, like foliage).
            AlphaHue = byte.MaxValue;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (!AllowedToDraw || IsDestroyed || Placement?.Chain == null)
            {
                return false;
            }

            float s = Placement.Scale;
            int level = FullDetail && Placement.Chain.Count > 0
                ? 0
                : SplatBatcher.SelectLevel(Placement.Chain, DrawZoom, s);
            if (level < 0)
            {
                return false;
            }

            // Baked around the model origin; the placement offset rides the
            // mesh transform (like every mesh under the view transform).
            // Rebuilt only on level, scale or yaw change: a full 64800-splat
            // build every camera step hitched the game on CPU + uploads.
            // Walking past a splat now costs one draw call, no rebuild.
            float yaw = Placement.Yaw;
            float ox = RealScreenPosition.X, oy = RealScreenPosition.Y;
            if (level != _bakedLevel || s != _bakedScale || yaw != _bakedYaw)
            {
                SplatBatcher.BakeLevel(Placement.Chain.Levels[level], s, yaw, DrawZoom,
                    out float[] pos, out Godot.Color[] colors, out Godot.Vector2[] uvs, out int quads);
                SplatBatcher.UploadBaked(Mesh, pos, colors, uvs, quads, ox, oy);
                _bakedLevel = level;
                _bakedScale = s;
                _bakedYaw = yaw;
                _basePos = pos;
                _baseQuads = quads;
                _bakedX = ox;
                _bakedY = oy;
            }
            else if ((ox != _bakedX || oy != _bakedY) && _basePos != null)
            {
                // Camera moved: same quads, new offset. No recompute, and the
                // card keeps colours/uvs (positions alone cross the bus).
                SplatBatcher.MoveBaked(Mesh, _basePos, _baseQuads, ox, oy);
                _bakedX = ox;
                _bakedY = oy;
            }

            batcher.DrawSplatMesh(Mesh);
            LastLevel = level;
            LastDrawn = Placement.Chain.Levels[level].Gaussians.Length;
            return true;
        }

        public override bool CheckMouseSelection()
        {
            if (SelectedObject.Object == this)
            {
                return false;
            }

            Point m = SelectedObject.TranslatedMousePositionByViewport;
            return m.X >= PickRect.X && m.X < PickRect.X + PickRect.Width
                && m.Y >= PickRect.Y && m.Y < PickRect.Y + PickRect.Height;
        }
    }
}
