// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.Scenes;
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

            // Victim hue (mirrors StaticView's highlight/out-of-range/dead;
            // splats have no partial-hue tiledata and the victim hue itself is
            // not stored on the placement, so it rides the splat shader's
            // uniform via DrawSplatMesh instead of baked vertex colours).
            ushort hue = Hue;
            if (ProfileManager.CurrentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
            }
            else if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player != null && World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, false, AlphaHue / 255f);

            // Circle of transparency (mirrors StaticView, minus its
            // tree/foliage exemption when the victim graphic is unknown:
            // parsed victims that are trees/foliage stay visible below).
            bool cot = false;
            if (World.Player != null && ProfileManager.CurrentProfile.UseCircleOfTransparency)
            {
                cot = TransparentTest(World.Player.Z + 5);
                if (cot && TryGetVictimGraphic(out ushort victim))
                {
                    bool foliage = false;
                    try
                    {
                        foliage = Client.Game.UO.FileManager.TileData.StaticData[victim].IsFoliage;
                    }
                    catch (System.Exception)
                    {
                    }

                    if (StaticFilters.IsTree(victim, out _) || foliage)
                    {
                        cot = false;
                    }
                }
            }

            float s = Placement.Scale;
            int level = FullDetail && Placement.Chain.Count > 0
                ? 0
                : SplatBatcher.SelectLevel(Placement.Chain, DrawZoom, s);
            if (level < 0)
            {
                return false;
            }

            // Shared LOCAL bake; the placement offset rides the draw
            // transform (like every mesh under the view transform). Same
            // bytes BakeLevel+UploadBaked at (0,0) produce, so identical
            // (Chain, level, scale, yaw, disc) placements share one mesh and
            // camera moves cost zero uploads (no per-instance mesh, no
            // MoveBaked rewrite: walking past a splat is one draw call).
            float yaw = Placement.Yaw;
            float disc = Placement.DiscSize;
            if (!(disc > 0f))
            {
                disc = 1f;
            }

            float ox = RealScreenPosition.X, oy = RealScreenPosition.Y;
            ArrayMesh mesh = SplatBatcher.GetSharedBaked(
                Placement.Chain, level, s, yaw, DrawZoom, disc,
                out bool _unusedBright, out int _);
            if (mesh == null)
            {
                return false;
            }

            // Cast shadows (mirrors StaticView: tree/foliage/rock victims only,
            // from the victim's art, not the splat mesh: the splat replaces the
            // victim at its tile, so its ground shadow is the victim's).
            if (ProfileManager.CurrentProfile.ShadowsEnabled
                && ProfileManager.CurrentProfile.ShadowsStatics
                && TryGetVictimGraphic(out ushort shadowGraphic))
            {
                bool foliage = false;
                try
                {
                    foliage = Client.Game.UO.FileManager.TileData.StaticData[shadowGraphic].IsFoliage;
                }
                catch (System.Exception)
                {
                }

                if (StaticFilters.IsTree(shadowGraphic, out _) || foliage || StaticFilters.IsRock(shadowGraphic))
                {
                    DrawVictimShadow(batcher, shadowGraphic, posX, posY, depth);
                }
            }

            batcher.DrawSplatMesh(mesh, new Vector2(ox, oy), hueVec, cot);

            // Light contribution (mirrors the StaticView/MultiView AddLight
            // calls: a static glows only when its own tiledata IsLight, so a
            // splat glows only when its victim's tiledata IsLight — a splat
            // replacing a lamp keeps its lamp light even when the bake itself
            // is dark, while a tree/rock/wall replacement never emits
            // regardless of baked brightness. Baked brightness alone must
            // never gate AddLight: ordinary sunlit art trips any max-channel
            // threshold and would glow at night with no source.
            bool victimIsLight = false;
            try
            {
                if (TryGetVictimGraphic(out ushort lightVictim) && lightVictim != 0)
                {
                    victimIsLight = Client.Game.UO.FileManager.TileData.StaticData[lightVictim].IsLight;
                }
            }
            catch (System.Exception)
            {
            }

            if (victimIsLight)
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            LastLevel = level;
            LastDrawn = Placement.Chain.Levels[level].Gaussians.Length;
            return true;
        }

        public override bool TransparentTest(int z)
        {
            // Splats are multi-sized placements with no ItemData.Height, so
            // they are always CoT-eligible unless well below the player.
            return Z > z - 10;
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

        private static void DrawVictimShadow(UltimaBatcher2D batcher, ushort graphic, int posX, int posY, float depth)
        {
            try
            {
                ref readonly var art = ref Client.Game.UO.Arts.GetArt(graphic);
                if (art.Texture == null)
                {
                    return;
                }

                int w = (art.UV.Width >> 1) - 22;
                int h = art.UV.Height - 44;
                batcher.DrawShadow(art.Texture, new Vector2(posX - w, posY - h), art.UV, false, depth + 0.25f);
            }
            catch (System.Exception)
            {
            }
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
