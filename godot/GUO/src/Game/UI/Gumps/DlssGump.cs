// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.Comfy;
using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// In-game DLSS bake, cloned from the repaint panel: select a static,
    /// run its artwork through the local DLSS 5 network (still fp16 or
    /// temporal fp32 with zero motion), preview Before/After side by side,
    /// and bake the result over the world. Opened with the "-dlss" client
    /// command.
    ///
    /// Nothing is ever written to the UO install (or any .mul): results land
    /// as PNGs in the override folder (GUO_ART_OVERRIDE, else
    /// %APPDATA%/GUO/overrides), overrides/DLSS/Statics/&lt;id&gt;.png plus
    /// a manifest.json row (backend dlss5-still/temporal, intensity,
    /// profile), which the loader prefers and rescans live. Revert deletes
    /// the file. The bake is a detail filter at the native footprint (the
    /// 320 network tile is folded back to native size and masked to the
    /// original silhouette), so the Art resolver chains it last: HD wins,
    /// then SR, then DLSS. The DlssBake toggle (Options/Video) gates the
    /// resolver; the Dlss5 frame toggle's full-viewport composite is separate
    /// (Dlss5Frame V3) and unaffected by this bake toggle either way.
    /// </summary>
    internal sealed class DlssGump : Gump
    {
        private const int W = 320;
        private const int H = 460;
        private const int Tile = GUO.Dlss5.Dlss5Service.WorkSize;

        private static readonly string[] Backends = { "Still (fp16)", "Temporal (fp32)" };
        private static readonly string[] Profiles = { "Standard", "Natural", "Cinematic", "Neutral" };

        private readonly Label _graphicLabel;
        private readonly Label _intensityLabel;
        private readonly Combobox _backendPick;
        private readonly Combobox _profilePick;
        private readonly HSliderBar _intensitySlider;
        private readonly PreviewPic _beforePic;
        private readonly PreviewPic _afterPic;
        private readonly Label _statusLabel;

        private ushort _targetGraphic;
        private GameObject _targetObj;
        private Task<SingleResult> _genTask;
        private string _genStatus = "";
        private string _shownStatus = "";
        private CancellationTokenSource _genCts;

        /// <summary>
        /// One baked-static job: the native pixels plus the 320 network
        /// square built from them on the main thread (the .mul/.uop readers
        /// and Godot Image builds stay off workers; inference runs on the
        /// worker, like the Dlss5Frame warm path).
        /// </summary>
        private sealed class AutoJob
        {
            public ushort Graphic;
            public byte[] Rgba;
            public int Width;
            public int Height;
            public Godot.Image Square;
            public float Intensity;
            public int ProfileIdx;
            public bool Temporal;
        }

        /// <summary>
        /// One finished enhance, handed from the worker to Update() for the
        /// main-thread commit (Image rebuild, mask, PNG encode and override
        /// files stay on the main thread). EnhancedRgb null means the item
        /// failed; Error carries why.
        /// </summary>
        private sealed class AutoBatchResult
        {
            public ushort Graphic;
            public byte[] Rgba;
            public int Width;
            public int Height;
            public byte[] EnhancedRgb;
            public string Error = "";
            public float Intensity;
            public int ProfileIdx;
            public bool Temporal;
        }

        /// <summary>
        /// One single-target enhance (the Generate button's worker). Same
        /// handoff as the batch: raw network bytes out, bake on the main
        /// thread in <see cref="OnBaked"/>.
        /// </summary>
        private sealed class SingleResult
        {
            public ushort Graphic;
            public byte[] Rgba;
            public int Width;
            public int Height;
            public byte[] Png;
            public byte[] EnhancedRgb;
            public string Error = "";
            public float Intensity;
            public int ProfileIdx;
            public bool Temporal;
        }

        private Task _autoRunner;
        private readonly ConcurrentQueue<AutoBatchResult> _autoResults = new ConcurrentQueue<AutoBatchResult>();
        private readonly HashSet<ushort> _autoGraphics = new HashSet<ushort>();
        private int _autoTotal;
        private int _autoDone;
        private int _autoSubmitted;
        private bool _autoActive;
        private string _autoRunnerError = "";

        private enum Buttons
        {
            Select,
            Generate,
            Revert,
            AutoQueue,
            ClearAll,
            Close,
        }

        public DlssGump(World world)
            : base(world, 140, 120)
        {
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("DLSS Bake", true, 0x0481) { X = 12, Y = 8 });

            var close = new NiceButton(W - 30, 6, 22, 20, ButtonAction.Activate, "X")
            {
                ButtonParameter = (int)Buttons.Close,
                IsSelectable = false
            };
            Add(close);

            int y = 34;
            Add(new Label("Graphic:", true, 0x0386) { X = 12, Y = y });
            _graphicLabel = new Label("<none>", true, 0x0021) { X = 100, Y = y };
            Add(_graphicLabel);
            Add(new NiceButton(190, y - 2, 98, 22, ButtonAction.Activate, "Select")
            {
                ButtonParameter = (int)Buttons.Select,
                IsSelectable = false
            });

            y += 30;
            Add(new Label("Before:", true, 0x0386) { X = 12, Y = y });
            Add(new Label("After:", true, 0x0386) { X = 156, Y = y });
            y += 18;
            _beforePic = new PreviewPic { X = 12, Y = y, Width = 132, Height = 132 };
            Add(_beforePic);
            _afterPic = new PreviewPic { X = 156, Y = y, Width = 132, Height = 132 };
            Add(_afterPic);
            y += 140;

            y += 8;
            Add(new Label("Backend:", true, 0x0386) { X = 12, Y = y });
            _backendPick = new Combobox(100, y - 2, 188, Backends, 0);
            Add(_backendPick);

            y += 30;
            Add(new Label("Profile:", true, 0x0386) { X = 12, Y = y });
            Profile profile0 = ProfileManager.CurrentProfile;
            int profileIdx = profile0 != null ? Math.Max(0, Math.Min(Profiles.Length - 1, profile0.DlssBakeProfile)) : 0;
            _profilePick = new Combobox(100, y - 2, 188, Profiles, profileIdx);
            _profilePick.OnOptionSelected += (s, i) =>
            {
                Profile p = ProfileManager.CurrentProfile;
                if (p != null)
                {
                    p.DlssBakeProfile = Math.Max(0, Math.Min(Profiles.Length - 1, i));
                }
            };
            Add(_profilePick);

            y += 30;
            _intensityLabel = new Label("Intensity:", true, 0x0386) { X = 12, Y = y };
            Add(_intensityLabel);
            Profile profile1 = ProfileManager.CurrentProfile;
            int intensity = profile1 != null ? (int)Math.Round(profile1.DlssBakeIntensity * 100f) : 100;
            intensity = Math.Max(1, Math.Min(100, intensity));
            _intensitySlider = new HSliderBar(100, y, 188, 0, 100, intensity,
                HSliderBarStyle.MetalWidgetRecessedBar, hasText: true, font: 1, color: 0x0386, unicode: true);
            _intensitySlider.ValueChanged += (s, e) =>
            {
                Profile p = ProfileManager.CurrentProfile;
                if (p != null)
                {
                    p.DlssBakeIntensity = _intensitySlider.Value / 100f;
                }
            };
            Add(_intensitySlider);

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Generate")
            {
                ButtonParameter = (int)Buttons.Generate,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Revert")
            {
                ButtonParameter = (int)Buttons.Revert,
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
        }

        private void UpdateLabels()
        {
            _graphicLabel.Text = _targetGraphic == 0
                ? "<none> (Select first)"
                : $"0x{_targetGraphic:X4} ({_targetGraphic})";
        }

        private void Say(string s)
        {
            _statusLabel.Text = s;
        }

        private float Intensity => _intensitySlider != null ? _intensitySlider.Value / 100f : 1.0f;
        private int ProfileIdx => _profilePick != null ? Math.Max(0, Math.Min(Profiles.Length - 1, _profilePick.SelectedIndex)) : 0;
        private bool Temporal => _backendPick != null && _backendPick.SelectedIndex == 1;

        /// <summary>
        /// Native RGBA (as <see cref="ComfyClient.ExtractArtShot"/> builds
        /// it) padded to a square and folded through the screenshot crop
        /// path (<see cref="GUO.Dlss5.Dlss5Service.PrepareSquare320"/>):
        /// already square, so the crop keeps the whole sprite and only the
        /// resize + RGB8 conversion bite. Main thread (Godot Image build).
        /// Null when the sprite has no size.
        /// </summary>
        private static Godot.Image BuildSquare320(byte[] rgba, int w, int h)
        {
            if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4)
            {
                return null;
            }

            try
            {
                int side = Math.Max(w, h);
                int ox = (side - w) / 2;
                int oy = (side - h) / 2;
                byte[] canvas = new byte[side * side * 4];
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(rgba, y * w * 4, canvas, ((oy + y) * side + ox) * 4, w * 4);
                }

                using Godot.Image native = Godot.Image.CreateFromData(
                    side, side, false, Godot.Image.Format.Rgba8, canvas);
                if (native == null)
                {
                    return null;
                }

                return GUO.Dlss5.Dlss5Service.PrepareSquare320(native);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Folds one 320 network tile back to the native footprint: resize
        /// to the padded square, crop the centered original rect, re-apply
        /// the native alpha (transparent stays transparent). Main thread
        /// (Image rebuild + PNG encode). Null when the frame is short.
        /// </summary>
        private static byte[] BakeDlssPng(byte[] origRgba, int w, int h, byte[] enhRgb)
        {
            if (origRgba == null || enhRgb == null || w <= 0 || h <= 0)
            {
                return null;
            }

            if (enhRgb.Length < Tile * Tile * 3)
            {
                return null;
            }

            try
            {
                int side = Math.Max(w, h);
                int ox = (side - w) / 2;
                int oy = (side - h) / 2;
                byte[] sideRgb;
                using (Godot.Image enh = Godot.Image.CreateFromData(
                    Tile, Tile, false, Godot.Image.Format.Rgb8, enhRgb))
                {
                    if (enh == null)
                    {
                        return null;
                    }

                    enh.Resize(side, side, Godot.Image.Interpolation.Bilinear);
                    sideRgb = enh.GetData();
                }

                if (sideRgb == null || sideRgb.Length < side * side * 3)
                {
                    return null;
                }

                byte[] rgba = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int sx = ox + x;
                        int sy = oy + y;
                        int sAt = (sy * side + sx) * 3;
                        int dAt = (y * w + x) * 4;
                        rgba[dAt] = sideRgb[sAt];
                        rgba[dAt + 1] = sideRgb[sAt + 1];
                        rgba[dAt + 2] = sideRgb[sAt + 2];
                        rgba[dAt + 3] = origRgba[dAt + 3];
                    }
                }

                using Godot.Image baked = Godot.Image.CreateFromData(
                    w, h, false, Godot.Image.Format.Rgba8, rgba);
                if (baked == null)
                {
                    return null;
                }

                return baked.SavePngToBuffer();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One network pass on the worker (inference + model load stay off
        /// the frame; the caller owns the square and it is disposed here).
        /// Returns the raw tile bytes; null means Error carries why. Never
        /// throws except on cancellation.
        /// </summary>
        private static byte[] EnhanceOne(Godot.Image square, float intensity, int profileIdx, bool temporal, CancellationToken ct)
        {
            if (square == null)
            {
                return null;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var svc = GUO.Dlss5.Dlss5Service.Shared;
                Godot.Image enhanced = temporal
                    ? svc.EnhanceTemporal(square, GUO.Dlss5.Dlss5Service.ZeroMotion320(), intensity, profileIdx)
                    : svc.EnhanceStill(square, intensity, profileIdx);
                try
                {
                    square.Dispose();
                }
                catch
                {
                }

                if (ct.IsCancellationRequested)
                {
                    try { enhanced?.Dispose(); } catch { }
                    ct.ThrowIfCancellationRequested();
                }

                if (enhanced == null)
                {
                    return null;
                }

                byte[] rgb;
                try
                {
                    rgb = enhanced.GetData();
                }
                finally
                {
                    try { enhanced.Dispose(); } catch { }
                }

                return rgb;
            }
            catch (OperationCanceledException)
            {
                try { square.Dispose(); } catch { }
                throw;
            }
            catch
            {
                try { square.Dispose(); } catch { }
                return null;
            }
        }

        private static string BackendName(bool temporal) => temporal ? "dlss5-temporal" : "dlss5-still";

        public override void Update()
        {
            base.Update();
            if (IsDisposed)
            {
                return;
            }

            if (_intensitySlider != null && _intensityLabel != null)
            {
                string t = $"Intensity: {_intensitySlider.Value / 100f:0.00}";
                if (_intensityLabel.Text != t)
                {
                    _intensityLabel.Text = t;
                }
            }

            if (_genTask != null && _genTask.IsCompleted)
            {
                Task<SingleResult> done = _genTask;
                _genTask = null;
                if (done.IsFaulted)
                {
                    Say($"Bake failed: {done.Exception?.GetBaseException().Message}");
                }
                else if (done.IsCanceled)
                {
                    Say("Bake cancelled.");
                }
                else
                {
                    OnBaked(done.Result);
                }
            }
            else if (_genTask != null && _genStatus != _shownStatus)
            {
                _shownStatus = _genStatus;
                Say(_genStatus);
            }

            DrainAutoBatch();
        }

        public override void OnButtonClick(int buttonID)
        {
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
                            if (obj != null)
                            {
                                // Statics only (house multis share static art
                                // ids, so their parts count): land and loose
                                // items resolve elsewhere.
                                if (obj is Static || obj is Multi)
                                {
                                    if (obj.IsDestroyed || !obj.AllowedToDraw || obj.Graphic == 0)
                                    {
                                        GameActions.Print(World, "Dlss: that object has no artwork.");
                                        Say("That object has no artwork.");
                                        obj = null;
                                    }
                                }
                                else
                                {
                                    GameActions.Print(World, obj is Land
                                        ? $"Dlss: no static at {obj.X}, {obj.Y} (land picked)."
                                        : "Dlss: pick a static object.");
                                    Say(obj is Land
                                        ? "That tile has no static; pick an object."
                                        : "Pick a static object.");
                                    obj = null;
                                }

                                if (obj != null)
                                {
                                    _targetGraphic = obj.Graphic;
                                    _targetObj = obj;
                                    GameActions.Print(World,
                                        $"Dlss selected: 0x{_targetGraphic:X4} at {obj.X}, {obj.Y}");
                                    RefreshBeforePic();
                                }
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click the static to bake...");
                    break;

                case (int)Buttons.Generate:
                    StartGenerate();
                    break;

                case (int)Buttons.Revert:
                    Revert();
                    break;

                case (int)Buttons.AutoQueue:
                    StartAutoQueue();
                    break;

                case (int)Buttons.ClearAll:
                    ClearAuto();
                    break;

                case (int)Buttons.Close:
                    _autoActive = false;
                    _autoRunner = null;
                    while (_autoResults.TryDequeue(out _)) { }
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
                    png = ComfyClient.ExtractArtShot(_targetGraphic, false)?.Png;
                }
                catch
                {
                }
            }

            _beforePic?.SetPng(png);
        }

        private void StartGenerate()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already baking...");
                return;
            }

            if (_autoActive || (_autoRunner != null && !_autoRunner.IsCompleted))
            {
                Say("Auto queue running...");
                return;
            }

            if (_targetGraphic == 0 || _targetObj == null || _targetObj.IsDestroyed)
            {
                Say("Select a static first.");
                return;
            }

            ComfyClient.ArtShot shot;
            try
            {
                shot = ComfyClient.ExtractArtShot(_targetGraphic, false);
            }
            catch (Exception ex)
            {
                Say($"Artwork unreadable: {ex.Message}");
                return;
            }

            if (shot == null || shot.Png == null || shot.Width <= 0 || shot.Height <= 0)
            {
                Say("No artwork pixels for that graphic.");
                return;
            }

            float intensity = Intensity;
            if (intensity <= 0f || intensity > 1f)
            {
                Say("Intensity must be 1-100.");
                return;
            }

            int profileIdx = ProfileIdx;
            bool temporal = Temporal;

            Godot.Image square;
            try
            {
                square = BuildSquare320(shot.Rgba, shot.Width, shot.Height);
            }
            catch (Exception ex)
            {
                Say($"Artwork unreadable: {ex.Message}");
                return;
            }

            if (square == null)
            {
                Say("No artwork pixels for that graphic.");
                return;
            }

            ushort graphic = _targetGraphic;
            byte[] rgba = shot.Rgba;
            int w = shot.Width;
            int h = shot.Height;
            byte[] png = shot.Png;
            _beforePic?.SetPng(png);

            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            _genStatus = $"Enhancing 0x{graphic:X4} ({BackendName(temporal)})...";
            _shownStatus = "";
            Say(_genStatus);
            _genTask = Task.Run(() =>
            {
                string error = "";
                byte[] rgb = null;
                try
                {
                    rgb = EnhanceOne(square, intensity, profileIdx, temporal, ct);
                    if (rgb == null)
                    {
                        try { error = GUO.Dlss5.Dlss5Service.Shared.LastError; } catch { }
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "enhance returned nothing";
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                return new SingleResult
                {
                    Graphic = graphic, Rgba = rgba, Width = w, Height = h, Png = png,
                    EnhancedRgb = rgb, Error = error ?? "",
                    Intensity = intensity, ProfileIdx = profileIdx, Temporal = temporal,
                };
            }, ct);
        }

        private void OnBaked(SingleResult result)
        {
            if (IsDisposed || result == null)
            {
                Say("Empty result.");
                return;
            }

            if (result.EnhancedRgb == null)
            {
                Say($"0x{result.Graphic:X4} failed: {result.Error}");
                GameActions.Print(World, $"Dlss 0x{result.Graphic:X4} failed: {result.Error}");
                return;
            }

            byte[] baked = BakeDlssPng(result.Rgba, result.Width, result.Height, result.EnhancedRgb);
            if (baked == null || baked.Length == 0)
            {
                Say($"0x{result.Graphic:X4}: bake failed.");
                return;
            }

            _afterPic?.SetPng(baked);
            if (!WriteDlssOverride(result.Graphic, result.Width, result.Height,
                baked, result.Intensity, result.ProfileIdx, result.Temporal))
            {
                Say($"0x{result.Graphic:X4}: override failed.");
                return;
            }

            string msg = $"0x{result.Graphic:X4} baked ({BackendName(result.Temporal)} {result.Intensity:0.00} {Profiles[result.ProfileIdx]})";
            if (ProfileManager.CurrentProfile?.DlssBake != true)
            {
                msg += " (enable DLSS bake under Options/Video to see it)";
            }

            Say(msg + ".");
            GameActions.Print(World, "Dlss: " + msg);
        }

        /// <summary>
        /// Main-thread commit: override PNG + manifest row + live rescan,
        /// exactly like the repaint commit but under overrides/DLSS. False
        /// when the write itself failed.
        /// </summary>
        private bool WriteDlssOverride(ushort graphic, int natW, int natH, byte[] png,
            float intensity, int profileIdx, bool temporal)
        {
            try
            {
                string dir = Path.Combine(ArtLoader.DlssDir(), "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{graphic}.png"), png);

                var row = new ArtLoader.DlssManifestRow
                {
                    NativeW = natW,
                    NativeH = natH,
                    DlssW = natW,
                    DlssH = natH,
                    Scale = 1.0,
                    Backend = BackendName(temporal),
                    Intensity = intensity,
                    Profile = profileIdx,
                    Sha256 = ArtLoader.Sha256Hex(png),
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                };
                ArtLoader.WriteDlssManifestRow(false, graphic, row);

                Client.Game.UO.FileManager.Arts.RescanOverrides();
                Client.Game.UO.Arts.RefreshStatic(graphic);
                ThemeManager.Reapply(World);
                return true;
            }
            catch (Exception ex)
            {
                Say($"Override failed: {ex.Message}");
                return false;
            }
        }

        private void Revert()
        {
            if (_targetGraphic == 0)
            {
                Say("Select a static first.");
                return;
            }

            try
            {
                ArtLoader.DeleteDlss(false, _targetGraphic);
                Client.Game.UO.FileManager.Arts.RescanOverrides();
                Client.Game.UO.Arts.RefreshStatic(_targetGraphic);
                ThemeManager.Reapply(World);
                _afterPic?.SetPng(null);
                Say($"Reverted 0x{_targetGraphic:X4}.");
            }
            catch (Exception ex)
            {
                Say($"Revert failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Bulk bake: every static or multi part within 17 tiles, deduped
        /// to unique graphics. Graphics with an existing DLSS override are
        /// skipped; the rest build their 320 squares ALL up front, then the
        /// worker enhances each one individually and commits land in
        /// completion order via <see cref="_autoResults"/> (Close/Clear All
        /// cancels). Each completion writes its own override file
        /// immediately; Clear All deletes the auto-created files again.
        /// </summary>
        private void StartAutoQueue()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already baking...");
                return;
            }

            if (_autoActive || (_autoRunner != null && !_autoRunner.IsCompleted))
            {
                Say("Auto queue running...");
                return;
            }

            if (World.Player == null || World.Map == null)
            {
                Say("No world.");
                return;
            }

            float intensity = Intensity;
            if (intensity <= 0f || intensity > 1f)
            {
                Say("Intensity must be 1-100.");
                return;
            }

            int profileIdx = ProfileIdx;
            bool temporal = Temporal;

            var found = new HashSet<ushort>();
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
                        // House multis share static art ids (same DLSS/Statics
                        // override path): include their parts so Auto Queue
                        // next to a house finds its walls.
                        if (o is Static s)
                        {
                            if (s.IsDestroyed || !s.AllowedToDraw || s.Graphic == 0)
                            {
                                continue;
                            }

                            found.Add(s.Graphic);
                        }
                        else if (o is Multi m)
                        {
                            if (m.IsDestroyed || !m.AllowedToDraw || m.Graphic == 0)
                            {
                                continue;
                            }

                            found.Add(m.Graphic);
                        }
                    }
                }
            }

            if (found.Count == 0)
            {
                Say("No statics within 17 tiles.");
                return;
            }

            var sorted = new List<ushort>(found);
            sorted.Sort();
            var batch = new List<AutoJob>();
            int skipped = 0;
            int nogfx = 0;
            foreach (ushort g in sorted)
            {
                string dlssPath = ArtLoader.DlssPathFor(false, g);
                try
                {
                    if (File.Exists(dlssPath))
                    {
                        skipped++;
                        continue;
                    }
                }
                catch
                {
                }

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

                if (shot == null || shot.Png == null || shot.Width <= 0 || shot.Height <= 0 || shot.TightWidth <= 0)
                {
                    nogfx++;
                    continue;
                }

                Godot.Image square = null;
                try
                {
                    square = BuildSquare320(shot.Rgba, shot.Width, shot.Height);
                }
                catch
                {
                }

                if (square == null)
                {
                    nogfx++;
                    continue;
                }

                batch.Add(new AutoJob
                {
                    Graphic = g, Rgba = shot.Rgba, Width = shot.Width, Height = shot.Height,
                    Square = square, Intensity = intensity, ProfileIdx = profileIdx, Temporal = temporal,
                });
            }

            GameActions.Print(World,
                $"Dlss auto: {found.Count} graphic(s), {skipped} with overrides, {batch.Count} to bake"
                + (nogfx > 0 ? $", {nogfx} without artwork" : ""));
            if (batch.Count == 0)
            {
                foreach (AutoJob leaked in batch)
                {
                    try { leaked.Square?.Dispose(); } catch { }
                }

                Say(skipped > 0
                    ? $"Auto done: {skipped} already baked, nothing to bake."
                    : "Nothing with artwork to queue.");
                return;
            }

            _autoTotal = batch.Count;
            _autoDone = 0;
            _autoSubmitted = batch.Count;
            _autoRunnerError = "";
            while (_autoResults.TryDequeue(out _)) { }
            _autoRunner = null;
            _autoActive = true;
            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            Say($"Auto 0/{_autoTotal} enhancing ({BackendName(temporal)})...");
            _autoRunner = Task.Run(() => RunAutoBatchAsync(batch, ct), ct);
        }

        /// <summary>
        /// Submit-all-up-front worker: every 320 square is already built, so
        /// this enhances each one individually (the service serializes on
        /// its own lock) and hands the raw tile bytes to the main thread
        /// for the bake. One item's failure never aborts the batch; cancel
        /// stops the sweep. Never throws except on cancellation.
        /// </summary>
        private async Task RunAutoBatchAsync(List<AutoJob> batch, CancellationToken ct)
        {
            await Task.Yield();
            foreach (AutoJob job in batch)
            {
                ct.ThrowIfCancellationRequested();
                string backend = BackendName(job.Temporal);
                _genStatus = $"Auto {_autoDone}/{_autoTotal} 0x{job.Graphic:X4} enhancing ({backend})...";
                byte[] rgb = null;
                string error = "";
                try
                {
                    rgb = EnhanceOne(job.Square, job.Intensity, job.ProfileIdx, job.Temporal, ct);
                    if (rgb == null)
                    {
                        try { error = GUO.Dlss5.Dlss5Service.Shared.LastError; } catch { }
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "enhance returned nothing";
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                _autoResults.Enqueue(new AutoBatchResult
                {
                    Graphic = job.Graphic, Rgba = job.Rgba, Width = job.Width, Height = job.Height,
                    EnhancedRgb = rgb, Error = error ?? "",
                    Intensity = job.Intensity, ProfileIdx = job.ProfileIdx, Temporal = job.Temporal,
                });
            }
        }

        /// <summary>
        /// Main-thread drain: bake each finished tile to its override + DLSS
        /// file as it arrives, keeping completed/total counts live. Runs
        /// inside Update(); never blocks.
        /// </summary>
        private void DrainAutoBatch()
        {
            if (!_autoActive && _autoRunner == null)
            {
                return;
            }

            if (IsDisposed)
            {
                return;
            }

            bool progressed = false;
            while (_autoResults.TryDequeue(out AutoBatchResult result))
            {
                progressed = true;
                CommitAutoBatchResult(result);
            }

            if (_genStatus != _shownStatus && _autoActive)
            {
                _shownStatus = _genStatus;
                if (_autoDone < _autoTotal)
                {
                    Say($"{_genStatus} ({_autoDone}/{_autoTotal} done)");
                }
            }

            Task runner = _autoRunner;
            if (runner != null && runner.IsCompleted)
            {
                if (runner.IsFaulted)
                {
                    _autoRunnerError = runner.Exception?.GetBaseException().Message ?? "auto run failed";
                }
                else if (runner.IsCanceled)
                {
                    if (_autoActive)
                    {
                        _autoActive = false;
                        _autoRunner = null;
                        Say("Auto cancelled.");
                        return;
                    }
                }

                // Runner finished but late results may still sit queued;
                // only close out once they drain and every item counted.
                if (_autoResults.IsEmpty && _autoDone >= _autoTotal)
                {
                    _autoActive = false;
                    _autoRunner = null;
                    if (!string.IsNullOrEmpty(_autoRunnerError) && _autoDone == 0)
                    {
                        Say($"Auto failed: {_autoRunnerError}");
                    }
                    else
                    {
                        Say(string.IsNullOrEmpty(_autoRunnerError)
                            ? $"Auto done: {_autoDone}/{_autoTotal} static baked."
                            : $"Auto done: {_autoDone}/{_autoTotal} ({_autoRunnerError}).");
                        GameActions.Print(World,
                            $"Dlss auto done: {_autoDone}/{_autoTotal} baked.");
                    }

                    _autoRunnerError = "";
                }
                else if (_autoResults.IsEmpty && runner.IsFaulted && _autoDone < _autoTotal)
                {
                    // Fatal sweep failure with nothing more coming:
                    // count the rest as failed so the run terminates.
                    _autoRunnerError = runner.Exception?.GetBaseException().Message ?? "auto run failed";
                    _autoActive = false;
                    _autoRunner = null;
                    Say($"Auto failed: {_autoRunnerError}");
                    _autoRunnerError = "";
                }
            }
            else if (progressed && _autoDone >= _autoTotal && _autoResults.IsEmpty)
            {
                _autoActive = false;
                _autoRunner = null;
                Say($"Auto done: {_autoDone}/{_autoTotal} static baked.");
                GameActions.Print(World,
                    $"Dlss auto done: {_autoDone}/{_autoTotal} baked.");
            }
        }

        /// <summary>
        /// Main-thread commit for one enhanced tile (or its per-item error):
        /// fold back to native, mask, override + manifest write, live
        /// rescan. Items land in completion order, each counted toward
        /// completed/total.
        /// </summary>
        private void CommitAutoBatchResult(AutoBatchResult result)
        {
            if (result == null)
            {
                _autoDone++;
                return;
            }

            ushort graphic = result.Graphic;
            if (string.IsNullOrEmpty(result.Error) && result.EnhancedRgb == null)
            {
                result.Error = "enhance returned nothing";
            }

            if (!string.IsNullOrEmpty(result.Error) || result.EnhancedRgb == null)
            {
                Say($"Auto 0x{graphic:X4} failed: {result.Error}");
                GameActions.Print(World, $"Dlss auto 0x{graphic:X4} failed: {result.Error}");
                _autoDone++;
                return;
            }

            byte[] baked = BakeDlssPng(result.Rgba, result.Width, result.Height, result.EnhancedRgb);
            if (baked == null || baked.Length == 0)
            {
                Say($"Auto 0x{graphic:X4}: bake failed.");
                _autoDone++;
                return;
            }

            try
            {
                string dir = Path.Combine(ArtLoader.DlssDir(), "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{graphic}.png"), baked);

                var row = new ArtLoader.DlssManifestRow
                {
                    NativeW = result.Width,
                    NativeH = result.Height,
                    DlssW = result.Width,
                    DlssH = result.Height,
                    Scale = 1.0,
                    Backend = BackendName(result.Temporal),
                    Intensity = result.Intensity,
                    Profile = result.ProfileIdx,
                    Sha256 = ArtLoader.Sha256Hex(baked),
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                };
                ArtLoader.WriteDlssManifestRow(false, graphic, row);

                Client.Game.UO.FileManager.Arts.RescanOverrides();
                Client.Game.UO.Arts.RefreshStatic(graphic);
                ThemeManager.Reapply(World);
            }
            catch (Exception ex)
            {
                Say($"Auto 0x{graphic:X4}: override failed: {ex.Message}");
                _autoDone++;
                return;
            }

            _autoGraphics.Add(graphic);
            GameActions.Print(World, $"Dlss auto: 0x{graphic:X4} baked ({BackendName(result.Temporal)})");
            _autoDone++;
            Say($"Auto {_autoDone}/{_autoTotal} 0x{graphic:X4}: done.");
        }

        /// <summary>
        /// Cancels any running bake and deletes the overrides this session's
        /// auto run created; single-target bakes are left alone unless they
        /// share a graphic (same file, same result).
        /// </summary>
        private void ClearAuto()
        {
            bool hadRun = _autoActive || _autoRunner != null;
            _autoActive = false;
            if (hadRun)
            {
                // Null the runner so DrainAutoBatch stops reporting; the
                // worker still exits on the CTS and its late results drain
                // harmlessly (IsDisposed / !_autoActive guard the commit).
                _autoRunner = null;
                while (_autoResults.TryDequeue(out _)) { }
                try
                {
                    _genCts?.Cancel();
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    _genCts?.Cancel();
                }
                catch
                {
                }
            }

            int n = 0;
            if (_autoGraphics.Count > 0)
            {
                var refreshed = new HashSet<ushort>();
                foreach (ushort g in new List<ushort>(_autoGraphics))
                {
                    try
                    {
                        string path = ArtLoader.DlssPathFor(false, g);
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            n++;
                        }

                        ArtLoader.RemoveDlssManifestRow(false, g);
                        refreshed.Add(g);
                    }
                    catch
                    {
                    }
                }

                _autoGraphics.Clear();
                try
                {
                    Client.Game.UO.FileManager.Arts.RescanOverrides();
                    foreach (ushort g in refreshed)
                    {
                        Client.Game.UO.Arts.RefreshStatic(g);
                    }

                    ThemeManager.Reapply(World);
                }
                catch
                {
                }
            }

            Say(n > 0 ? $"Cleared {n} auto bake(s)." : "Nothing to clear.");
        }
    }
}
