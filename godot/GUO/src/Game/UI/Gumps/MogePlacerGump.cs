// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.Comfy;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Renderer;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// In-game MoGe replacer: select a world object, generate a 3D-gaussian
    /// splat from its own sprite artwork (MogeClient shells tools/moge/
    /// moge_to_ply.py, like VoiceManager shells voice.py), auto-fit it like
    /// SplatPlacerGump.OnGenerated, then place it at the victim's own tile
    /// and hide the original. Restore removes the live splat and unhides the
    /// original. Opened with the "-moge" client command. Testing tool, not
    /// shipped UI: placements are client-side only (GUO_SPLAT_STAGE/
    /// splats.json on Save) and never touch the shard. No new render code:
    /// the replacement draws through the ordinary SplatObject path.
    /// </summary>
    internal sealed class MogePlacerGump : Gump
    {
        private const int W = 300;
        private const int H = 602;
        private const string PreviewKey = "moge:preview";

        private readonly Label _tileLabel;
        private readonly Label _xLabel;
        private readonly Label _yLabel;
        private readonly Label _zLabel;
        private readonly Label _scaleLabel;
        private readonly Label _yawLabel;
        private readonly Label _discLabel;
        private readonly Label _graphicLabel;
        private readonly PreviewPic _beforePic;
        private readonly PreviewPic _afterPic;
        private readonly Label _statusLabel;

        private bool _hasTile;
        private int _tx, _ty, _tz;
        private float _scale = 44f;
        private float _yaw = 180f;
        private float _disc = 1f;
        private ushort _targetGraphic;
        private GameObject _targetObj;
        private readonly List<PlacedEntry> _placed = new List<PlacedEntry>();
        private Task<byte[]> _genTask;
        private string _genStatus = "";
        private string _shownStatus = "";
        private int _genCount;
        private string _lastGenName;
        private string _liveKey;
        private CancellationTokenSource _genCts;

        private sealed class AutoVictim
        {
            public int X, Y;
            public sbyte Z;
            public GameObject Obj;
        }

        private sealed class AutoJob
        {
            public ushort Graphic;
            public List<AutoVictim> Victims;
            public byte[] Png;
            public int ArtW, ArtH, TightX0, TightY0, TightW, TightH, ArtWidth;
        }

        private readonly List<AutoJob> _autoPending = new List<AutoJob>();
        private AutoJob _autoCurrent;
        private int _autoTotal;
        private int _autoDone;
        private bool _autoActive;
        private float _autoBaseScale;
        private float _autoYaw;
        private float _autoDisc;
        private readonly List<string> _autoKeys = new List<string>();

        private sealed class PlacedEntry
        {
            public string Name;
            public SplatPlacement At;
            public List<byte[]> Lods;
        }

        private enum Buttons
        {
            Select,
            Generate,
            Restore,
            XDown,
            XUp,
            YDown,
            YUp,
            ZDown,
            ZUp,
            ScaleDown,
            ScaleUp,
            YawDown,
            YawUp,
            DiscDown,
            DiscUp,
            Save,
            Close,
            AutoQueue,
            ClearAll
        }

        public MogePlacerGump(World world)
            : base(world, 120, 120)
        {
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("MoGe Replacer", true, 0x0481) { X = 12, Y = 8 });

            var close = new NiceButton(W - 30, 6, 22, 20, ButtonAction.Activate, "X")
            {
                ButtonParameter = (int)Buttons.Close,
                IsSelectable = false
            };
            Add(close);

            int y = 34;
            Add(new NiceButton(12, y, W - 24, 24, ButtonAction.Activate, "Select")
            {
                ButtonParameter = (int)Buttons.Select,
                IsSelectable = false
            });

            y += 32;
            Add(new Label("Tile:", true, 0x0386) { X = 12, Y = y });
            _tileLabel = new Label("<none>", true, 0x0021, 200) { X = 70, Y = y };
            Add(_tileLabel);

            y += 26;
            Add(new Label("Graphic:", true, 0x0386) { X = 12, Y = y });
            _graphicLabel = new Label("<none>", true, 0x0021) { X = 100, Y = y };
            Add(_graphicLabel);

            y += 26;
            Add(new Label("Before:", true, 0x0386) { X = 12, Y = y });
            Add(new Label("After:", true, 0x0386) { X = 156, Y = y });
            y += 18;
            _beforePic = new PreviewPic { X = 12, Y = y, Width = 132, Height = 132 };
            Add(_beforePic);
            _afterPic = new PreviewPic { X = 156, Y = y, Width = 132, Height = 132 };
            Add(_afterPic);
            y += 140;

            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Generate")
            {
                ButtonParameter = (int)Buttons.Generate,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Restore")
            {
                ButtonParameter = (int)Buttons.Restore,
                IsSelectable = false
            });

            y += 32;
            _xLabel = StepperRow("X:", y, Buttons.XDown, Buttons.XUp);
            y += 26;
            _yLabel = StepperRow("Y:", y, Buttons.YDown, Buttons.YUp);
            y += 26;
            _zLabel = StepperRow("Z:", y, Buttons.ZDown, Buttons.ZUp);

            y += 30;
            Add(new Label("Scale:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "-")
            {
                ButtonParameter = (int)Buttons.ScaleDown,
                IsSelectable = false
            });
            _scaleLabel = new Label("44", true, 0x0021) { X = 110, Y = y };
            Add(_scaleLabel);
            Add(new NiceButton(160, y - 2, 26, 22, ButtonAction.Activate, "+")
            {
                ButtonParameter = (int)Buttons.ScaleUp,
                IsSelectable = false
            });

            y += 30;
            Add(new Label("Yaw:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "-")
            {
                ButtonParameter = (int)Buttons.YawDown,
                IsSelectable = false
            });
            _yawLabel = new Label("180", true, 0x0021) { X = 110, Y = y };
            Add(_yawLabel);
            Add(new NiceButton(160, y - 2, 26, 22, ButtonAction.Activate, "+")
            {
                ButtonParameter = (int)Buttons.YawUp,
                IsSelectable = false
            });

            y += 30;
            Add(new Label("Disc:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "-")
            {
                ButtonParameter = (int)Buttons.DiscDown,
                IsSelectable = false
            });
            _discLabel = new Label("1x", true, 0x0021) { X = 110, Y = y };
            Add(_discLabel);
            Add(new NiceButton(160, y - 2, 26, 22, ButtonAction.Activate, "+")
            {
                ButtonParameter = (int)Buttons.DiscUp,
                IsSelectable = false
            });

            y += 34;
            Add(new NiceButton(12, y, W - 24, 24, ButtonAction.Activate, "Save")
            {
                ButtonParameter = (int)Buttons.Save,
                IsSelectable = false
            });

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Auto Queue")
            {
                ButtonParameter = (int)Buttons.AutoQueue,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Clear All")
            {
                ButtonParameter = (int)Buttons.ClearAll,
                IsSelectable = false
            });

            y += 32;
            _statusLabel = new Label(string.Empty, true, 0x0021, W - 24) { X = 12, Y = y };
            Add(_statusLabel);

            Width = W;
            Height = H;
            UpdateLabels();
            SetInScreen();
            Say("Select an object to replace...");
        }

        public override GumpType GumpType => GumpType.MogePlacer;

        private Label StepperRow(string caption, int y, Buttons down, Buttons up)
        {
            Add(new Label(caption, true, 0x0386) { X = 40, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "-")
            {
                ButtonParameter = (int)down,
                IsSelectable = false
            });
            var value = new Label("–", true, 0x0021) { X = 110, Y = y };
            Add(value);
            Add(new NiceButton(160, y - 2, 26, 22, ButtonAction.Activate, "+")
            {
                ButtonParameter = (int)up,
                IsSelectable = false
            });
            return value;
        }

        private GameScene Scene => Client.Game.GetScene<GameScene>();

        private void UpdateLabels()
        {
            _tileLabel.Text = _hasTile ? $"{_tx}, {_ty}, {_tz}" : "<none> (Select first)";
            _xLabel.Text = _hasTile ? _tx.ToString() : "–";
            _yLabel.Text = _hasTile ? _ty.ToString() : "–";
            _zLabel.Text = _hasTile ? _tz.ToString() : "–";
            _scaleLabel.Text = _scale.ToString("0.#");
            _yawLabel.Text = _yaw.ToString("0.#");
            _discLabel.Text = _disc.ToString("0.##") + "x";
            _graphicLabel.Text = _targetGraphic == 0
                ? "<none>"
                : $"0x{_targetGraphic:X4} ({_targetGraphic})";
        }

        public override void Update()
        {
            base.Update();
            if (IsDisposed)
            {
                return;
            }

            if (_genTask != null && _genTask.IsCompleted)
            {
                Task<byte[]> done = _genTask;
                _genTask = null;
                AutoJob auto = _autoCurrent;
                _autoCurrent = null;
                if (done.IsFaulted)
                {
                    if (auto != null)
                    {
                        Say($"Auto 0x{auto.Graphic:X4} failed: {done.Exception?.GetBaseException().Message}");
                        _autoDone++;
                        ContinueAutoQueue();
                    }
                    else
                    {
                        Say($"Generate failed: {done.Exception?.GetBaseException().Message}");
                    }
                }
                else if (done.IsCanceled)
                {
                    if (auto != null)
                    {
                        // Clear already stopped the queue and said Cleared:
                        // keep that message instead of overwriting it.
                        bool wasActive = _autoActive;
                        _autoActive = false;
                        _autoPending.Clear();
                        if (wasActive)
                        {
                            Say("Auto cancelled.");
                        }
                    }
                    else
                    {
                        Say("Generate cancelled.");
                    }
                }
                else
                {
                    if (auto != null)
                    {
                        OnAutoGenerated(auto, done.Result);
                    }
                    else
                    {
                        OnGenerated(done.Result);
                    }
                }
            }
            else if (_genTask != null && _genStatus != _shownStatus)
            {
                _shownStatus = _genStatus;
                Say(_genStatus);
            }
        }

        private void Say(string s)
        {
            _statusLabel.Text = s;
        }

        /// <summary>
        /// The Before cell follows the selection: the victim's own artwork,
        /// pulled the same way Generate reads it. A new victim retires the
        /// previous run's After cell.
        /// </summary>
        private void RefreshBeforePic()
        {
            _afterPic?.SetPng(null);
            byte[] png = null;
            if (_targetGraphic != 0 && _targetObj != null && !_targetObj.IsDestroyed)
            {
                try
                {
                    png = ComfyClient.ExtractArtShot(
                        _targetGraphic, _targetObj is Land)?.Png;
                }
                catch
                {
                }
            }

            _beforePic?.SetPng(png);
        }

        private void RefreshPreview()
        {
            if (IsDisposed || !_hasTile || _lastGenName == null || _genBaseSet == null)
            {
                return;
            }

            // Preview re-anchors the base billboard for the live yaw/scale:
            // the registered chain always carries the translated copy, never
            // the centered base (a PLY cannot carry the shift: the parser
            // recenters on bbox, so it is reapplied after every parse/load).
            // Preview-only: it never hides the victim (no HideSingle call),
            // so the original stays visible underneath by design. Runs only
            // while nothing is committed (_liveKey == null); once Generate
            // commits, the steppers move the live placement instead
            // (RefreshLive) so a Z nudge cannot strand a second overlay
            // beside the committed splat.
            RetranslatePreview();
            GameScene scene = Scene;
            if (scene == null || !scene.PlaceSplat(PreviewKey, _lastGenName, _tx, _ty, _tz, _scale, _yaw, _disc))
            {
                Say("Preview failed.");
                return;
            }

            Say($"Preview {_lastGenName} at {_tx}, {_ty}, {_tz} (not committed; original visible).");
        }

        /// <summary>
        /// Post-commit tuning: re-place the COMMITTED live splat in place
        /// (same key updates in place) instead of spawning the preview
        /// overlay, persist the tuned tile into the Save entry, and retire
        /// any preview so exactly one object draws. The victim hide stays
        /// anchored to the victim's own tile+graphic (HideSingle): moving
        /// the splat off that tile does not reveal it, only Restore does.
        /// </summary>
        private void RefreshLive()
        {
            if (IsDisposed || !_hasTile || _lastGenName == null || _genBaseSet == null || _liveKey == null)
            {
                return;
            }

            RetranslatePreview();
            GameScene scene = Scene;
            if (scene == null || !scene.PlaceSplat(_liveKey, _lastGenName, _tx, _ty, _tz, _scale, _yaw, _disc, _targetGraphic))
            {
                Say("Move failed.");
                return;
            }

            scene.RemoveSplat(PreviewKey);
            foreach (PlacedEntry e in _placed)
            {
                if (e != null && e.Name == _lastGenName && e.At != null)
                {
                    e.At.X = _tx;
                    e.At.Y = _ty;
                    e.At.Z = _tz;
                    e.At.Scale = _scale;
                    e.At.Yaw = _yaw;
                    e.At.DiscSize = _disc;
                }
            }

            string victimNote = "";
            if (_targetObj != null && !_targetObj.IsDestroyed
                && (_targetObj.X != _tx || _targetObj.Y != _ty || _targetObj.Z != _tz))
            {
                victimNote = $" Victim 0x{_targetGraphic:X4} stays hidden at {_targetObj.X}, {_targetObj.Y}, {_targetObj.Z}.";
            }

            Say($"Moved {_lastGenName} to {_tx}, {_ty}, {_tz} (committed; 0x{_targetGraphic:X4} stays hidden).{victimNote} Save to keep.");
        }

        /// <summary>
        /// The victim instance to hide at commit time. The Select snapshot
        /// (_targetObj) can go stale while the GPU run takes minutes (chunk
        /// re-stream replaces the instance), so prefer it only while it
        /// still names the tuned tile+graphic; otherwise re-find the live
        /// drawable at (_tx, _ty, _tz) by graphic (season-swapped
        /// statics/land match on their original too). Falls back to the
        /// snapshot when it is still alive, else null (placed, not hidden).
        /// </summary>
        private GameObject ResolveVictim()
        {
            if (_targetObj != null && !_targetObj.IsDestroyed && _targetObj.AllowedToDraw
                && _targetObj.X == _tx && _targetObj.Y == _ty && _targetObj.Z == _tz
                && _targetObj.Graphic == _targetGraphic)
            {
                return _targetObj;
            }

            try
            {
                for (GameObject o = World.Map.GetTile(_tx, _ty, false); o != null; o = o.TNext)
                {
                    if (o == null || o.IsDestroyed || !o.AllowedToDraw)
                    {
                        continue;
                    }

                    if (o.X != _tx || o.Y != _ty || o.Z != _tz)
                    {
                        continue;
                    }

                    if (o.Graphic == _targetGraphic)
                    {
                        return o;
                    }

                    if (o is Static s && s.OriginalGraphic == _targetGraphic)
                    {
                        return o;
                    }

                    if (o is Land l && l.OriginalGraphic == _targetGraphic)
                    {
                        return o;
                    }
                }
            }
            catch
            {
            }

            return _targetObj != null && !_targetObj.IsDestroyed ? _targetObj : null;
        }

        /// <summary>
        /// Screen offset of the victim's artwork bbox-center from its tile
        /// origin (RSP): sprite centre sits at RSP+(22, 44-H/2) for full art,
        /// exactly (tightCenter-anchor) with the tight bbox. Tile-space px.
        /// </summary>
        private void AnchorOffset(out float dx, out float dy)
        {
            float cx = _tightX0 + _tightW * 0.5f;
            float cy = _tightY0 + _tightH * 0.5f;
            dx = cx - (_artW * 0.5f - 22f);
            dy = cy - (_artH - 44f);
        }

        private void RetranslatePreview()
        {
            if (_genBaseSet == null || _lastGenName == null)
            {
                return;
            }

            if (!SplatStage.TryGetSplat(_lastGenName, out SplatLodChain chain, out _)
                || chain?.Levels == null || chain.Levels.Length == 0
                || chain.Levels[0] == null)
            {
                return;
            }

            AnchorOffset(out float dx, out float dy);
            Renderer.SplatBatcher.AnchorShift(dx, dy, _scale, _yaw,
                out float dmx, out float dmy, out float dmz);
            Assets.SplatSet baseSet = _genBaseSet;
            Assets.SplatSet level = chain.Levels[0];
            var moved = new Assets.SplatGaussian[baseSet.Gaussians.Length];
            for (int i = 0; i < moved.Length; i++)
            {
                Assets.SplatGaussian g = baseSet.Gaussians[i];
                g.X += dmx;
                g.Y += dmy;
                g.Z += dmz;
                moved[i] = g;
            }

            level.Gaussians = moved;
            level.BoundsMin = baseSet.BoundsMin + new Godot.Vector3(dmx, dmy, dmz);
            level.BoundsMax = baseSet.BoundsMax + new Godot.Vector3(dmx, dmy, dmz);
            chain.BoundsMin = level.BoundsMin;
            chain.BoundsMax = level.BoundsMax;
        }

        public override void OnButtonClick(int buttonID)
        {
            bool preview = true;
            switch (buttonID)
            {
                case (int)Buttons.Select:
                    if (World.TargetManager.IsTargeting)
                    {
                        World.TargetManager.CancelTarget();
                    }

                    World.TargetManager.SetTargeting(
                        (GameObject obj) =>
                        {
                            // Stacking guard: hidden tiles already have a
                            // replacement, so they cannot be selected again.
                            if (obj != null && !obj.AllowedToDraw)
                            {
                                GameActions.Print(World, "MogePlacer: already replaced (hidden).");
                                Say("Already replaced.");
                                obj = null;
                            }

                            if (obj != null)
                            {
                                // Select picks the replace victim: no staged
                                // preview moves, only the graphic readout.
                                _tx = obj.X;
                                _ty = obj.Y;
                                _tz = obj.Z;
                                _hasTile = true;
                                _targetGraphic = obj.Graphic;
                                _targetObj = obj;
                                GameActions.Print(World,
                                    $"MogePlacer selected: 0x{_targetGraphic:X4} at {_tx}, {_ty}, {_tz}");
                                RefreshBeforePic();
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click the object to replace...");
                    preview = false;
                    break;

                case (int)Buttons.XDown:
                    _tx--;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.XUp:
                    _tx++;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.YDown:
                    _ty--;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.YUp:
                    _ty++;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.ZDown:
                    _tz--;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.ZUp:
                    _tz++;
                    _hasTile = true;
                    UpdateLabels();
                    break;

                case (int)Buttons.ScaleDown:
                    _scale = System.Math.Max(2f, _scale - 2f);
                    UpdateLabels();
                    break;

                case (int)Buttons.ScaleUp:
                    _scale = System.Math.Min(160f, _scale + 2f);
                    UpdateLabels();
                    break;

                case (int)Buttons.YawDown:
                    _yaw -= 15f;
                    if (_yaw < 0f)
                    {
                        _yaw += 360f;
                    }

                    UpdateLabels();
                    break;

                case (int)Buttons.YawUp:
                    _yaw += 15f;
                    if (_yaw >= 360f)
                    {
                        _yaw -= 360f;
                    }

                    UpdateLabels();
                    break;

                case (int)Buttons.DiscDown:
                    _disc = System.Math.Max(0.25f, _disc - 0.25f);
                    UpdateLabels();
                    break;

                case (int)Buttons.DiscUp:
                    _disc = System.Math.Min(4f, _disc + 0.25f);
                    UpdateLabels();
                    break;

                case (int)Buttons.Generate:
                    StartGenerate();
                    preview = false;
                    break;

                case (int)Buttons.Restore:
                    {
                        GameScene scene = Scene;
                        if (scene != null)
                        {
                            if (_liveKey != null)
                            {
                                scene.RemoveSplat(_liveKey);
                                scene.ClearSingleHide(_liveKey);
                            }

                            scene.RemoveSplat(PreviewKey);
                        }

                        _liveKey = null;
                        if (_targetObj != null && !_targetObj.IsDestroyed)
                        {
                            _targetObj.AllowedToDraw = true;
                            Say($"Restored 0x{_targetGraphic:X4}.");
                        }
                        else
                        {
                            Say("Nothing to restore.");
                        }
                    }
                    preview = false;
                    break;

                case (int)Buttons.Save:
                    {
                        string dir = SplatStage.ResolveDir();
                        int saved = 0;
                        if (!string.IsNullOrEmpty(dir))
                        {
                            string note = _targetGraphic == 0
                                ? "moge replacer"
                                : $"moge replacer: graphic 0x{_targetGraphic:X4}";
                            foreach (PlacedEntry e in _placed)
                            {
                                if (e.Lods != null && !SplatStage.StagedNames().Contains(e.Name))
                                {
                                    SplatStage.SaveNewSplat(dir, e.Name, e.Lods, e.At, note);
                                }

                                if (SplatStage.SavePlacement(dir, e.Name, e.At))
                                {
                                    saved++;
                                }
                            }
                        }

                        Say(saved > 0 ? $"Saved {saved} placement(s)." : "Nothing saved.");
                    }
                    preview = false;
                    break;

                case (int)Buttons.AutoQueue:
                    StartAutoQueue();
                    preview = false;
                    break;

                case (int)Buttons.ClearAll:
                    ClearAuto();
                    preview = false;
                    break;

                case (int)Buttons.Close:
                    preview = false;
                    _autoActive = false;
                    _autoPending.Clear();
                    _autoCurrent = null;
                    try
                    {
                        _genCts?.Cancel();
                    }
                    catch
                    {
                    }

                    Dispose();
                    break;
            }
            if (preview)
            {
                // Committed placements tune live (one object on screen);
                // only the uncommitted state tunes the preview overlay.
                if (_liveKey != null)
                {
                    RefreshLive();
                }
                else
                {
                    RefreshPreview();
                }
            }
        }

        private void StartGenerate()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already generating...");
                return;
            }

            if (!_hasTile || _targetGraphic == 0 || _targetObj == null || _targetObj.IsDestroyed)
            {
                Say("Select a static object first.");
                return;
            }

            // Double-generate guard: the victim is already hidden behind a
            // live replacement, so refuse a second run over it.
            if (!_targetObj.AllowedToDraw)
            {
                Say("Already replaced.");
                return;
            }

            // Any world object with artwork goes: statics, land, items and
            // multis all resolve through the art archive; mobiles and empty
            // slots come back pixel-less and are refused below, not here.
            bool isLand = _targetObj is Land;
            ComfyClient.ArtShot shot;
            try
            {
                shot = ComfyClient.ExtractArtShot(_targetGraphic, isLand);
            }
            catch (Exception ex)
            {
                Say($"Artwork unreadable: {ex.Message}");
                return;
            }

            if (shot == null)
            {
                Say("No artwork pixels for that graphic.");
                return;
            }

            _artWidth = shot.TightWidth;
            _artW = shot.Width;
            _artH = shot.Height;
            _tightX0 = shot.TightX0;
            _tightY0 = shot.TightY0;
            _tightW = shot.TightWidth;
            _tightH = shot.TightHeight;
            byte[] png = shot.Png;

            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            _genStatus = $"Running MoGe on 0x{_targetGraphic:X4} artwork (minutes on GPU)...";
            _shownStatus = "";
            Say(_genStatus);
            _beforePic?.SetPng(shot.Png);
            // The After cell stays empty: MoGe returns PLY bytes only (no
            // 2D preview PNG like the Comfy flow), so there is no result
            // image to show; the 3D result itself goes live in the world
            // on completion, not in the gump.
            _afterPic?.SetPng(null);
            // MogeToPlyAsync already returns a hot worker task (it owns the
            // Task.Run around the child process), so assign it directly: the
            // Update poll above is the same select-then-generate shape as
            // SplatPlacerGump, which wraps a cold ComfyClient call instead.
            _genTask = MogeClient.MogeToPlyAsync(png, s => _genStatus = s, ct);
        }

        private int _artWidth;
        private int _artW, _artH, _tightX0, _tightY0, _tightW, _tightH;
        private Assets.SplatSet _genBaseSet;

        private void OnGenerated(byte[] ply)
        {
            if (IsDisposed || ply == null || ply.Length == 0)
            {
                Say("Empty result.");
                return;
            }

            _genCount++;
            string name = $"moge_{_targetGraphic:X4}_{_genCount:D2}";
            Assets.SplatSet set;
            try
            {
                set = Assets.SplatPlyParser.Parse(ply, name);
            }
            catch (Exception ex)
            {
                Say($"Unparsable PLY: {ex.Message}");
                return;
            }

            if (set.Gaussians.Length == 0)
            {
                Say("No gaussians in result.");
                return;
            }

            _genBaseSet = set;
            _lastGenName = name;
            // Auto-fit: frame the replacement like the original's own
            // artwork instead of inheriting whatever scale was dialled in.
            // Projected model width at scale 1, over the lod0 bounds.
            double yw = _yaw * Math.PI / 180.0;
            float cyw = (float)Math.Cos(yw), syw = (float)Math.Sin(yw);
            Godot.Vector3 lo = set.BoundsMin, hi = set.BoundsMax;
            float x0 = float.MaxValue, x1 = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Renderer.SplatBatcher.IsoPointW(1f, cyw, syw,
                    (c & 1) == 0 ? lo.X : hi.X,
                    (c & 2) == 0 ? lo.Y : hi.Y,
                    (c & 4) == 0 ? lo.Z : hi.Z,
                    out float px, out _);
                if (px < x0)
                {
                    x0 = px;
                }

                if (px > x1)
                {
                    x1 = px;
                }
            }

            if (_artWidth > 0 && x1 - x0 > 0.001f)
            {
                _scale = Math.Max(2f, Math.Min(160f, (float)Math.Round(_artWidth / (x1 - x0))));
                UpdateLabels();
            }
            // Anchor: shift the bbox-center to the tile anchor (RSP). A PLY
            // cannot carry the shift (the parser recenters on bbox), so the
            // live chain holds this translated copy while _genBaseSet stays
            // centered for yaw/scale retunes; _placed keeps the raw bytes
            // for save (GameScene re-anchors staged moge_* on load).
            AnchorOffset(out float adx, out float ady);
            Renderer.SplatBatcher.AnchorShift(adx, ady, _scale, _yaw,
                out float amx, out float amy, out float amz);
            var moved = new Assets.SplatGaussian[set.Gaussians.Length];
            for (int i = 0; i < moved.Length; i++)
            {
                Assets.SplatGaussian g = set.Gaussians[i];
                g.X += amx;
                g.Y += amy;
                g.Z += amz;
                moved[i] = g;
            }

            var placedSet = new Assets.SplatSet
            {
                Gaussians = moved,
                BoundsMin = set.BoundsMin + new Godot.Vector3(amx, amy, amz),
                BoundsMax = set.BoundsMax + new Godot.Vector3(amx, amy, amz),
                OpacityMass = set.OpacityMass,
            };
            var chain = new SplatLodChain
            {
                Levels = new[] { placedSet },
                BoundsMin = placedSet.BoundsMin,
                BoundsMax = placedSet.BoundsMax,
            };
            SplatStage.Register(name, chain, _scale);
            GameScene scene = Scene;
            _liveKey = $"moge:{name}";
            if (scene == null || !scene.PlaceSplat(_liveKey, name, _tx, _ty, _tz, _scale, _yaw, _disc, _targetGraphic))
            {
                Say("Place failed.");
                return;
            }

            // Retire any preview overlay so only the committed splat draws.
            scene.RemoveSplat(PreviewKey);

            // Bind the hide to the live victim instance, not the possibly
            // stale Select snapshot (the GPU run takes minutes; chunk
            // re-streams replace the instance). The hide anchors to the
            // victim's own tile+graphic and survives later moves of the
            // SPLAT; only Restore reveals it.
            GameObject victim = ResolveVictim();
            bool hidden = victim != null && !victim.IsDestroyed;
            if (hidden)
            {
                victim.AllowedToDraw = false;
                scene?.HideSingle(victim, _liveKey);
                _targetObj = victim;
            }

            _placed.Add(new PlacedEntry
            {
                Name = name,
                At = new SplatPlacement
                {
                    Name = name,
                    Facet = World.MapIndex,
                    X = _tx,
                    Y = _ty,
                    Z = _tz,
                    Scale = _scale,
                    Yaw = _yaw,
                    DiscSize = _disc,
                    LockZ = true,
                    VictimGraphic = _targetGraphic,
                },
                Lods = new List<byte[]> { ply },
            });
            string msg = $"{name} replaced 0x{_targetGraphic:X4} ({set.Gaussians.Length} gaussians)";
            if (hidden)
            {
                if (_targetObj.X != _tx || _targetObj.Y != _ty || _targetObj.Z != _tz)
                {
                    msg += $" at {_tx}, {_ty}, {_tz}; victim hidden at {_targetObj.X}, {_targetObj.Y}, {_targetObj.Z}";
                }
                else
                {
                    msg += $" at {_tx}, {_ty}, {_tz} (committed, original hidden)";
                }
            }
            else
            {
                msg += $" at {_tx}, {_ty}, {_tz} (committed, victim not found: nothing hidden)";
            }

            Say(msg + ". Save to keep.");
            GameActions.Print(World, "MogePlacer: " + msg);
            // Single runs populate the graphic cache too, so a later Auto
            // Queue reuses them without another GPU run. The single path
            // itself never skips: it always regenerates.
            MogeClient.SaveCachedPly(_targetGraphic, ply);
            UpdateLabels();
        }

        /// <summary>
        /// Bulk MoGe: every static within 17 tiles, deduped to unique
        /// graphics. Cached graphics place immediately from
        /// build/moge/moge_0xXXXX.ply; the rest generate sequentially on the
        /// same one-Task worker pattern as single Generate (Close cancels).
        /// Each victim keeps its own tile; scale auto-fits per graphic from
        /// the Auto-click snapshot of Scale/Yaw/Disc. Save persists the whole
        /// set (placements + hides) to splats.json for relog restore.
        /// </summary>
        private void StartAutoQueue()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already generating...");
                return;
            }

            if (_autoActive)
            {
                Say("Auto queue running...");
                return;
            }

            if (World.Player == null || World.Map == null)
            {
                Say("No world.");
                return;
            }

            float baseScale = _scale;
            _autoBaseScale = baseScale;
            _autoYaw = _yaw;
            _autoDisc = _disc;

            var groups = new Dictionary<ushort, List<AutoVictim>>();
            int px = World.Player.X;
            int py = World.Player.Y;
            for (int dy = -17; dy <= 17; dy++)
            {
                for (int dx = -17; dx <= 17; dx++)
                {
                    int tx = px + dx;
                    int ty = py + dy;
                    if (tx < 0 || ty < 0)
                    {
                        continue;
                    }

                    GameObject head = null;
                    try
                    {
                        head = World.Map.GetTile(tx, ty, false);
                    }
                    catch
                    {
                        continue;
                    }

                    for (GameObject o = head; o != null; o = o.TNext)
                    {
                        if (o is not Static s)
                        {
                            continue;
                        }

                        if (s.IsDestroyed || !s.AllowedToDraw || s.Graphic == 0)
                        {
                            continue;
                        }

                        if (!groups.TryGetValue(s.Graphic, out List<AutoVictim> list))
                        {
                            list = new List<AutoVictim>();
                            groups[s.Graphic] = list;
                        }

                        bool dup = false;
                        foreach (AutoVictim v in list)
                        {
                            if (v.X == s.X && v.Y == s.Y && v.Z == s.Z)
                            {
                                dup = true;
                                break;
                            }
                        }

                        if (!dup)
                        {
                            list.Add(new AutoVictim { X = s.X, Y = s.Y, Z = s.Z, Obj = s });
                        }
                    }
                }
            }

            if (groups.Count == 0)
            {
                Say("No statics within 17 tiles.");
                return;
            }

            var sorted = new List<ushort>(groups.Keys);
            sorted.Sort();
            _autoPending.Clear();
            int cached = 0;
            int nogfx = 0;
            foreach (ushort g in sorted)
            {
                List<AutoVictim> victims = groups[g];
                ComfyClient.ArtShot shot = null;
                try
                {
                    shot = ComfyClient.ExtractArtShot(g, false);
                }
                catch
                {
                    nogfx++;
                    continue;
                }

                if (shot == null || shot.Png == null || shot.TightWidth <= 0)
                {
                    nogfx++;
                    continue;
                }

                if (MogeClient.TryGetCachedPly(g, out byte[] cachedPly)
                    && cachedPly != null && cachedPly.Length > 0)
                {
                    Assets.SplatSet testSet = null;
                    try
                    {
                        testSet = Assets.SplatPlyParser.Parse(cachedPly, $"moge_{g:X4}_cache");
                    }
                    catch
                    {
                        testSet = null;
                    }

                    if (testSet != null && testSet.Gaussians.Length > 0)
                    {
                        PlaceAutoGroup(g, cachedPly, testSet,
                            shot.Width, shot.Height, shot.TightX0, shot.TightY0,
                            shot.TightWidth, shot.TightHeight, shot.TightWidth, victims);
                        cached++;
                        continue;
                    }
                }

                _autoPending.Add(new AutoJob
                {
                    Graphic = g,
                    Victims = victims,
                    Png = shot.Png,
                    ArtW = shot.Width,
                    ArtH = shot.Height,
                    TightX0 = shot.TightX0,
                    TightY0 = shot.TightY0,
                    TightW = shot.TightWidth,
                    TightH = shot.TightHeight,
                    ArtWidth = shot.TightWidth,
                });
            }

            int queued = _autoPending.Count;
            GameActions.Print(World,
                $"MogePlacer auto: {groups.Count} graphic(s), {cached} cached, {queued} to generate"
                + (nogfx > 0 ? $", {nogfx} without artwork" : ""));
            if (queued == 0)
            {
                Say(cached > 0
                    ? $"Auto done: {cached} cached, nothing to generate. Save to keep."
                    : "Nothing with artwork to queue.");
                UpdateLabels();
                return;
            }

            _autoTotal = queued;
            _autoDone = 0;
            _autoActive = true;
            _genCts = new CancellationTokenSource();
            StartNextAuto();
        }

        private void StartNextAuto()
        {
            if (!_autoActive || _autoPending.Count == 0 || IsDisposed)
            {
                _autoActive = false;
                return;
            }

            AutoJob job = _autoPending[0];
            _autoPending.RemoveAt(0);
            _autoCurrent = job;
            _genStatus = $"Auto {_autoDone + 1}/{_autoTotal} 0x{job.Graphic:X4} ...";
            _shownStatus = "";
            Say(_genStatus);
            CancellationToken ct = _genCts.Token;
            int done = _autoDone;
            int total = _autoTotal;
            ushort graphic = job.Graphic;
            _genTask = MogeClient.MogeToPlyAsync(
                job.Png, s => _genStatus = $"Auto {done + 1}/{total} 0x{graphic:X4}: {s}", ct);
        }

        private void ContinueAutoQueue()
        {
            if (!_autoActive || IsDisposed)
            {
                _autoActive = false;
                return;
            }

            if (_genCts != null && _genCts.IsCancellationRequested)
            {
                _autoActive = false;
                _autoPending.Clear();
                Say("Auto cancelled.");
                return;
            }

            if (_autoPending.Count > 0)
            {
                StartNextAuto();
            }
            else
            {
                _autoActive = false;
                Say($"Auto done: {_autoDone}/{_autoTotal} generated. Save to keep.");
                GameActions.Print(World,
                    $"MogePlacer auto done: {_autoDone}/{_autoTotal} generated.");
            }
        }

        private void OnAutoGenerated(AutoJob job, byte[] ply)
        {
            if (IsDisposed || ply == null || ply.Length == 0)
            {
                Say($"Auto 0x{job.Graphic:X4}: empty result.");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            Assets.SplatSet set;
            try
            {
                set = Assets.SplatPlyParser.Parse(ply, $"moge_{job.Graphic:X4}");
            }
            catch (Exception ex)
            {
                Say($"Auto 0x{job.Graphic:X4}: unparsable PLY: {ex.Message}");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            if (set.Gaussians.Length == 0)
            {
                Say($"Auto 0x{job.Graphic:X4}: no gaussians.");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            MogeClient.SaveCachedPly(job.Graphic, ply);
            PlaceAutoGroup(job.Graphic, ply, set,
                job.ArtW, job.ArtH, job.TightX0, job.TightY0,
                job.TightW, job.TightH, job.ArtWidth, job.Victims);
            _autoDone++;
            ContinueAutoQueue();
        }

        /// <summary>
        /// One graphic's PLY onto every victim's own tile: auto-fit the scale
        /// from the art width over the lod0 projected width (same math as
        /// single OnGenerated), anchor the bbox-center to the tile, then one
        /// staged name + live key per victim so Save writes each placement.
        /// </summary>
        private void PlaceAutoGroup(ushort graphic, byte[] ply, Assets.SplatSet baseSet,
            int artW, int artH, int tightX0, int tightY0, int tightW, int tightH,
            int artWidth, List<AutoVictim> victims)
        {
            double yw = _autoYaw * Math.PI / 180.0;
            float cyw = (float)Math.Cos(yw), syw = (float)Math.Sin(yw);
            Godot.Vector3 lo = baseSet.BoundsMin, hi = baseSet.BoundsMax;
            float x0 = float.MaxValue, x1 = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Renderer.SplatBatcher.IsoPointW(1f, cyw, syw,
                    (c & 1) == 0 ? lo.X : hi.X,
                    (c & 2) == 0 ? lo.Y : hi.Y,
                    (c & 4) == 0 ? lo.Z : hi.Z,
                    out float px, out _);
                if (px < x0)
                {
                    x0 = px;
                }

                if (px > x1)
                {
                    x1 = px;
                }
            }

            float fitted = _autoBaseScale > 0f ? _autoBaseScale : _scale;
            if (artWidth > 0 && x1 - x0 > 0.001f)
            {
                fitted = Math.Max(2f, Math.Min(160f, (float)Math.Round(artWidth / (x1 - x0))));
            }

            _scale = fitted;
            float cx = tightX0 + tightW * 0.5f;
            float cy = tightY0 + tightH * 0.5f;
            float dx = cx - (artW * 0.5f - 22f);
            float dy = cy - (artH - 44f);
            Renderer.SplatBatcher.AnchorShift(dx, dy, fitted, _autoYaw,
                out float amx, out float amy, out float amz);
            GameScene scene = Scene;
            int placed = 0;
            foreach (AutoVictim v in victims)
            {
                GameObject victimObj = v.Obj;
                if (victimObj == null || victimObj.IsDestroyed)
                {
                    victimObj = FindDrawableAt(v.X, v.Y, v.Z, graphic);
                }
                else if (!victimObj.AllowedToDraw)
                {
                    continue;
                }

                _genCount++;
                string name = $"moge_{graphic:X4}_{_genCount:D2}";
                var moved = new Assets.SplatGaussian[baseSet.Gaussians.Length];
                for (int i = 0; i < moved.Length; i++)
                {
                    Assets.SplatGaussian g = baseSet.Gaussians[i];
                    g.X += amx;
                    g.Y += amy;
                    g.Z += amz;
                    moved[i] = g;
                }

                var placedSet = new Assets.SplatSet
                {
                    Gaussians = moved,
                    BoundsMin = baseSet.BoundsMin + new Godot.Vector3(amx, amy, amz),
                    BoundsMax = baseSet.BoundsMax + new Godot.Vector3(amx, amy, amz),
                    OpacityMass = baseSet.OpacityMass,
                };
                var chain = new SplatLodChain
                {
                    Levels = new[] { placedSet },
                    BoundsMin = placedSet.BoundsMin,
                    BoundsMax = placedSet.BoundsMax,
                };
                SplatStage.Register(name, chain, fitted);
                string liveKey = $"moge:{name}";
                if (scene == null || !scene.PlaceSplat(liveKey, name, v.X, v.Y, v.Z, fitted, _autoYaw, _autoDisc, graphic))
                {
                    continue;
                }

                if (victimObj != null && !victimObj.IsDestroyed && victimObj.AllowedToDraw)
                {
                    victimObj.AllowedToDraw = false;
                    scene?.HideSingle(victimObj, liveKey);
                }

                _placed.Add(new PlacedEntry
                {
                    Name = name,
                    At = new SplatPlacement
                    {
                        Name = name,
                        Facet = World.MapIndex,
                        X = v.X,
                        Y = v.Y,
                        Z = v.Z,
                        Scale = fitted,
                        Yaw = _autoYaw,
                        DiscSize = _autoDisc,
                        LockZ = true,
                        VictimGraphic = graphic,
                    },
                    Lods = new List<byte[]> { ply },
                });
                _autoKeys.Add(liveKey);
                placed++;
            }

            string msg = $"moge 0x{graphic:X4} x{placed} ({baseSet.Gaussians.Length} gaussians)";
            GameActions.Print(World, "MogePlacer auto: " + msg);
            if (_autoActive && _autoTotal > 0)
            {
                Say($"Auto {_autoDone + 1}/{_autoTotal} 0x{graphic:X4}: {placed} placed.");
            }
            else if (placed > 0)
            {
                Say($"{msg}. Save to keep.");
            }

            UpdateLabels();
        }

        private GameObject FindDrawableAt(int x, int y, int z, ushort graphic)
        {
            GameObject fallback = null;
            try
            {
                for (GameObject o = World.Map.GetTile(x, y, false); o != null; o = o.TNext)
                {
                    if (o is Static s && !s.IsDestroyed && s.AllowedToDraw && s.Graphic == graphic)
                    {
                        // Same-graphic statics can stack at one tile at
                        // different Z: prefer the victim's own level so the
                        // hide binds the right instance, not its neighbour.
                        if (s.Z == z)
                        {
                            return s;
                        }

                        if (fallback == null)
                        {
                            fallback = s;
                        }
                    }
                }
            }
            catch
            {
            }

            return fallback;
        }

        /// <summary>
        /// Unhides every auto-queued victim and removes their live splats;
        /// single-target state (_liveKey, preview) is left alone.
        /// </summary>
        private void ClearAuto()
        {
            _autoActive = false;
            _autoPending.Clear();
            if (_autoCurrent != null)
            {
                // Keep _autoCurrent so the Update poll reports the in-flight
                // run as auto-cancelled instead of a single cancel; it nulls
                // the field when the task lands.
                try
                {
                    _genCts?.Cancel();
                }
                catch
                {
                }
            }

            int n = 0;
            GameScene scene = Scene;
            if (scene != null)
            {
                foreach (string k in new List<string>(_autoKeys))
                {
                    scene.RemoveSplat(k);
                    scene.ClearSingleHide(k);
                    n++;
                }
            }
            else
            {
                n = _autoKeys.Count;
            }

            if (_autoKeys.Count > 0)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (string k in _autoKeys)
                {
                    int c = k.IndexOf(':');
                    names.Add(c >= 0 ? k.Substring(c + 1) : k);
                }

                _placed.RemoveAll(e => e != null && names.Contains(e.Name));
                _autoKeys.Clear();
            }

            Say(n > 0 ? $"Cleared {n} auto placement(s)." : "Nothing to clear.");
        }
    }
}
