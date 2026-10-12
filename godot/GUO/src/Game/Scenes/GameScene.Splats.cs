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
        private bool _mogeAnchored;
        private bool _variantsApplied;
        private bool _splatDebugDone;
        private readonly Dictionary<string, SplatObject> _splatObjects = new();

        /// <summary>Level and splats drawn for a placement on the last frame (the smoke check).</summary>
        internal (int Level, int Drawn) SplatDrawn(string name) =>
            _splatObjects.TryGetValue(name, out SplatObject o) ? (o.LastLevel, o.LastDrawn) : (-2, 0);

        /// <summary>
        /// Drops a staged splat into the world now (the placer gump's live
        /// preview and its committed placements): same queue path as the
        /// manifest entries, keyed so re-placing updates in place. The gump
        /// passes the victim graphic it replaced (theme walks set it too);
        /// 0 parses the gen_/moge_ key once here, so draws never scan names.
        /// </summary>
        internal bool PlaceSplat(string key, string splatName, int x, int y, int z, float scale, float yaw, float discSize = 1f, ushort victimGraphic = 0)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(splatName)
                || !Renderer.SplatStage.TryGetSplat(splatName, out Renderer.SplatLodChain chain, out _)
                || chain == null)
            {
                return false;
            }

            if (!(discSize > 0f))
            {
                discSize = 1f;
            }

            discSize = Math.Max(0.25f, Math.Min(4f, discSize));
            if (victimGraphic == 0)
            {
                Renderer.SplatStage.TryParseVictimGraphic(key, out victimGraphic);
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
                DiscSize = discSize,
                LockZ = true,
                VictimGraphic = victimGraphic,
            };
            _splatObjects[key] = new GameObjects.SplatObject(_world, p);
            return true;
        }

        /// <summary>
        /// Drops a live-placed splat object (the moge gump's restore path):
        /// manifest entries re-queue every frame, so this only retires
        /// live-placed keys (previews and generations). Returns nothing
        /// when the key was never placed.
        /// </summary>
        internal bool RemoveSplat(string key) =>
            !string.IsNullOrEmpty(key) && _splatObjects.Remove(key);

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
        /// Single-victim hides: one Generate hides its own victim object
        /// (client-side, restored on Restore). The victim reference alone
        /// does not survive season changes (UpdateGraphicBySeason recomputes
        /// AllowedToDraw), ThemeManager.Reapply (same walk) or chunk
        /// re-streams (a new instance appears at the tile), so each hide is
        /// keyed by its live splat key and carries facet/tile/graphic to
        /// re-find the victim. Saved gen_/moge_ placements re-arm their hide
        /// on boot the same way (the name carries the victim graphic).
        /// Victim-anchored, never splat-anchored: moving the SPLAT (the
        /// placer steppers re-place it) neither moves nor lifts the hide;
        /// the victim stays hidden at its own tile until ClearSingleHide.
        /// Preview overlays (moge:preview) never register a hide, so the
        /// original stays visible under them by design.
        /// </summary>
        private sealed class SingleHide
        {
            public int Facet;
            public int X, Y;
            public sbyte Z;
            public ushort Graphic;
            public ushort Original;
            public string SplatKey = "";
            public GameObjects.GameObject Victim;
        }

        private readonly Dictionary<string, SingleHide> _singleHides = new();

        /// <summary>
        /// Gates the manifest re-arm scan and the placement-name index below:
        /// set by hide/unhide calls (and whenever the manifest list changes),
        /// so idle frames skip the per-placement string work. The per-hide
        /// re-assert still runs every frame (one bool store on live victims);
        /// only dead-victim hides pay a tile walk, waiting on their chunk.
        /// </summary>
        private bool _singleHidesRearmNeeded = true;

        private List<Renderer.SplatPlacement> _singleRearmPlacements;
        private int _singleRearmCount = -1;

        /// <summary>
        /// Manifest placement names (rebuilt with the re-arm scan): O(1)
        /// splat-alive check instead of the O(hides x placements) Exists walk.
        /// </summary>
        private readonly HashSet<string> _splatPlacementNames = new(StringComparer.Ordinal);

        /// <summary>
        /// Records a single-victim hide (the Generate path calls this right
        /// after hiding the victim): re-asserted every frame by
        /// QueueSingleHides, so seasons, reapplies and re-streams keep it
        /// hidden until ClearSingleHide. Callers must pass the LIVE victim
        /// instance (re-found by tile+graphic at commit time, never a stale
        /// Select snapshot): the record anchors to that instance's own
        /// tile+graphic, and later moves of the splat do not touch it.
        /// </summary>
        internal void HideSingle(GameObjects.GameObject victim, string splatKey)
        {
            if (victim == null || victim.IsDestroyed || string.IsNullOrEmpty(splatKey))
            {
                return;
            }

            ushort original = victim.Graphic;
            if (victim is GameObjects.Static s)
            {
                original = s.OriginalGraphic;
            }
            else if (victim is GameObjects.Land l)
            {
                original = l.OriginalGraphic;
            }

            victim.AllowedToDraw = false;
            _singleHides[splatKey] = new SingleHide
            {
                Facet = _world != null ? _world.MapIndex : 0,
                X = victim.X,
                Y = victim.Y,
                Z = victim.Z,
                Graphic = victim.Graphic,
                Original = original,
                SplatKey = splatKey,
                Victim = victim,
            };
            _singleHidesRearmNeeded = true;
            if (_splatObjects.TryGetValue(splatKey, out SplatObject so))
            {
                so.Victim = victim;
            }
        }

        /// <summary>
        /// Forgets a single-victim hide (the Restore path): unhides the
        /// tracked victim when it is still alive. Missing keys are a no-op,
        /// so double Restore stays harmless.
        /// </summary>
        internal void ClearSingleHide(string splatKey)
        {
            if (string.IsNullOrEmpty(splatKey) || !_singleHides.TryGetValue(splatKey, out SingleHide h))
            {
                return;
            }

            _singleHides.Remove(splatKey);
            _singleHidesRearmNeeded = true;
            if (h.Victim != null && !h.Victim.IsDestroyed)
            {
                h.Victim.AllowedToDraw = true;
            }
            if (_splatObjects.TryGetValue(splatKey, out SplatObject soClear))
            {
                soClear.Victim = null;
            }
        }

        private static bool TryParseSingleGraphic(string name, out ushort graphic)
        {
            graphic = 0;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // Live keys carry a gen:/moge: prefix (gen:gen_XXXX_NN); staged
            // manifest names are the bare gen_XXXX_NN / moge_XXXX_NN.
            string n = name;
            int colon = n.IndexOf(':');
            if (colon >= 0)
            {
                n = n.Substring(colon + 1);
            }

            if (!n.StartsWith("gen_", StringComparison.Ordinal)
                && !n.StartsWith("moge_", StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = n.Split('_');
            if (parts.Length < 3)
            {
                return false;
            }

            try
            {
                graphic = Convert.ToUInt16(parts[1], 16);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool SingleMatches(SingleHide h, GameObjects.GameObject o)
        {
            if (o == null || o.IsDestroyed || o.X != h.X || o.Y != h.Y || o.Z != h.Z)
            {
                return false;
            }

            if (o.Graphic == h.Graphic || o.Graphic == h.Original)
            {
                return true;
            }

            if (o is GameObjects.Static s && (s.OriginalGraphic == h.Original || s.OriginalGraphic == h.Graphic))
            {
                return true;
            }

            if (o is GameObjects.Land l && (l.OriginalGraphic == h.Original || l.OriginalGraphic == h.Graphic))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Re-asserts single-victim hides every frame: live victims stay
        /// hidden across season changes and Reapply (both recompute
        /// AllowedToDraw on the same instance), streamed chunks re-hide by
        /// tile/graphic lookup, and saved gen_/moge_ placements re-arm their
        /// hide on boot. Stale entries drop their dead reference; entries
        /// whose splat is gone restore and go away. Deliberately deaf to
        /// splat moves: the hide follows the VICTIM's recorded tile, not
        /// the splat's current tile, so tuning X/Y/Z never reveals the
        /// original (only ClearSingleHide does).
        /// </summary>
        private void QueueSingleHides()
        {
            if (_world?.Map == null)
            {
                return;
            }

            // Saved-then-reloaded placements re-arm their victim-hide on
            // boot: the manifest already records the placement tile, and the
            // gen_/moge_ name carries the victim graphic. Gated on real
            // changes (hide/unhide calls, manifest load) so idle frames pay
            // nothing here; the name index rebuilds on the same gate and
            // replaces the O(hides x placements) Exists walk below.
            bool placementsChanged = !ReferenceEquals(_splatPlacements, _singleRearmPlacements)
                || (_splatPlacements?.Count ?? -1) != _singleRearmCount;
            if (_singleHidesRearmNeeded || placementsChanged)
            {
                _singleHidesRearmNeeded = false;
                _singleRearmPlacements = _splatPlacements;
                _singleRearmCount = _splatPlacements?.Count ?? -1;
                _splatPlacementNames.Clear();
                if (_splatPlacements != null)
                {
                    foreach (Renderer.SplatPlacement p in _splatPlacements)
                    {
                        if (p == null || string.IsNullOrEmpty(p.Name))
                        {
                            continue;
                        }

                        _splatPlacementNames.Add(p.Name);
                        if (_singleHides.ContainsKey(p.Name))
                        {
                            continue;
                        }

                        if (TryParseSingleGraphic(p.Name, out ushort g))
                        {
                            _singleHides[p.Name] = new SingleHide
                            {
                                Facet = p.Facet,
                                X = p.X,
                                Y = p.Y,
                                Z = (sbyte)p.Z,
                                Graphic = g,
                                Original = g,
                                SplatKey = p.Name,
                                Victim = null,
                            };
                        }
                    }
                }
            }

            if (_singleHides.Count == 0)
            {
                return;
            }

            int facet = _world.MapIndex;
            var gone = new List<string>();
            foreach (var kv in _singleHides)
            {
                SingleHide h = kv.Value;
                bool splatAlive = _splatObjects.ContainsKey(h.SplatKey)
                    || _splatPlacementNames.Contains(h.SplatKey);
                if (!splatAlive)
                {
                    if (h.Victim != null && !h.Victim.IsDestroyed)
                    {
                        h.Victim.AllowedToDraw = true;
                    }

                    gone.Add(kv.Key);
                    continue;
                }

                if (h.Facet != facet)
                {
                    continue;
                }

                if (h.Victim != null && !h.Victim.IsDestroyed)
                {
                    h.Victim.AllowedToDraw = false;
                    if (_splatObjects.TryGetValue(h.SplatKey, out SplatObject soLive))
                    {
                        soLive.Victim = h.Victim;
                    }
                    continue;
                }

                // Chunk re-stream: the old instance is destroyed, so find
                // the new one at the tile by graphic. No chunk load here
                // (GetTile load:false): unloaded tiles simply wait.
                h.Victim = null;
                GameObjects.GameObject head = null;
                try
                {
                    head = _world.Map.GetTile(h.X, h.Y, false);
                }
                catch (Exception)
                {
                }

                for (GameObjects.GameObject o = head; o != null; o = o.TNext)
                {
                    if (SingleMatches(h, o))
                    {
                        o.AllowedToDraw = false;
                        h.Victim = o;
                        break;
                    }
                }

                if (_splatObjects.TryGetValue(h.SplatKey, out SplatObject soRearm))
                {
                    soRearm.Victim = h.Victim != null && !h.Victim.IsDestroyed ? h.Victim : null;
                }
            }

            foreach (string k in gone)
            {
                _singleHides.Remove(k);
            }
        }

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
                                    VictimGraphic = m.Graphic,
                                });
                                _splatObjects[key] = so;
                            }
                            if (so != null && !m.IsDestroyed)
                            {
                                so.Victim = m;
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
                                    VictimGraphic = s.Graphic,
                                });
                                _splatObjects[key] = so;
                            }
                            if (so != null && !s.IsDestroyed)
                            {
                                so.Victim = s;
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

            // MoGe replacer anchor: staged moge_* PLYs store centered
            // billboards (a PLY cannot carry the tile shift: the parser
            // recenters on bbox), so shift each once from its graphic's tile
            // anchor, the same math as the gump's live placement. Runs once
            // the art archive is readable; live-placed chains are already
            // anchored by the gump and never pass through here.
            if (!_mogeAnchored && _splatPlacements != null)
            {
                _mogeAnchored = true;
                foreach (SplatPlacement p in _splatPlacements)
                {
                    if (p?.Chain?.Levels == null || p.Chain.Levels.Length == 0
                        || p.Chain.Levels[0] == null || string.IsNullOrEmpty(p.Name)
                        || !p.Name.StartsWith("moge_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        string[] parts = p.Name.Split('_');
                        if (parts.Length < 3)
                        {
                            continue;
                        }

                        ushort graphic = Convert.ToUInt16(parts[1], 16);
                        var shot = Comfy.ComfyClient.ExtractArtShot(graphic, false);
                        if (shot == null || shot.TightWidth <= 0)
                        {
                            continue;
                        }

                        float cx = shot.TightX0 + shot.TightWidth * 0.5f;
                        float cy = shot.TightY0 + shot.TightHeight * 0.5f;
                        float dx = cx - (shot.Width * 0.5f - 22f);
                        float dy = cy - (shot.Height - 44f);
                        SplatBatcher.AnchorShift(dx, dy, p.Scale, p.Yaw,
                            out float mx, out float my, out float mz);
                        var level = p.Chain.Levels[0];
                        var gs = level.Gaussians;
                        for (int i = 0; i < gs.Length; i++)
                        {
                            gs[i].X += mx;
                            gs[i].Y += my;
                            gs[i].Z += mz;
                        }

                        var shift = new Vector3(mx, my, mz);
                        level.BoundsMin += shift;
                        level.BoundsMax += shift;
                        p.Chain.BoundsMin = level.BoundsMin;
                        p.Chain.BoundsMax = level.BoundsMax;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            // Themed multi skins first (hides originals, ensures objects).
            QueueThemeMultis();
            // Static splat skins next (the placer gump's replace-all rules).
            QueueStaticSkins();
            // Single-victim hides last, so Generate victims stay hidden
            // across seasons, reapplies and re-streams.
            QueueSingleHides();

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

                // LOD before queue (zoom and scale only: the same level Draw
                // picks, FullDetail override included), so culled levels skip
                // the queue, the bake lookup and the draw entirely.
                o.DrawZoom = zoom;
                o.FullDetail = zoom <= Camera.ZoomMin;
                float k = p.Scale;
                int preLevel = o.FullDetail ? 0 : SplatBatcher.SelectLevel(p.Chain, zoom, k);
                if (preLevel < 0)
                {
                    continue;
                }

                // Bounds rect from the lod0 bounds projected through the
                // oblique map (all 8 corners), with the yaw trig hoisted out
                // of the loop. At the camera floor the zoom value sits on
                // ZoomMin exactly (the setter clamps to it), so fully zoomed
                // in draws lod0.
                float x0 = float.MaxValue, y0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue;
                Vector3 lo = p.Chain.BoundsMin, hi = p.Chain.BoundsMax;
                double ywr = p.Yaw * Math.PI / 180.0;
                float cyw = (float)Math.Cos(ywr), syw = (float)Math.Sin(ywr);
                for (int c = 0; c < 8; c++)
                {
                    float mx = (c & 1) == 0 ? lo.X : hi.X;
                    float my = (c & 2) == 0 ? lo.Y : hi.Y;
                    float mz = (c & 4) == 0 ? lo.Z : hi.Z;
                    SplatBatcher.IsoPointW(k, cyw, syw, mx, my, mz, out float px, out float py);
                    if (px < x0) x0 = px;
                    if (py < y0) y0 = py;
                    if (px > x1) x1 = px;
                    if (py > y1) y1 = py;
                }

                // Tight off-screen cull on the rect itself (replaces the
                // fixed +-512px centre check, which queued every splat within
                // half a screen). Bounds hold gaussian centres; quads can
                // bloom past them up to the bake clamp, hence the guard.
                int sx = o.RealScreenPosition.X, sy = o.RealScreenPosition.Y;
                const float cullMargin = 128f;
                float rx0 = sx + x0, ry0 = sy + y0, rx1 = sx + x1, ry1 = sy + y1;
                if (rx1 < _minPixel.X - cullMargin || rx0 > _maxPixel.X + cullMargin
                    || ry1 < _minPixel.Y - cullMargin || ry0 > _maxPixel.Y + cullMargin)
                {
                    continue;
                }

                const float margin = 8f;
                o.PickRect = new Rectangle(
                    (int)(rx0 - margin), (int)(ry0 - margin),
                    (int)(rx1 - rx0 + margin * 2f), (int)(ry1 - ry0 + margin * 2f));
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
