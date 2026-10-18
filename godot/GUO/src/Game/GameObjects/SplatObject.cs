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

        /// <summary>
        /// The hidden original this splat replaces (AllowedToDraw=false).
        /// Clicks landing on the splat's <see cref="PickRect"/> behave exactly
        /// as if they landed on this victim (same tile/object), so doors open,
        /// signs show and containers open through the replacement. Wired by
        /// GameScene.Splats (HideSingle, theme/skin queues); the tile-scan
        /// fallback below covers re-arms that have not propagated yet. Never
        /// drawn (the victim stays AllowedToDraw=false: no visual change).
        /// </summary>
        public GameObject Victim;

        /// <summary>
        /// The victim when it is still alive, else a live tile-scan for the
        /// hidden object carrying the placement's victim graphic at this
        /// tile (null when there is none). Never returns a destroyed object.
        /// </summary>
        public bool TryGetInteractionTarget(out GameObject victim)
        {
            if (Victim != null && !Victim.IsDestroyed)
            {
                victim = Victim;
                return true;
            }

            victim = FindHiddenVictim();
            if (victim != null)
            {
                Victim = victim;
                return true;
            }

            victim = null;
            return false;
        }

        /// <summary>
        /// This splat's click target: the hidden victim when one is alive,
        /// else this (preserving the old no-op for graphic-less placements).
        /// </summary>
        public BaseGameObject ResolveInteraction()
        {
            if (TryGetInteractionTarget(out GameObject victim))
            {
                return victim;
            }

            return this;
        }

        /// <summary>
        /// Maps a picked object to its click target (splats forward to their
        /// hidden victim; everything else passes through). Call it wherever
        /// <see cref="SelectedObject.Object"/> (or LastLeftDownObject) feeds
        /// single (select/label), double (use) or target handling, so both
        /// mouse paths (world viewport picking + cursor/target) see the
        /// victim.
        /// </summary>
        public static BaseGameObject Resolve(BaseGameObject obj)
        {
            if (obj is SplatObject splat)
            {
                return splat.ResolveInteraction();
            }

            return obj;
        }

        private GameObject FindHiddenVictim()
        {
            try
            {
                if (World?.Map == null)
                {
                    return null;
                }

                if (!TryGetVictimGraphic(out ushort want) || want == 0)
                {
                    return null;
                }

                GameObject head;
                try
                {
                    head = World.Map.GetTile(X, Y, false);
                }
                catch (System.Exception)
                {
                    return null;
                }

                GameObject zMatch = null;
                GameObject anyMatch = null;
                for (GameObject o = head; o != null; o = o.TNext)
                {
                    if (ReferenceEquals(o, this) || o.IsDestroyed || o.AllowedToDraw)
                    {
                        continue;
                    }

                    bool graphicMatch = o.Graphic == want
                        || (o is Static st && st.OriginalGraphic == want)
                        || (o is Land l && l.OriginalGraphic == want);
                    if (!graphicMatch)
                    {
                        continue;
                    }

                    if (anyMatch == null)
                    {
                        anyMatch = o;
                    }

                    if (o.Z == Z)
                    {
                        zMatch = o;
                        break;
                    }
                }

                return zMatch ?? anyMatch;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The replaced graphic: the placement's stored victim (parsed once
        /// at placement creation), falling back to the name scan only when
        /// it is unknown (0). Manifest names without a hex tag and preview
        /// keys have no victim: false, and the caller skips shadow/tree work.
        /// </summary>
        internal bool TryGetVictimGraphic(out ushort graphic)
        {
            graphic = Placement != null ? Placement.VictimGraphic : (ushort)0;
            if (graphic != 0)
            {
                return true;
            }

            graphic = 0;
            string name = Placement?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (int i = 0; i + 5 < name.Length; i++)
            {
                if (name[i] != '_')
                {
                    continue;
                }

                int v = 0;
                bool ok = true;
                for (int j = 1; j <= 4; j++)
                {
                    char c = name[i + j];
                    v <<= 4;
                    if (c >= '0' && c <= '9')
                    {
                        v |= c - '0';
                    }
                    else if (c >= 'a' && c <= 'f')
                    {
                        v |= c - 'a' + 10;
                    }
                    else if (c >= 'A' && c <= 'F')
                    {
                        v |= c - 'A' + 10;
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok && v != 0)
                {
                    graphic = (ushort)v;
                    return true;
                }
            }

            return false;
        }

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

            // PORT DEVIATION (GUO): ADR-0027 victim lamps emit only in active HD mode.
            var scene = Client.Game?.GetScene<GUO.Game.Scenes.GameScene>();
            if (scene?.UseHDLight == true && TryGetVictimGraphic(out ushort victim))
            {
                var data = Client.Game.UO.FileManager.TileData.StaticData;
                if (victim < data.Length && data[victim].IsLight)
                    scene.AddLight(this, this, (int)ox + 22, (int)oy + 22);
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
