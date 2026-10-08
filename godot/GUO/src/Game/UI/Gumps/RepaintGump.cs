// SPDX-License-Identifier: BSD-2-Clause

using System;
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
        private const int H = 430;

        private readonly Label _graphicLabel;
        private readonly StbTextBox _promptBox;
        private readonly StbTextBox _seedBox;
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
            Close
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
            Add(new Label("Denoise:", true, 0x0386) { X = 12, Y = y });
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

        public override void Update()
        {
            base.Update();
            if (IsDisposed)
            {
                return;
            }

            if (_genTask != null && _genTask.IsCompleted)
            {
                Task<GenJob> done = _genTask;
                _genTask = null;
                if (done.IsFaulted)
                {
                    Say($"Generate failed: {done.Exception?.GetBaseException().Message}");
                }
                else if (done.IsCanceled)
                {
                    Say("Generate cancelled.");
                }
                else
                {
                    OnRepainted(done.Result);
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

                case (int)Buttons.Close:
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

            bool mask = _maskBox.IsChecked;
            ushort graphic = _targetGraphic;
            bool isLand = _targetIsLand;
            string url = ComfyClient.ResolveUrl();
            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            _genStatus = $"Contacting ComfyUI at {url}...";
            _shownStatus = "";
            Say(_genStatus);
            _genTask = Task.Run(async () =>
            {
                ComfyClient.PngResult result = await ComfyClient.ImageToImageAsync(
                    url, shot.Png, prompt, seed, denoise, s => _genStatus = s, ct);
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
                Say($"Size changed {job.Shot.Width}x{job.Shot.Height} -> {image.GetWidth()}x{image.GetHeight()}; keeping unmasked.");
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
    }
}
