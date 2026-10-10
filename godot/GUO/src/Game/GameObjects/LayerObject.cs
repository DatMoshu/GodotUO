// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Compat;
using GUO.Renderer;

namespace GUO.Game.GameObjects
{
    /// <summary>
    /// One terrain overlay layer as a real world object: it joins the render
    /// lists at its own height, so the decal sorts against the land beneath
    /// and the statics above exactly like the ground does. The mesh is the
    /// layer's own single quad (RefreshMesh); one image, one mesh, no
    /// per-tile cutting. Underlays never enter the queue: the scene draws
    /// them before everything.
    /// Nothing upstream knows this type.
    /// </summary>
    internal sealed class LayerObject : GameObject
    {
        public TerrainLayer Layer;

        public LayerObject(World world, TerrainLayer layer) : base(world)
        {
            Layer = layer;
            Graphic = 0x0001;
            X = (ushort)System.Math.Max(0, layer.CenterX);
            Y = (ushort)System.Math.Max(0, layer.CenterY);
            AllowedToDraw = true;
            // Not zero, or PushToRenderQueue skips the object outright.
            AlphaHue = byte.MaxValue;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (!AllowedToDraw || IsDestroyed || Layer?.Texture == null
                || Layer.Mesh == null || Layer.Mesh.GetSurfaceCount() == 0)
            {
                return false;
            }

            batcher.DrawLayerMesh(Layer.Mesh, Layer.Texture);
            return true;
        }

        public override bool CheckMouseSelection()
        {
            return false;
        }
    }
}
