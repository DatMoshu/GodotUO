// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.Comfy;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// In-game image repaint, cloned from JarJarGM's panel: select a static,
    /// describe the change, denoise its artwork through the local ComfyUI
    /// img2img workflow, and paint the result over the world. Opened with the
    /// "-repaint" client command.
    ///
    /// Nothing is ever written to the UO install (or any .mul): results land
    /// as .art files in the override folder (GUO_ART_OVERRIDE, else
    /// %APPDATA%/GUO/overrides), which the loader prefers and rescans live.
    /// Revert deletes the file. Masked (default) keeps the original
    /// silhouette exactly; unmasked takes the whole generated frame.
    /// </summary>
    internal sealed class RepaintGump : Gump
    {
        private const int W = 320;
        private const int H = 592;

        private static readonly string[] Samplers =
        {
            "euler", "euler_ancestral", "heun", "dpm_2", "dpmpp_2m",
            "dpmpp_sde", "ddim", "uni_pc", "lcm",
        };

        private static readonly string[] Schedulers = { "normal", "karras", "exponential", "simple" };

        private readonly Label _graphicLabel;
        private readonly Label _denoiseLabel;
        private readonly StbTextBox _promptBox;
        private readonly StbTextBox _seedBox;
        private readonly StbTextBox _stepsBox;
        private readonly StbTextBox _cfgBox;
        private readonly Combobox _backendPick;
        private readonly Combobox _samplerPick;
        private readonly Combobox _schedulerPick;
        private readonly HSliderBar _denoiseSlider;
        private readonly Checkbox _maskBox;
        private readonly Label _statusLabel;

        private ushort _targetGraphic;
        private GameObject _targetObj;
        private bool _targetIsLand;
        private Task<GenJob> _genTask;
        private string _genStatus = "";
        private string _shownStatus = "";
        private CancellationTokenSource _genCts;

        private sealed class AutoJob
        {
            public ComfyClient.ArtShot Shot;
            public ushort Graphic;
            public int Seed;
        }

        private readonly List<AutoJob> _autoPending = new List<AutoJob>();
        private AutoJob _autoCurrent;
        private int _autoTotal;
        private int _autoDone;
        private bool _autoActive;
        private string _autoPrompt;
        private bool _autoFlux;
        private string _autoSampler;
        private string _autoScheduler;
        private int _autoSteps;
        private float _autoCfg;
        private float _autoDenoise;
        private bool _autoMask;
        private readonly List<ushort> _autoGraphics = new List<ushort>();

        private sealed class GenJob
        {
            public ComfyClient.PngResult Result;
            public ComfyClient.ArtShot Shot;
            public ushort Graphic;
            public bool IsLand;
            public bool Mask;
        }

        private enum Buttons
        {
            Select,
            Generate,
            Revert,
            Close,
            AutoQueue,
            ClearAll
        }

        public RepaintGump(World world)
            : base(world, 140, 120)
        {
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("Repaint", true, 0x0481) { X = 12, Y = 8 });

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
            Add(new Label("Prompt:", true, 0x0386) { X = 12, Y = y });
            y += 22;
            _promptBox = new StbTextBox(1, 500, W - 24, true, FontStyle.None, 0x0021)
            {
                X = 12, Y = y, Width = W - 24, Height = 64, Multiline = true
            };
            _promptBox.SetText("mossy overgrown ancient stone bricks");
            Add(_promptBox);

            y += 72;
            Add(new Label("Seed (-1=random):", true, 0x0386) { X = 12, Y = y });
            _seedBox = new StbTextBox(1, 12, 80, true, FontStyle.None, 0x0021)
            {
                X = 150, Y = y, Width = 80, Height = 22
            };
            _seedBox.SetText("7");
            Add(_seedBox);

            y += 30;
            Add(new Label("Backend:", true, 0x0386) { X = 12, Y = y });
            _backendPick = new Combobox(100, y - 2, 150, new[] { "DreamShaper", "Flux Klein" }, 0);
            _backendPick.OnOptionSelected += (s, i) => ApplyBackendDefaults(i == 1);
            Add(_backendPick);

            y += 30;
            Add(new Label("Sampler:", true, 0x0386) { X = 12, Y = y });
            _samplerPick = new Combobox(100, y - 2, 150, Samplers, 0);
            Add(_samplerPick);

            y += 30;
            Add(new Label("Scheduler:", true, 0x0386) { X = 12, Y = y });
            _schedulerPick = new Combobox(100, y - 2, 150, Schedulers, 0);
            Add(_schedulerPick);

            y += 30;
            Add(new Label("Steps:", true, 0x0386) { X = 12, Y = y });
            _stepsBox = new StbTextBox(1, 4, 60, true, FontStyle.None, 0x0021)
            {
                X = 70, Y = y, Width = 60, Height = 22
            };
            _stepsBox.SetText("20");
            Add(_stepsBox);
            Add(new Label("Cfg:", true, 0x0386) { X = 150, Y = y });
            _cfgBox = new StbTextBox(1, 6, 60, true, FontStyle.None, 0x0021)
            {
                X = 190, Y = y, Width = 60, Height = 22
            };
            _cfgBox.SetText("7");
            Add(_cfgBox);

            y += 30;
            _denoiseLabel = new Label("Denoise:", true, 0x0386) { X = 12, Y = y };
            Add(_denoiseLabel);
            _denoiseSlider = new HSliderBar(100, y, 180, 0, 100, 65,
                HSliderBarStyle.MetalWidgetRecessedBar, hasText: true, font: 1, color: 0x0386, unicode: true);
            Add(_denoiseSlider);

            y += 32;
            _maskBox = new Checkbox(0x00D2, 0x00D3, "Mask to original silhouette", 1, 0x0386, isunicode: true)
            {
                X = 12, Y = y, IsChecked = true
            };
            Add(_maskBox);

            y += 30;
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

        /// <summary>
        /// Tiledata display name for a graphic (land vs static). Empty or
        /// out-of-range names return empty so callers send the prompt
        /// unchanged. The prompt box keeps showing only what the user typed;
        /// the name is only prepended to the submitted string.
        /// </summary>
        private static string TileName(ushort graphic, bool isLand)
        {
            try
            {
                var tileData = Client.Game.UO.FileManager.TileData;
                if (tileData == null)
                {
                    return string.Empty;
                }

                string name;
                if (isLand)
                {
                    var land = tileData.LandData;
                    if (land == null || graphic >= land.Length)
                    {
                        return string.Empty;
                    }

                    name = land[graphic].Name;
                }
                else
                {
                    var statics = tileData.StaticData;
                    if (statics == null || graphic >= statics.Length)
                    {
                        return string.Empty;
                    }

                    name = statics[graphic].Name;
                }

                return string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WithTileName(string prompt, string tileName)
        {
            if (string.IsNullOrWhiteSpace(tileName))
            {
                return prompt;
            }

            return $"{tileName.Trim()}, {prompt}";
        }

        /// <summary>
        /// Flux edits in few steps with middling guidance; DreamShaper wants
        /// more of both. Denoise and scheduler only bite on DreamShaper.
        /// </summary>
        private void ApplyBackendDefaults(bool flux)
        {
            _stepsBox?.SetText(flux ? "4" : "20");
            _cfgBox?.SetText(flux ? "5" : "7");
        }

        public override void Update()
        {
            base.Update();
            if (IsDisposed)
            {
                return;
            }

            if (_denoiseSlider != null && _denoiseLabel != null)
            {
                // The slider runs 0-100; the sampler gets a 0-1 float.
                string t = $"Denoise: {_denoiseSlider.Value / 100f:0.00}";
                if (_denoiseLabel.Text != t)
                {
                    _denoiseLabel.Text = t;
                }
            }

            if (_genTask != null && _genTask.IsCompleted)
            {
                Task<GenJob> done = _genTask;
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
                        OnAutoRepainted(auto, done.Result);
                    }
                    else
                    {
                        OnRepainted(done.Result);
                    }
                }
            }
            else if (_genTask != null && _genStatus != _shownStatus)
            {
                _shownStatus = _genStatus;
                Say(_genStatus);
            }
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
                                _targetGraphic = obj.Graphic;
                                _targetObj = obj;
                                _targetIsLand = obj is Land;
                                GameActions.Print(World,
                                    $"Repaint selected: 0x{_targetGraphic:X4} at {obj.X}, {obj.Y}");
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click the object to repaint...");
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
        }

        private void StartGenerate()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already generating...");
                return;
            }

            if (_targetGraphic == 0 || _targetObj == null || _targetObj.IsDestroyed)
            {
                Say("Select an object first.");
                return;
            }

            ComfyClient.ArtShot shot;
            try
            {
                shot = ComfyClient.ExtractArtShot(_targetGraphic, _targetIsLand);
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

            string prompt = _promptBox.Text?.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                Say("Type a prompt first.");
                return;
            }

            int seed = Random.Shared.Next(1, int.MaxValue);
            if (int.TryParse(_seedBox.Text?.Trim(), out int given) && given >= 0)
            {
                seed = given;
            }

            float denoise = _denoiseSlider.Value / 100f;
            if (denoise <= 0f || denoise > 1f)
            {
                Say("Denoise must be 1-100.");
                return;
            }

            bool flux = _backendPick.SelectedIndex == 1;
            string sampler = _samplerPick.SelectedIndex >= 0 && _samplerPick.SelectedIndex < Samplers.Length
                ? Samplers[_samplerPick.SelectedIndex] : "euler";
            string scheduler = _schedulerPick.SelectedIndex >= 0 && _schedulerPick.SelectedIndex < Schedulers.Length
                ? Schedulers[_schedulerPick.SelectedIndex] : "normal";
            if (!int.TryParse(_stepsBox.Text?.Trim(), out int steps) || steps < 1 || steps > 100)
            {
                Say("Steps must be 1-100.");
                return;
            }

            if (!float.TryParse(_cfgBox.Text?.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float cfg)
                || cfg < 0f || cfg > 30f)
            {
                Say("Cfg must be 0-30.");
                return;
            }

            bool mask = _maskBox.IsChecked;
            ushort graphic = _targetGraphic;
            bool isLand = _targetIsLand;
            string submitPrompt = WithTileName(prompt, TileName(graphic, isLand));
            string url = ComfyClient.ResolveUrl();
            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            _genStatus = $"Contacting ComfyUI at {url}...";
            _shownStatus = "";
            Say(_genStatus);
            _genTask = Task.Run(async () =>
            {
                // The Flux graph has no denoise or scheduler inputs: change
                // amount there is cfg + steps + wording.
                ComfyClient.PngResult result = flux
                    ? await ComfyClient.ImageToImageFluxAsync(
                        url, shot.WorkPng, submitPrompt, seed, cfg, steps, sampler, s => _genStatus = s, ct)
                    : await ComfyClient.ImageToImageAsync(
                        url, shot.WorkPng, submitPrompt, seed, denoise, s => _genStatus = s, ct,
                        sampler: sampler, scheduler: scheduler, steps: steps, cfg: cfg);
                return new GenJob
                {
                    Result = result, Shot = shot, Graphic = graphic, IsLand = isLand, Mask = mask,
                };
            }, ct);
        }

        private void OnRepainted(GenJob job)
        {
            if (IsDisposed || job?.Result?.Png == null)
            {
                Say("Empty result.");
                return;
            }

            using var image = new Godot.Image();
            if (image.LoadPngFromBuffer(job.Result.Png) != Godot.Error.Ok)
            {
                Say("Result is not a PNG.");
                return;
            }

            // ComfyUI saves RGB (no alpha): the encoder and the mask both
            // need RGBA bytes.
            if (image.GetFormat() != Godot.Image.Format.Rgba8)
            {
                image.Convert(Godot.Image.Format.Rgba8);
            }

            if (image.GetWidth() != job.Shot.Width || image.GetHeight() != job.Shot.Height)
            {
                // The diffusion working copy, coming home (DreamShaper keeps
                // the upload size; Flux decodes at its own working size):
                // fit to the native art size. This is a large DOWNSCALE
                // (wall 0x0034: 224x536 work -> 44x107 native, ~5x): it must
                // filter over the whole footprint, not subsample (Nearest).
                // Nearest keeps 1 pixel per 5x5 block and discards the rest,
                // turning smooth diffusion detail into aliased static noise
                // in-game while the pre-downscale preview looks perfect.
                // ("Nearest-exact" is an UPSCALE concept -- centre-corrected
                // nearest so integer enlargement replicates exactly -- and
                // Godot offers no such mode; on a downscale any nearest
                // variant still subsamples, so it cannot apply here. The
                // upscale side already uses Nearest, which replicates exactly
                // at integer factors.) Trilinear is Godot's mipmap-based
                // resize: box-filtered mip chain plus blend of the two
                // bracketing levels, i.e. a true area average over each ~5x5
                // block, so detail folds down instead of aliasing. It reads
                // slightly softer than Lanczos/single-step Cubic, but those
                // keep a narrow footprint at 5x (aliasing) and can ring on
                // hard pixel edges; softness here costs nothing because the
                // silhouette mask below re-applies the original crisp edges
                // at native size (see VariantStudio.FitTo). The source has
                // no alpha yet, so filtering opaque RGB first is safe. Mask
                // handling stays a byte copy / Nearest-only elsewhere.
                image.Resize(job.Shot.Width, job.Shot.Height, Godot.Image.Interpolation.Trilinear);
            }

            byte[] rgba = image.GetData();
            if (job.Mask && image.GetWidth() == job.Shot.Width && image.GetHeight() == job.Shot.Height)
            {
                for (int i = 0; i < job.Shot.Width * job.Shot.Height; i++)
                {
                    if (job.Shot.Rgba[i * 4 + 3] == 0)
                    {
                        rgba[i * 4 + 3] = 0;
                    }
                }
            }

            byte[] art;
            try
            {
                art = Assets.ArtEncoder.EncodeStatic(rgba, image.GetWidth(), image.GetHeight());
            }
            catch (Exception ex)
            {
                Say($"Unencodable: {ex.Message}");
                return;
            }

            try
            {
                string dir = Path.Combine(ArtLoader.OverrideDir(), "Art",
                    job.IsLand ? "Land" : "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{job.Graphic}.art"), art);
                Client.Game.UO.FileManager.Arts.RescanOverrides();
                if (job.IsLand)
                {
                    Client.Game.UO.Arts.RefreshLand(job.Graphic);
                }
                else
                {
                    Client.Game.UO.Arts.RefreshStatic(job.Graphic);
                }

                ThemeManager.Reapply(World);
            }
            catch (Exception ex)
            {
                Say($"Override failed: {ex.Message}");
                return;
            }

            string msg = $"0x{job.Graphic:X4} repainted ({(job.Mask ? "masked" : "unmasked")})";
            Say(msg + ".");
            GameActions.Print(World, "Repaint: " + msg);
        }

        private void Revert()
        {
            if (_targetGraphic == 0)
            {
                Say("Select an object first.");
                return;
            }

            try
            {
                string path = Path.Combine(ArtLoader.OverrideDir(), "Art",
                    _targetIsLand ? "Land" : "Statics", $"{_targetGraphic}.art");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                Client.Game.UO.FileManager.Arts.RescanOverrides();
                if (_targetIsLand)
                {
                    Client.Game.UO.Arts.RefreshLand(_targetGraphic);
                }
                else
                {
                    Client.Game.UO.Arts.RefreshStatic(_targetGraphic);
                }

                ThemeManager.Reapply(World);
                Say($"Reverted 0x{_targetGraphic:X4}.");
            }
            catch (Exception ex)
            {
                Say($"Revert failed: {ex.Message}");
            }
        }

        private static string OverridePathFor(ushort graphic)
        {
            return Path.Combine(ArtLoader.OverrideDir(), "Art", "Statics", $"{graphic}.art");
        }

        /// <summary>
        /// Bulk repaint: every static within 17 tiles, deduped to unique
        /// graphics. Graphics with an existing override file are skipped
        /// (the repaint equivalent of the moge cache-hit); the rest generate
        /// sequentially on the same one-Task worker pattern as single
        /// Generate (Close/Clear All cancels). Each completion writes its own
        /// override file immediately; Clear All deletes the auto-created
        /// files again. Single-target flow is untouched.
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

            string prompt = _promptBox.Text?.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                Say("Type a prompt first.");
                return;
            }

            float denoise = _denoiseSlider.Value / 100f;
            if (denoise <= 0f || denoise > 1f)
            {
                Say("Denoise must be 1-100.");
                return;
            }

            bool flux = _backendPick.SelectedIndex == 1;
            string sampler = _samplerPick.SelectedIndex >= 0 && _samplerPick.SelectedIndex < Samplers.Length
                ? Samplers[_samplerPick.SelectedIndex] : "euler";
            string scheduler = _schedulerPick.SelectedIndex >= 0 && _schedulerPick.SelectedIndex < Schedulers.Length
                ? Schedulers[_schedulerPick.SelectedIndex] : "normal";
            if (!int.TryParse(_stepsBox.Text?.Trim(), out int steps) || steps < 1 || steps > 100)
            {
                Say("Steps must be 1-100.");
                return;
            }

            if (!float.TryParse(_cfgBox.Text?.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float cfg)
                || cfg < 0f || cfg > 30f)
            {
                Say("Cfg must be 0-30.");
                return;
            }

            bool mask = _maskBox.IsChecked;
            int seedBase = 0;
            bool seedGiven = int.TryParse(_seedBox.Text?.Trim(), out seedBase) && seedBase >= 0;

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
                        if (o is not Static s)
                        {
                            continue;
                        }

                        if (s.IsDestroyed || !s.AllowedToDraw || s.Graphic == 0)
                        {
                            continue;
                        }

                        found.Add(s.Graphic);
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
            _autoPending.Clear();
            int skipped = 0;
            int nogfx = 0;
            int seedIndex = 0;
            foreach (ushort g in sorted)
            {
                if (File.Exists(OverridePathFor(g)))
                {
                    skipped++;
                    continue;
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

                if (shot == null || shot.WorkPng == null || shot.TightWidth <= 0)
                {
                    nogfx++;
                    continue;
                }

                int seed = seedGiven
                    ? seedBase + seedIndex
                    : Random.Shared.Next(1, int.MaxValue);
                seedIndex++;
                _autoPending.Add(new AutoJob { Shot = shot, Graphic = g, Seed = seed });
            }

            int queued = _autoPending.Count;
            GameActions.Print(World,
                $"Repaint auto: {found.Count} graphic(s), {skipped} with overrides, {queued} to generate"
                + (nogfx > 0 ? $", {nogfx} without artwork" : ""));
            if (queued == 0)
            {
                Say(skipped > 0
                    ? $"Auto done: {skipped} already repainted, nothing to generate."
                    : "Nothing with artwork to queue.");
                return;
            }

            _autoPrompt = prompt;
            _autoFlux = flux;
            _autoSampler = sampler;
            _autoScheduler = scheduler;
            _autoSteps = steps;
            _autoCfg = cfg;
            _autoDenoise = denoise;
            _autoMask = mask;
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
            string prompt = WithTileName(_autoPrompt, TileName(job.Graphic, false));
            bool flux = _autoFlux;
            string sampler = _autoSampler;
            string scheduler = _autoScheduler;
            int steps = _autoSteps;
            float cfg = _autoCfg;
            float denoise = _autoDenoise;
            bool mask = _autoMask;
            ComfyClient.ArtShot shot = job.Shot;
            int seed = job.Seed;
            string url = ComfyClient.ResolveUrl();
            _genTask = Task.Run(async () =>
            {
                ComfyClient.PngResult result = flux
                    ? await ComfyClient.ImageToImageFluxAsync(
                        url, shot.WorkPng, prompt, seed, cfg, steps, sampler, s => _genStatus = $"Auto {done + 1}/{total} 0x{graphic:X4}: {s}", ct)
                    : await ComfyClient.ImageToImageAsync(
                        url, shot.WorkPng, prompt, seed, denoise, s => _genStatus = $"Auto {done + 1}/{total} 0x{graphic:X4}: {s}", ct,
                        sampler: sampler, scheduler: scheduler, steps: steps, cfg: cfg);
                return new GenJob
                {
                    Result = result, Shot = shot, Graphic = graphic, IsLand = false, Mask = mask,
                };
            }, ct);
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
                Say($"Auto done: {_autoDone}/{_autoTotal} repainted.");
                GameActions.Print(World,
                    $"Repaint auto done: {_autoDone}/{_autoTotal} repainted.");
            }
        }

        private void OnAutoRepainted(AutoJob job, GenJob result)
        {
            if (IsDisposed || result?.Result?.Png == null)
            {
                Say($"Auto 0x{job.Graphic:X4}: empty result.");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            using var image = new Godot.Image();
            if (image.LoadPngFromBuffer(result.Result.Png) != Godot.Error.Ok)
            {
                Say($"Auto 0x{job.Graphic:X4}: result is not a PNG.");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            if (image.GetFormat() != Godot.Image.Format.Rgba8)
            {
                image.Convert(Godot.Image.Format.Rgba8);
            }

            if (image.GetWidth() != job.Shot.Width || image.GetHeight() != job.Shot.Height)
            {
                image.Resize(job.Shot.Width, job.Shot.Height, Godot.Image.Interpolation.Trilinear);
            }

            byte[] rgba = image.GetData();
            if (_autoMask && image.GetWidth() == job.Shot.Width && image.GetHeight() == job.Shot.Height)
            {
                for (int i = 0; i < job.Shot.Width * job.Shot.Height; i++)
                {
                    if (job.Shot.Rgba[i * 4 + 3] == 0)
                    {
                        rgba[i * 4 + 3] = 0;
                    }
                }
            }

            byte[] art;
            try
            {
                art = Assets.ArtEncoder.EncodeStatic(rgba, image.GetWidth(), image.GetHeight());
            }
            catch (Exception ex)
            {
                Say($"Auto 0x{job.Graphic:X4}: unencodable: {ex.Message}");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            try
            {
                string dir = Path.Combine(ArtLoader.OverrideDir(), "Art", "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{job.Graphic}.art"), art);
                Client.Game.UO.FileManager.Arts.RescanOverrides();
                Client.Game.UO.Arts.RefreshStatic(job.Graphic);
                ThemeManager.Reapply(World);
            }
            catch (Exception ex)
            {
                Say($"Auto 0x{job.Graphic:X4}: override failed: {ex.Message}");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            if (!_autoGraphics.Contains(job.Graphic))
            {
                _autoGraphics.Add(job.Graphic);
            }

            string msg = $"0x{job.Graphic:X4} repainted ({(_autoMask ? "masked" : "unmasked")})";
            GameActions.Print(World, "Repaint auto: " + msg);
            _autoDone++;
            if (_autoPending.Count > 0)
            {
                Say($"Auto {_autoDone + 1}/{_autoTotal} 0x{job.Graphic:X4}: done.");
            }

            ContinueAutoQueue();
        }

        /// <summary>
        /// Cancels any running auto queue and deletes the overrides this
        /// session's auto run created; single-target repaints are left alone
        /// unless they share a graphic (same file, same result).
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
            if (_autoGraphics.Count > 0)
            {
                var refreshed = new HashSet<ushort>();
                foreach (ushort g in new List<ushort>(_autoGraphics))
                {
                    try
                    {
                        string path = OverridePathFor(g);
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            n++;
                        }

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

            Say(n > 0 ? $"Cleared {n} auto repaint(s)." : "Nothing to clear.");
        }
    }
}
