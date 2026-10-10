// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Game.GameObjects;
using GUO.Renderer;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// Staged gaussian splats (ComfyUI multis) as world objects.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no generated content. Each placement
    /// from <c>GUO_SPLAT_STAGE/splats.json</c> (tools/comfy/stage.py) becomes a
    /// <see cref="SplatObject"/> queued every frame through the ordinary
    /// <c>PushToRenderQueue</c>: it sorts by <c>CalculateDepthZ</c> with roofs,
    /// walls and mobiles, draws its baked mesh at its turn, and answers clicks
    /// with its projected bounds. Without that folder nothing is queued and
    /// the per-frame cost is one null check.
    /// </remarks>
    internal partial class GameScene
    {
        private List<SplatPlacement> _splatPlacements;
        private bool _splatsLoaded;
        private bool _variantsApplied;
        private bool _splatDebugDone;
        private readonly Dictionary<string, SplatObject> _splatObjects = new();

        /// <summary>Level and splats drawn for a placement on the last frame (the smoke check).</summary>
        internal (int Level, int Drawn) SplatDrawn(string name) =>
            _splatObjects.TryGetValue(name, out SplatObject o) ? (o.LastLevel, o.LastDrawn) : (-2, 0);

        /// <summary>
        /// Drops a staged splat into the world now (the placer gump's live
        /// preview and its committed placements): same queue path as the
        /// manifest entries, keyed so re-placing updates in place.
        /// </summary>
        internal bool PlaceSplat(string key, string splatName, int x, int y, int z, float scale, float yaw)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(splatName)
                || !Renderer.SplatStage.TryGetSplat(splatName, out Renderer.SplatLodChain chain, out _)
                || chain == null)
            {
                return false;
            }

            var p = new Renderer.SplatPlacement
            {
                Name = key,
                Chain = chain,
                Facet = _world.MapIndex,
                X = x,
                Y = y,
                Z = z,
                Scale = scale > 0f ? scale : 22f,
                Yaw = yaw,
                LockZ = true,
            };
            _splatObjects[key] = new GameObjects.SplatObject(_world, p);
            return true;
        }

        /// <summary>
        /// Pick-rect centre of a placement in tile space, or (-1, -1). The
        /// probe compares the mouse tile against this: equal means the cursor
        /// is really over the splat (no real-cursor interference).
        /// </summary>
        internal Point SplatTileCenter(string name)
        {
            if (!_splatObjects.TryGetValue(name, out SplatObject o))
            {
                return new Point(-1, -1);
            }

            return new Point(
                o.PickRect.X + o.PickRect.Width / 2, o.PickRect.Y + o.PickRect.Height / 2);
        }

        /// <summary>
        /// Screen position to click a placement (its pick-rect centre through
        /// the camera), or (-1, -1) when it is not queued. The probe clicks
        /// this to prove picking selects the splat.
        /// </summary>
        internal Vector2 SplatClickAt(string name)
        {
            if (!_splatObjects.TryGetValue(name, out SplatObject o))
            {
                return new Vector2(-1, -1);
            }

            var center = new Point(
                o.PickRect.X + o.PickRect.Width / 2, o.PickRect.Y + o.PickRect.Height / 2);
            Point screen = Camera.WorldToScreen(center, true);
            return new Vector2(screen.X, screen.Y);
        }

        internal int SplatCount => _splatPlacements?.Count ?? 0;

        private readonly Dictionary<GameObjects.Multi, string> _themeHidden = new();
        private readonly Dictionary<GameObjects.Static, string> _skinHidden = new();

        /// <summary>
        /// Themed multi skins: multis of listed ids inside active zones hide
        /// (client-side, restored on toggle) and gain a splat object at their
        /// position. Runs every frame while multi rules are active so streamed
        /// chunks re-hide themselves; static swaps need no such walk (their
        /// UpdateGraphicBySeason hook fires at creation).
        /// </summary>
        private void QueueThemeMultis()
        {
            bool anyRules = false;
            foreach (Managers.ThemeManager.ActiveTheme a in Managers.ThemeManager.Active)
            {
                if (a.Theme.Multis.Count > 0 && !string.IsNullOrEmpty(a.Theme.MultiSplat))
                {
                    anyRules = true;
                    break;
                }
            }

            if (!anyRules || _world?.Map == null)
            {
                if (_themeHidden.Count > 0)
                {
                    foreach (var m in _themeHidden.Keys)
                    {
                        m.AllowedToDraw = true;
                    }

                    _themeHidden.Clear();
                }

                return;
            }

            var seen = new HashSet<GameObjects.Multi>();
            foreach (var chunk in _world.Map.GetUsedChunks())
            {
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObjects.GameObject o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                        {
                            if (o is not GameObjects.Multi m || m.IsDestroyed)
                            {
                                continue;
                            }

                            string splat = Managers.ThemeManager.MultiSplat(_world, m.X, m.Y, m.Graphic);
                            if (splat == null)
                            {
                                continue;
                            }

                            seen.Add(m);
                            m.AllowedToDraw = false;
                            _themeHidden[m] = splat;
                            string key = $"theme:{splat}:{m.X},{m.Y}";
                            if (!_splatObjects.TryGetValue(key, out SplatObject so)
                                && Renderer.SplatStage.TryGetSplat(splat, out Renderer.SplatLodChain chain, out float k))
                            {
                                so = new SplatObject(_world, new Renderer.SplatPlacement
                                {
                                    Name = key, Chain = chain, Scale = k,
                                    Facet = _world.MapIndex, X = m.X, Y = m.Y, Z = m.Z,
                                });
                                _splatObjects[key] = so;
                            }
                        }
                    }
                }
            }

            // Restore what no rule covers any more (theme off, multi left the
            // zone) and drop its skin object so nothing draws stale.
            var gone = new List<GameObjects.Multi>();
            foreach (var kv in _themeHidden)
            {
                if (!seen.Contains(kv.Key))
                {
                    kv.Key.AllowedToDraw = true;
                    _splatObjects.Remove($"theme:{kv.Value}:{kv.Key.X},{kv.Key.Y}");
                    gone.Add(kv.Key);
                }
            }

            foreach (var m in gone)
            {
                _themeHidden.Remove(m);
            }
        }

        /// <summary>
        /// Static splat skins: statics whose graphic a skin rule covers hide
        /// (client-side, restored when the rule lifts) and gain a splat
        /// object at their own tile. Runs every frame while skin rules are
        /// active so streamed chunks re-hide themselves; skipped entirely
        /// otherwise, so idle scenes pay nothing for it.
        /// </summary>
        private void QueueStaticSkins()
        {
            if (!Managers.ThemeManager.HasSplatSkins || _world?.Map == null)
            {
                if (_skinHidden.Count > 0)
                {
                    foreach (var s in _skinHidden.Keys)
                    {
                        s.AllowedToDraw = true;
                    }

                    _skinHidden.Clear();
                }

                return;
            }

            var seen = new HashSet<GameObjects.Static>();
            foreach (var chunk in _world.Map.GetUsedChunks())
            {
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObjects.GameObject o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                        {
                            if (o is not GameObjects.Static s || s.IsDestroyed)
                            {
                                continue;
                            }

                            string splat = Managers.ThemeManager.StaticSplat(_world, s.X, s.Y, s.Graphic);
                            if (splat == null)
                            {
                                continue;
                            }

                            seen.Add(s);
                            s.AllowedToDraw = false;
                            _skinHidden[s] = splat;
                            string key = $"skin:{splat}:{s.X},{s.Y},{s.Z}";
                            if (!_splatObjects.TryGetValue(key, out SplatObject so)
                                && Renderer.SplatStage.TryGetSplat(splat, out Renderer.SplatLodChain chain, out float k))
                            {
                                so = new SplatObject(_world, new Renderer.SplatPlacement
                                {
                                    Name = key, Chain = chain, Scale = k,
                                    Facet = _world.MapIndex, X = s.X, Y = s.Y, Z = s.Z,
                                    LockZ = true,
                                });
                                _splatObjects[key] = so;
                            }
                        }
                    }
                }
            }

            // Restore what no rule covers any more (rule removed, static left
            // the zone) and drop its skin object so nothing draws stale.
            var gone = new List<GameObjects.Static>();
            foreach (var kv in _skinHidden)
            {
                if (!seen.Contains(kv.Key))
                {
                    kv.Key.AllowedToDraw = true;
                    _splatObjects.Remove($"skin:{kv.Value}:{kv.Key.X},{kv.Key.Y},{kv.Key.Z}");
                    gone.Add(kv.Key);
                }
            }

            foreach (var s in gone)
            {
                _skinHidden.Remove(s);
            }
        }

        private void QueueStagedSplats()
        {
            if (!_splatsLoaded)
            {
                _splatsLoaded = true;
                string dir = SplatStage.ResolveDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    _splatPlacements = SplatStage.Load(dir);
                    GD.Print($"[GUO] staged splats: {_splatPlacements.Count} placement(s) from {dir}");
                    Managers.ThemeManager.LoadSkinTheme(dir);
                }

                // Variant atlas (PNG theme variants): load every theme, then
                // re-arm what the editor persisted. Main thread: PNG decoding
                // makes Godot textures.
                string vdir = Assets.VariantAtlas.ResolveDir();
                if (!string.IsNullOrEmpty(vdir))
                {
                    Assets.VariantAtlas.LoadAll(vdir);
                    foreach (var (name, zones) in Assets.VariantAtlas.LoadActive(vdir))
                    {
                        try
                        {
                            Managers.ThemeManager.Activate(
                                Managers.Theme.Load(Assets.VariantAtlas.ThemePath(vdir, name)), zones);
                            GD.Print($"[GUO] variant theme armed: {name}");
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }

            if (_world?.Player == null)
            {
                return;
            }

            // Armed variant themes repaint once the world is up; later
            // arrivals resolve lazily on first draw (GameObject.ResolveTheme).
            if (!_variantsApplied)
            {
                _variantsApplied = true;
                Managers.ThemeManager.Reapply(_world);
            }

            // Themed multi skins first (hides originals, ensures objects).
            QueueThemeMultis();
            // Static splat skins next (the placer gump's replace-all rules).
            QueueStaticSkins();

            int facet = _world.MapIndex;
            float zoom = Camera.Zoom;
            bool debug = !_splatDebugDone;
            _splatDebugDone = true;
            if (_splatPlacements != null)
            {
                foreach (SplatPlacement p in _splatPlacements)
                {
                    if (p.Facet != facet || p.Chain == null || p.Chain.Count == 0)
                    {
                        continue;
                    }

                    if (!_splatObjects.TryGetValue(p.Name, out _))
                    {
                        _splatObjects[p.Name] = new SplatObject(_world, p);
                    }
                }
            }

            foreach (var kv in _splatObjects)
            {
                SplatObject o = kv.Value;
                SplatPlacement p = o.Placement;
                if (p == null || p.Chain == null || p.Chain.Count == 0 || p.Facet != facet)
                {
                    continue;
                }

                // Stand on the terrain: the map's tile height wins over the
                // staged z, which was authored flat. Gump placements lock
                // their Z instead, so Z tuning moves them vertically.
                sbyte gz = (sbyte)p.Z;
                if (!p.LockZ)
                {
                    try
                    {
                        gz = _world.Map.GetTileZ(p.X, p.Y);
                    }
                    catch (System.Exception)
                    {
                    }
                }

                o.Z = gz;
                o.PriorityZ = gz;
                o.UpdateRealScreenPosition(_offset.X, _offset.Y);
                int sx = o.RealScreenPosition.X, sy = o.RealScreenPosition.Y;
                if (sx < _minPixel.X - 512 || sx > _maxPixel.X + 512
                    || sy < _minPixel.Y - 512 || sy > _maxPixel.Y + 512)
                {
                    continue;
                }

                // LOD by effective zoom; the click rect from the lod0 bounds
                // projected through the oblique map (all 8 corners). At the
                // camera floor the zoom value sits on ZoomMin exactly (the
                // setter clamps to it), so fully zoomed in draws lod0.
                o.DrawZoom = zoom;
                o.FullDetail = zoom <= Camera.ZoomMin;
                float k = p.Scale;
                float x0 = float.MaxValue, y0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue;
                Vector3 lo = p.Chain.BoundsMin, hi = p.Chain.BoundsMax;
                for (int c = 0; c < 8; c++)
                {
                    float mx = (c & 1) == 0 ? lo.X : hi.X;
                    float my = (c & 2) == 0 ? lo.Y : hi.Y;
                    float mz = (c & 4) == 0 ? lo.Z : hi.Z;
                    SplatBatcher.IsoPoint(k, p.Yaw, mx, my, mz, out float px, out float py);
                    if (px < x0) x0 = px;
                    if (py < y0) y0 = py;
                    if (px > x1) x1 = px;
                    if (py > y1) y1 = py;
                }

                const float margin = 8f;
                o.PickRect = new Rectangle(
                    (int)(sx + x0 - margin), (int)(sy + y0 - margin),
                    (int)(x1 - x0 + margin * 2f), (int)(y1 - y0 + margin * 2f));
                PushToRenderQueue(o, false, true);
                if (debug)
                {
                    var (level, drawn) = (o.LastLevel, o.LastDrawn);
                    GD.Print($"[GUO] splat {p.Name}: queued at {sx},{sy} "
                        + $"(last level {level} drawn {drawn}; build stats "
                        + $"built {SplatBatcher.LastBuilt}, skip alpha {SplatBatcher.LastSkippedAlpha}, "
                        + $"degen {SplatBatcher.LastSkippedDegenerate}, data {SplatBatcher.LastSkippedData})");
                }
            }
        }
    }
}
