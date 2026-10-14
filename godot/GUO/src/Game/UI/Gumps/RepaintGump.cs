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
        private const int H = 816;

        private static readonly string[] Samplers =
        {
            "euler", "euler_ancestral", "heun", "dpm_2", "dpmpp_2m",
            "dpmpp_sde", "ddim", "uni_pc", "lcm",
        };

        private static readonly string[] Schedulers = { "normal", "karras", "exponential", "simple" };

        /// <summary>
        /// Continuous rescan range (tiles around the player, same 17-tile
        /// pattern as the manual batch) and the movement that triggers a
        /// rescan: N tiles (Chebyshev) or an 8x8 chunk change, whichever
        /// comes first. Per-rescan cap bounds each new batch so a walk
        /// through a dense town cannot pile up hundreds of prompts.
        /// </summary>
        private const int AutoScanRange = 17;
        private const int AutoRescanMoveThreshold = 8;
        private const int AutoChunkSize = 8;
        private const int AutoContinuousMaxQueue = 32;

        private readonly Label _graphicLabel;
        private readonly Label _denoiseLabel;
        private readonly StbTextBox _promptBox;
        private readonly StbTextBox _seedBox;
        private readonly StbTextBox _stepsBox;
        private readonly StbTextBox _cfgBox;
        private readonly Combobox _backendPick;
        private readonly Combobox _samplerPick;
        private readonly Combobox _schedulerPick;
        private readonly Combobox _kindPick;
        private readonly HSliderBar _denoiseSlider;
        private readonly Checkbox _maskBox;
        private readonly Checkbox _includeNameBox;
        private readonly Checkbox _depthBox;
        private readonly Checkbox _autoQueueBox;
        private readonly Label _depthStrengthLabel;
        private readonly Label _depthStartLabel;
        private readonly Label _depthEndLabel;
        private readonly HSliderBar _depthStrengthSlider;
        private readonly HSliderBar _depthStartSlider;
        private readonly HSliderBar _depthEndSlider;
        private readonly Label _statusLabel;

        /// <summary>True when the Target combobox says Land; false is Statics.</summary>
        private bool LandMode => _kindPick != null && _kindPick.SelectedIndex == 1;

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
            public bool IsLand;
            public string SubmitPrompt = "";
            public byte[] DepthPng;
        }

        /// <summary>
        /// One submitted prompt: the graphic it belongs to plus the ComfyUI
        /// prompt id to poll. Filled during the submit-all-up-front burst;
        /// the poll phase matches each returned image back by PromptId.
        /// </summary>
        private sealed class AutoSubmitted
        {
            public AutoJob Job;
            public string PromptId = "";
        }

        /// <summary>
        /// One finished poll, handed from the worker to Update() for the
        /// main-thread commit (Godot Images + override files stay off worker
        /// threads). Png null means the item failed; Error carries why.
        /// </summary>
        private sealed class AutoBatchResult
        {
            public AutoJob Job;
            public string PromptId = "";
            public byte[] Png;
            public string FileName = "";
            public string Error = "";
            public string Prompt = "";
            public int Seed;
            public string Backend = "";
            public string Sampler = "";
            public string Scheduler = "";
            public int Steps;
            public float Cfg;
            public float Denoise;
            public bool Mask;
            public bool IsLand;
            public bool DepthEnabled;
            public float DepthStrength;
            public float DepthStart;
            public float DepthEnd;
            public bool DepthFallback;
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
        private bool _autoIncludeName;
        private bool _autoIsLand;
        private bool _autoDepthEnabled;
        private float _autoDepthStrength;
        private float _autoDepthStart;
        private float _autoDepthEnd;
        private bool _autoDepthFallback;
        private volatile bool _autoSubmitsFinished;
        private readonly List<(ushort Graphic, bool IsLand)> _autoGraphics = new List<(ushort, bool)>();

        /// <summary>
        /// Continuous toggle state: on means rescan whenever the player
        /// moves (see <see cref="AutoRescanMoveThreshold"/>); off means
        /// finish the current batch and stop. Persisted in
        /// <see cref="Profile.RepaintAutoQueue"/> like RepaintIncludeName.
        /// _autoQueuedKeys dedups across rescans (already queued or done);
        /// _autoLastPX/PY is the player tile of the last scan.
        /// </summary>
        private bool _autoContinuous;
        private bool _autoSuppressQueueBoxEvent;
        private readonly HashSet<string> _autoQueuedKeys = new HashSet<string>();
        private int _autoLastPX;
        private int _autoLastPY;
        private bool _autoHasPos;

        /// <summary>
        /// Parallel batch state: the worker submits prompts while a
        /// concurrent poll loop sweeps whatever is submitted so far;
        /// each finished download waits here for Update() to commit it
        /// on the main thread (true streaming: nothing waits for
        /// all-done). _autoSubmitted counts queued prompts so the status
        /// line can show submitted + done.
        /// </summary>
        private Task _autoRunner;
        private readonly ConcurrentQueue<AutoBatchResult> _autoResults = new ConcurrentQueue<AutoBatchResult>();
        private volatile int _autoSubmitted;
        private string _autoRunnerError = "";

        /// <summary>
        /// Truly-continuous ingress: WatchPlayerForRescan fires while the
        /// runner is active and ScanAndQueueContinuous appends new graphics
        /// here (deduped via _autoQueuedKeys, totals bumped live). The
        /// running submit loop drains this queue without restart, so walks
        /// mid-batch submit while generations are still in flight. Caps
        /// stay: 32/scan, max in-flight 4, queue-depth throttle; Drain
        /// still commits max 2/frame. Cleared on Clear All / Close / fresh
        /// start; main thread enqueues, worker dequeues.
        /// </summary>
        private readonly ConcurrentQueue<AutoJob> _autoLiveQueue = new ConcurrentQueue<AutoJob>();

        /// <summary>
        /// Streaming pump cap: Update() commits at most this many
        /// finished auto items per tick so a burst of simultaneous
        /// completions applies ingame one-by-one without hitching.
        /// The 0.25s backup below re-pumps within the same tick budget
        /// when a hitch leaves finished items sitting past the interval.
        /// </summary>
        private const int AutoMaxCommitsPerFrame = 2;
        private static readonly TimeSpan AutoDrainBackupInterval = TimeSpan.FromSeconds(0.25);
        private DateTime _autoLastDrainUtc = DateTime.MinValue;

        private sealed class GenJob
        {
            public ComfyClient.PngResult Result;
            public ComfyClient.ArtShot Shot;
            public ushort Graphic;
            public bool IsLand;
            public bool Mask;
            public string Prompt = "";
            public int Seed;
            public string Backend = "";
            public string Sampler = "";
            public string Scheduler = "";
            public int Steps;
            public float Cfg;
            public float Denoise;
            public bool DepthEnabled;
            public float DepthStrength;
            public float DepthStart;
            public float DepthEnd;
            public bool DepthFallback;
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
            Add(new Label("Target:", true, 0x0386) { X = 12, Y = y });
            _kindPick = new Combobox(100, y - 2, 150, new[] { "Statics", "Land" }, 0);
            Add(_kindPick);

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

            y += 26;
            bool includeName = ProfileManager.CurrentProfile?.RepaintIncludeName ?? true;
            _includeNameBox = new Checkbox(0x00D2, 0x00D3, "Include object name in prompt", 1, 0x0386, isunicode: true)
            {
                X = 12, Y = y, IsChecked = includeName
            };
            _includeNameBox.ValueChanged += (s, e) =>
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintIncludeName = _includeNameBox.IsChecked;
                }
            };
            Add(_includeNameBox);

            y += 26;
            bool depthOn = ProfileManager.CurrentProfile?.RepaintDepthEnabled ?? false;
            _depthBox = new Checkbox(0x00D2, 0x00D3, "Depth ControlNet (keep structure)", 1, 0x0386, isunicode: true)
            {
                X = 12, Y = y, IsChecked = depthOn
            };
            _depthBox.ValueChanged += (s, e) =>
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintDepthEnabled = _depthBox.IsChecked;
                }
            };
            Add(_depthBox);

            y += 26;
            Profile dep = ProfileManager.CurrentProfile;
            float dStrength = dep != null ? dep.RepaintDepthStrength : ComfyClient.DefaultDepthStrength;
            float dStart = dep != null ? dep.RepaintDepthStart : ComfyClient.DefaultDepthStart;
            float dEnd = dep != null ? dep.RepaintDepthEnd : ComfyClient.DefaultDepthEnd;
            _depthStrengthLabel = new Label("Depth str:", true, 0x0386) { X = 12, Y = y };
            Add(_depthStrengthLabel);
            _depthStrengthSlider = new HSliderBar(100, y, 180, 0, 100, (int)Math.Round(dStrength * 100f),
                HSliderBarStyle.MetalWidgetRecessedBar, hasText: true, font: 1, color: 0x0386, unicode: true);
            _depthStrengthSlider.ValueChanged += (s, e) =>
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintDepthStrength = _depthStrengthSlider.Value / 100f;
                }
            };
            Add(_depthStrengthSlider);

            y += 30;
            _depthStartLabel = new Label("Depth start:", true, 0x0386) { X = 12, Y = y };
            Add(_depthStartLabel);
            _depthStartSlider = new HSliderBar(100, y, 180, 0, 100, (int)Math.Round(dStart * 100f),
                HSliderBarStyle.MetalWidgetRecessedBar, hasText: true, font: 1, color: 0x0386, unicode: true);
            _depthStartSlider.ValueChanged += (s, e) =>
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintDepthStart = _depthStartSlider.Value / 100f;
                }
            };
            Add(_depthStartSlider);

            y += 30;
            _depthEndLabel = new Label("Depth end:", true, 0x0386) { X = 12, Y = y };
            Add(_depthEndLabel);
            _depthEndSlider = new HSliderBar(100, y, 180, 0, 100, (int)Math.Round(dEnd * 100f),
                HSliderBarStyle.MetalWidgetRecessedBar, hasText: true, font: 1, color: 0x0386, unicode: true);
            _depthEndSlider.ValueChanged += (s, e) =>
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintDepthEnd = _depthEndSlider.Value / 100f;
                }
            };
            Add(_depthEndSlider);

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
            bool autoQueue = ProfileManager.CurrentProfile?.RepaintAutoQueue ?? false;
            _autoContinuous = autoQueue;
            _autoQueueBox = new Checkbox(0x00D2, 0x00D3, "Auto Queue (continuous)", 1, 0x0386, isunicode: true)
            {
                X = 12, Y = y, IsChecked = autoQueue
            };
            _autoQueueBox.ValueChanged += (s, e) =>
            {
                if (_autoSuppressQueueBoxEvent)
                {
                    return;
                }

                bool on = _autoQueueBox != null && _autoQueueBox.IsChecked;
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintAutoQueue = on;
                }

                if (on)
                {
                    EnableAutoContinuous();
                }
                else
                {
                    DisableAutoContinuous();
                }
            };
            Add(_autoQueueBox);

            y += 26;
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
                : $"0x{_targetGraphic:X4} ({_targetGraphic}){(_targetIsLand ? " land" : "")}";
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

        private static string WithTileName(string prompt, string tileName, bool includeName)
        {
            if (!includeName || string.IsNullOrWhiteSpace(tileName))
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

            if (_depthStrengthSlider != null && _depthStrengthLabel != null)
            {
                string t = $"Depth str: {_depthStrengthSlider.Value / 100f:0.00}";
                if (_depthStrengthLabel.Text != t)
                {
                    _depthStrengthLabel.Text = t;
                }
            }

            if (_depthStartSlider != null && _depthStartLabel != null)
            {
                string t = $"Depth start: {_depthStartSlider.Value / 100f:0.00}";
                if (_depthStartLabel.Text != t)
                {
                    _depthStartLabel.Text = t;
                }
            }

            if (_depthEndSlider != null && _depthEndLabel != null)
            {
                string t = $"Depth end: {_depthEndSlider.Value / 100f:0.00}";
                if (_depthEndLabel.Text != t)
                {
                    _depthEndLabel.Text = t;
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

            DrainAutoBatch();
            WatchPlayerForRescan();
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
                                GameObject picked = obj;
                                bool wantLand = LandMode;
                                bool pickedIsLand = picked is Land;
                                if (wantLand != pickedIsLand)
                                {
                                    // Explicit override: the toggle wins over
                                    // whatever the cursor hit. Resolve the
                                    // wanted kind on the same tile so Land mode
                                    // clicking a house still grabs its grass,
                                    // and Statics mode clicking grass grabs an
                                    // object on it. One kind per run.
                                    GameObject wanted = null;
                                    try
                                    {
                                        for (GameObject o = World.Map.GetTile(picked.X, picked.Y, false);
                                            o != null; o = o.TNext)
                                        {
                                            if (o == null || o.IsDestroyed || o.Graphic == 0)
                                            {
                                                continue;
                                            }

                                            if ((o is Land) != wantLand)
                                            {
                                                continue;
                                            }

                                            if (!o.AllowedToDraw)
                                            {
                                                continue;
                                            }

                                            wanted = o;
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                    }

                                    if (wanted != null)
                                    {
                                        picked = wanted;
                                        pickedIsLand = picked is Land;
                                    }
                                    else
                                    {
                                        GameActions.Print(World, wantLand
                                            ? $"Repaint: no land tile at {picked.X}, {picked.Y}."
                                            : $"Repaint: no static at {picked.X}, {picked.Y} (land picked).");
                                        Say(wantLand
                                            ? "That tile has no land; pick a ground tile."
                                            : "That tile has no static; pick an object.");
                                        UpdateLabels();
                                        return;
                                    }
                                }

                                _targetGraphic = picked.Graphic;
                                _targetObj = picked;
                                _targetIsLand = picked is Land;
                                GameActions.Print(World,
                                    $"Repaint selected: 0x{_targetGraphic:X4} at {picked.X}, {picked.Y}");
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say(LandMode ? "Click the ground to repaint..." : "Click the object to repaint...");
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
                    _autoContinuous = false;
                    _autoPending.Clear();
                    _autoCurrent = null;
                    _autoRunner = null;
                    while (_autoResults.TryDequeue(out _)) { }
                    while (_autoLiveQueue.TryDequeue(out _)) { }
                    try
                    {
                        _genCts?.Cancel();
                    }
                    catch
                    {
                    }

                    try
                    {
                        string closeUrl = ComfyClient.ResolveUrl();
                        _ = ComfyClient.InterruptAsync(closeUrl, CancellationToken.None);
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

            if (_autoActive || (_autoRunner != null && !_autoRunner.IsCompleted))
            {
                Say("Auto queue running...");
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
            bool includeName = _includeNameBox != null && _includeNameBox.IsChecked;
            bool depthEnabled = _depthBox != null && _depthBox.IsChecked;
            float depthStrength = _depthStrengthSlider != null ? _depthStrengthSlider.Value / 100f : ComfyClient.DefaultDepthStrength;
            float depthStart = _depthStartSlider != null ? _depthStartSlider.Value / 100f : ComfyClient.DefaultDepthStart;
            float depthEnd = _depthEndSlider != null ? _depthEndSlider.Value / 100f : ComfyClient.DefaultDepthEnd;
            if (depthEnabled)
            {
                if (depthStrength < 0f || depthStrength > 1f)
                {
                    Say("Depth strength must be 0-100.");
                    return;
                }

                if (depthStart < 0f || depthStart > 1f || depthEnd < 0f || depthEnd > 1f || depthStart > depthEnd)
                {
                    Say("Depth start/end must be 0-100 with start <= end.");
                    return;
                }
            }

            ushort graphic = _targetGraphic;
            bool isLand = _targetIsLand;
            string submitPrompt = WithTileName(prompt, TileName(graphic, isLand), includeName);
            byte[] depthPng = null;
            if (depthEnabled)
            {
                try
                {
                    depthPng = ComfyClient.BuildDepthMapPng(shot.WorkPng);
                }
                catch (Exception ex)
                {
                    Say($"Depth map failed: {ex.Message}");
                    return;
                }
            }

            string url = ComfyClient.ResolveUrl();
            _genCts = new CancellationTokenSource();
            CancellationToken ct = _genCts.Token;
            _genStatus = $"Contacting ComfyUI at {url}...";
            _shownStatus = "";
            Say(_genStatus);
            bool depthFallback = false;
            _genTask = Task.Run(async () =>
            {
                void Report(string s)
                {
                    _genStatus = s;
                    if (s == ComfyClient.DepthFallbackStatus)
                    {
                        depthFallback = true;
                    }
                }

                // The Flux graph has no denoise or scheduler inputs: change
                // amount there is cfg + steps + wording.
                ComfyClient.PngResult result = flux
                    ? await ComfyClient.ImageToImageFluxAsync(
                        url, shot.WorkPng, submitPrompt, seed, cfg, steps, sampler, Report, ct,
                        depthPng: depthPng, depthStrength: depthStrength, depthStart: depthStart, depthEnd: depthEnd)
                    : await ComfyClient.ImageToImageAsync(
                        url, shot.WorkPng, submitPrompt, seed, denoise, Report, ct,
                        sampler: sampler, scheduler: scheduler, steps: steps, cfg: cfg,
                        depthPng: depthPng, depthStrength: depthStrength, depthStart: depthStart, depthEnd: depthEnd);
                return new GenJob
                {
                    Result = result, Shot = shot, Graphic = graphic, IsLand = isLand, Mask = mask,
                    Prompt = submitPrompt, Seed = seed,
                    Backend = flux ? "Flux Klein" : "DreamShaper",
                    Sampler = sampler, Scheduler = scheduler, Steps = steps, Cfg = cfg, Denoise = denoise,
                    DepthEnabled = depthEnabled && !depthFallback, DepthStrength = depthStrength, DepthStart = depthStart, DepthEnd = depthEnd,
                    DepthFallback = depthFallback,
                };
            }, ct);
        }

        /// <summary>
        /// Enhanced HD save: the pre-downscale RGBA (before the native
        /// downscale resize below) as overrides/HD/Art PNG plus its
        /// manifest.json row. The mask is applied at HD with the
        /// nearest-upscaled native silhouette; land enforces its diamond
        /// (corners stay transparent even unmasked). Best effort: HD
        /// never fails the native .art write.
        /// </summary>
        private static void SaveHdPreDownscale(GenJob job, Godot.Image image)
        {
            try
            {
                int hdW = image.GetWidth();
                int hdH = image.GetHeight();
                int natW = job.Shot.Width;
                int natH = job.Shot.Height;
                if (hdW <= 0 || hdH <= 0 || natW <= 0 || natH <= 0)
                {
                    return;
                }

                // Only HD when the generation came home larger than native;
                // a same-size result adds nothing over the .art.
                if (hdW < natW || hdH < natH)
                {
                    return;
                }

                byte[] hdRgba = image.GetData();
                if (hdRgba == null || hdRgba.Length < hdW * hdH * 4)
                {
                    return;
                }

                ApplyHdMask(hdRgba, hdW, hdH, job.Shot.Rgba, natW, natH, job.IsLand, job.Mask);

                byte[] png;
                using (var hd = Godot.Image.CreateFromData(hdW, hdH, false, Godot.Image.Format.Rgba8, hdRgba))
                {
                    png = hd.SavePngToBuffer();
                }

                if (png == null || png.Length == 0)
                {
                    return;
                }

                string dir = Path.Combine(ArtLoader.HdDir(), job.IsLand ? "Land" : "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{job.Graphic}.png"), png);

                var row = new ArtLoader.HdManifestRow
                {
                    NativeW = natW,
                    NativeH = natH,
                    HdW = hdW,
                    HdH = hdH,
                    Scale = natW > 0 ? (double)hdW / natW : 1.0,
                    Prompt = job.Prompt ?? "",
                    Seed = job.Seed,
                    Backend = job.Backend ?? "",
                    Sampler = job.Sampler ?? "",
                    Scheduler = job.Scheduler ?? "",
                    Steps = job.Steps,
                    Cfg = job.Cfg,
                    Denoise = job.Denoise,
                    Masked = job.Mask,
                    DepthEnabled = job.DepthEnabled,
                    DepthStrength = job.DepthStrength,
                    DepthStart = job.DepthStart,
                    DepthEnd = job.DepthEnd,
                    Sha256 = ArtLoader.Sha256Hex(png),
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                };
                ArtLoader.WriteHdManifestRow(job.IsLand, job.Graphic, row);
            }
            catch
            {
            }
        }

        private static void ApplyHdMask(byte[] hdRgba, int hdW, int hdH, byte[] nativeRgba, int natW, int natH, bool isLand, bool mask)
        {
            bool apply = mask || isLand;
            if (!apply || hdRgba == null || nativeRgba == null)
            {
                return;
            }

            for (int y = 0; y < hdH; y++)
            {
                int ny = y * natH / hdH;
                for (int x = 0; x < hdW; x++)
                {
                    int nx = x * natW / hdW;
                    if (nativeRgba[(ny * natW + nx) * 4 + 3] == 0)
                    {
                        hdRgba[(y * hdW + x) * 4 + 3] = 0;
                    }
                }
            }
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

            // Enhanced HD: save the pre-downscale frame before the native
            // resize below (mask at HD, land diamond enforced).
            SaveHdPreDownscale(job, image);

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
            // Land enforces its diamond even unmasked: corners stay
            // transparent so a square frame never pavs the ground.
            if ((job.Mask || job.IsLand) && image.GetWidth() == job.Shot.Width && image.GetHeight() == job.Shot.Height)
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
            if (job.DepthEnabled)
            {
                msg += $" depth {job.DepthStrength:0.00} {job.DepthStart:0.00}-{job.DepthEnd:0.00}";
            }

            GameActions.Print(World, "Repaint: " + msg);
            Say(job.DepthFallback ? ComfyClient.DepthFallbackStatus : msg + ".");
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

                // Enhanced HD: the pre-downscale PNG plus its manifest row.
                ArtLoader.DeleteHd(_targetIsLand, _targetGraphic);

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
                try
                {
                    _autoQueuedKeys.Remove(AutoKey(_targetGraphic, _targetIsLand));
                }
                catch
                {
                }

                Say($"Reverted 0x{_targetGraphic:X4}.");
            }
            catch (Exception ex)
            {
                Say($"Revert failed: {ex.Message}");
            }
        }

        private static string OverridePathFor(ushort graphic, bool isLand)
        {
            return Path.Combine(ArtLoader.OverrideDir(), "Art", isLand ? "Land" : "Statics", $"{graphic}.art");
        }

        private static string AutoKey(ushort graphic, bool isLand)
        {
            return $"{graphic}:{(isLand ? "land" : "static")}";
        }

        /// <summary>
        /// True when this graphic already has paint: a native .art
        /// override or an HD pre-downscale PNG. Either one means there is
        /// nothing to generate (the HD PNG loads through the Art cache).
        /// </summary>
        private static bool HasExistingPaint(ushort graphic, bool isLand)
        {
            try
            {
                if (File.Exists(OverridePathFor(graphic, isLand)))
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                if (File.Exists(ArtLoader.HdPathFor(isLand, graphic)))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// Visible statics/land in range: the same 17-tile pattern as the
        /// manual batch, deduped to unique graphics. One kind per call
        /// (the Target combobox): land tiles or static + multi parts (house
        /// multis share static art ids, so walls are found next to a house).
        /// </summary>
        private HashSet<ushort> CollectInRangeGraphics(bool landMode)
        {
            var found = new HashSet<ushort>();
            if (World.Player == null || World.Map == null)
            {
                return found;
            }

            int px = World.Player.X;
            int py = World.Player.Y;
            for (int dy = -AutoScanRange; dy <= AutoScanRange; dy++)
            {
                for (int dx = -AutoScanRange; dx <= AutoScanRange; dx++)
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
                        if (landMode)
                        {
                            if (o is not Land l)
                            {
                                continue;
                            }

                            if (l.IsDestroyed || !l.AllowedToDraw || l.Graphic == 0)
                            {
                                continue;
                            }

                            found.Add(l.Graphic);
                        }
                        else
                        {
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
            }

            return found;
        }

        /// <summary>
        /// Toggle on: persist, remember where the player stands, and start
        /// a batch now when idle. When a batch is already running the flag
        /// alone is enough: the player watcher queues the next batch once
        /// it drains.
        /// </summary>
        private void EnableAutoContinuous()
        {
            _autoContinuous = true;
            if (World.Player != null)
            {
                _autoLastPX = World.Player.X;
                _autoLastPY = World.Player.Y;
                _autoHasPos = true;
            }

            if (_autoActive || (_autoRunner != null && !_autoRunner.IsCompleted))
            {
                Say("Auto continuous on: will rescan as you move.");
                return;
            }

            if (!_autoResults.IsEmpty)
            {
                Say("Auto continuous on: draining, then rescanning as you move.");
                return;
            }

            StartAutoQueue();
            if (!_autoActive)
            {
                // StartAutoQueue says why (no world, no prompt, nothing
                // new); keep the toggle on so the watcher retries on move.
                _autoHasPos = World.Player != null;
                if (World.Player != null)
                {
                    _autoLastPX = World.Player.X;
                    _autoLastPY = World.Player.Y;
                }
            }
        }

        /// <summary>
        /// Toggle off: persist and stop rescanning after the current batch.
        /// The running batch (if any) keeps draining to completion; nothing
        /// is cancelled so finished items still commit (max 2/frame).
        /// </summary>
        private void DisableAutoContinuous()
        {
            _autoContinuous = false;
            if (_autoActive || (_autoRunner != null && !_autoRunner.IsCompleted))
            {
                Say("Auto continuous off: finishing current queue...");
            }
            else
            {
                Say("Auto continuous off.");
            }
        }

        /// <summary>
        /// Player position watcher: rescan whenever the player moved more
        /// than <see cref="AutoRescanMoveThreshold"/> tiles (Chebyshev) or
        /// crossed an 8x8 chunk boundary since the last scan. Fires while
        /// the runner is active too (truly continuous): ScanAndQueueContinuous
        /// appends to the shared live queue with dedup and live totals, and
        /// the running submit loop picks the appended ids up without restart
        /// (one runner at a time, each scan capped, finished items drain at
        /// max 2/frame while submits throttle on a busy server). Off just
        /// waits; a finishing runner (submits done, poll draining) also
        /// waits with the last-scan tile left alone so the move retries once
        /// the runner drains and the next batch starts.
        /// </summary>
        private void WatchPlayerForRescan()
        {
            if (IsDisposed || !_autoContinuous)
            {
                return;
            }

            if (World.Player == null || World.Map == null)
            {
                return;
            }

            int px = World.Player.X;
            int py = World.Player.Y;
            if (!_autoHasPos)
            {
                _autoLastPX = px;
                _autoLastPY = py;
                _autoHasPos = true;
                ScanAndQueueContinuous();

                return;
            }

            int moved = Math.Max(Math.Abs(px - _autoLastPX), Math.Abs(py - _autoLastPY));
            bool chunkChanged = (px / AutoChunkSize != _autoLastPX / AutoChunkSize)
                || (py / AutoChunkSize != _autoLastPY / AutoChunkSize);
            if (moved < AutoRescanMoveThreshold && !chunkChanged)
            {
                return;
            }

            if (_genTask != null && !_genTask.IsCompleted)
            {
                return;
            }

            ScanAndQueueContinuous();
        }

        /// <summary>
        /// Continuous rescan: visible statics/land in range that have
        /// neither an override/.art nor an HD PNG and were never queued
        /// this session, capped per scan (32). When no runner is in flight
        /// it launches a new streaming batch with the gump's current
        /// settings; when a runner is active it appends the new graphics to
        /// the shared live queue (_autoLiveQueue, deduped via _autoQueuedKeys)
        /// and bumps the live totals, and the running submit loop picks the
        /// appended ids up without restart. Totals accumulate so the status
        /// line keeps lifetime done/total. No-op when there is nothing new
        /// (the last-scan tile still advances so one spot says it once); a
        /// finishing runner (submits done, poll draining) waits with the
        /// last-scan tile left alone so the move retries after it drains.
        /// </summary>
        private void ScanAndQueueContinuous()
        {
            if (IsDisposed || !_autoContinuous)
            {
                return;
            }

            if (World.Player == null || World.Map == null)
            {
                return;
            }

            string prompt = _promptBox?.Text?.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                return;
            }

            float denoise = _denoiseSlider != null ? _denoiseSlider.Value / 100f : 0.65f;
            if (denoise <= 0f || denoise > 1f)
            {
                return;
            }

            bool flux = _backendPick != null && _backendPick.SelectedIndex == 1;
            string sampler = _samplerPick != null && _samplerPick.SelectedIndex >= 0 && _samplerPick.SelectedIndex < Samplers.Length
                ? Samplers[_samplerPick.SelectedIndex] : "euler";
            string scheduler = _schedulerPick != null && _schedulerPick.SelectedIndex >= 0 && _schedulerPick.SelectedIndex < Schedulers.Length
                ? Schedulers[_schedulerPick.SelectedIndex] : "normal";
            if (!int.TryParse(_seedBox?.Text?.Trim(), out int parsedSeed) || parsedSeed < 0)
            {
                parsedSeed = 0;
            }

            int seedBase = parsedSeed;
            bool seedGiven = int.TryParse(_seedBox?.Text?.Trim(), out int sb2) && sb2 >= 0;
            if (seedGiven)
            {
                seedBase = sb2;
            }
            if (!int.TryParse(_stepsBox?.Text?.Trim(), out int steps) || steps < 1 || steps > 100)
            {
                return;
            }

            if (!float.TryParse(_cfgBox?.Text?.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float cfg)
                || cfg < 0f || cfg > 30f)
            {
                return;
            }

            bool mask = _maskBox != null && _maskBox.IsChecked;
            bool includeName = _includeNameBox != null && _includeNameBox.IsChecked;
            bool depthEnabled = _depthBox != null && _depthBox.IsChecked;
            float depthStrength = _depthStrengthSlider != null ? _depthStrengthSlider.Value / 100f : ComfyClient.DefaultDepthStrength;
            float depthStart = _depthStartSlider != null ? _depthStartSlider.Value / 100f : ComfyClient.DefaultDepthStart;
            float depthEnd = _depthEndSlider != null ? _depthEndSlider.Value / 100f : ComfyClient.DefaultDepthEnd;
            if (depthEnabled && (depthStart < 0f || depthStart > 1f || depthEnd < 0f || depthEnd > 1f || depthStart > depthEnd))
            {
                Say("Depth start/end must be 0-100 with start <= end.");
                return;
            }

            // Finishing runner (submits done, poll draining, runner not yet
            // completed): no consumer left for appended ids, so wait with the
            // last-scan tile left alone; the move retries once it drains and
            // the next batch starts. Checked before Collect/build so no
            // _autoQueuedKeys are consumed for work that cannot submit.
            bool runnerActive = _autoActive || (_autoRunner != null && !_autoRunner.IsCompleted);
            if (runnerActive && _autoSubmitsFinished)
            {
                return;
            }

            bool landMode = LandMode;
            HashSet<ushort> found = CollectInRangeGraphics(landMode);
            int px = World.Player.X;
            int py = World.Player.Y;
            if (found.Count == 0)
            {
                _autoLastPX = px;
                _autoLastPY = py;
                _autoHasPos = true;
                return;
            }

            var sorted = new List<ushort>(found);
            sorted.Sort();
            var jobs = new List<AutoJob>();
            int skipped = 0;
            int nogfx = 0;
            int dup = 0;
            int seedIndex = 0;
            foreach (ushort g in sorted)
            {
                if (jobs.Count >= AutoContinuousMaxQueue)
                {
                    break;
                }

                string key = AutoKey(g, landMode);
                if (_autoQueuedKeys.Contains(key))
                {
                    dup++;
                    continue;
                }

                if (HasExistingPaint(g, landMode))
                {
                    skipped++;
                    continue;
                }

                ComfyClient.ArtShot shot = null;
                try
                {
                    shot = ComfyClient.ExtractArtShot(g, landMode);
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

                int seed = seedGiven ? seedBase + seedIndex : Random.Shared.Next(1, int.MaxValue);
                seedIndex++;
                byte[] depthPng = null;
                if (depthEnabled)
                {
                    try
                    {
                        depthPng = ComfyClient.BuildDepthMapPng(shot.WorkPng);
                    }
                    catch
                    {
                        nogfx++;
                        continue;
                    }
                }

                jobs.Add(new AutoJob { Shot = shot, Graphic = g, Seed = seed, IsLand = landMode, DepthPng = depthPng });
                _autoQueuedKeys.Add(key);
            }

            // Runner state may have shifted during the (main-thread) artwork
            // extraction above: re-read fresh. Finishing (submits done) was
            // already filtered before Collect, but re-check here in case the
            // submit loop exited mid-scan (rollback dedup, retry next tick
            // with the last-scan tile left alone).
            bool runnerLive = _autoActive || (_autoRunner != null && !_autoRunner.IsCompleted);
            if (runnerLive && _autoSubmitsFinished)
            {
                foreach (AutoJob rollback in jobs)
                {
                    try
                    {
                        _autoQueuedKeys.Remove(AutoKey(rollback.Graphic, rollback.IsLand));
                    }
                    catch
                    {
                    }
                }

                return;
            }

            _autoLastPX = px;
            _autoLastPY = py;
            _autoHasPos = true;
            if (jobs.Count == 0)
            {
                return;
            }

            string kindName = landMode ? "land" : "static";
            int capped = found.Count - skipped - dup - nogfx > jobs.Count ? 1 : 0;

            if (runnerLive)
            {
                // Truly continuous append: no new runner, no CTS/templates
                // reset, no _autoSubmitted wipe (the worker owns it). Per-job
                // prompt uses the gump's current settings; the running submit
                // loop drains the shared queue without restart and the totals
                // bump live so Drain shows completed/total streaming.
                foreach (AutoJob pending in jobs)
                {
                    pending.SubmitPrompt = WithTileName(prompt, TileName(pending.Graphic, pending.IsLand), includeName);
                    _autoLiveQueue.Enqueue(pending);
                }

                _autoTotal += jobs.Count;
                GameActions.Print(World,
                    $"Repaint auto-continuous ({kindName}): {found.Count} graphic(s), {skipped} with overrides/HD, {dup} already queued, {jobs.Count} appended live"
                    + (capped == 1 ? $" (capped at {AutoContinuousMaxQueue}/scan)" : "")
                    + (nogfx > 0 ? $", {nogfx} without artwork" : "")
                    + $" ({_autoDone}/{_autoTotal})");
                Say($"Auto {_autoDone}/{_autoTotal} (+{jobs.Count} as you move, continuous)...");
                return;
            }

            GameActions.Print(World,
                $"Repaint auto-continuous ({kindName}): {found.Count} graphic(s), {skipped} with overrides/HD, {dup} already queued, {jobs.Count} to generate"
                + (capped == 1 ? $" (capped at {AutoContinuousMaxQueue}/scan)" : "")
                + (nogfx > 0 ? $", {nogfx} without artwork" : ""));

            _autoPrompt = prompt;
            _autoFlux = flux;
            _autoSampler = sampler;
            _autoScheduler = scheduler;
            _autoSteps = steps;
            _autoCfg = cfg;
            _autoDenoise = denoise;
            _autoMask = mask;
            _autoIncludeName = includeName;
            _autoIsLand = landMode;
            _autoDepthEnabled = depthEnabled;
            _autoDepthStrength = depthStrength;
            _autoDepthStart = depthStart;
            _autoDepthEnd = depthEnd;
            if (!_autoActive && (_autoRunner == null || _autoRunner.IsCompleted))
            {
                _autoDepthFallback = false;
            }

            _autoTotal += jobs.Count;
            _autoSubmitted = 0;
            _autoSubmitsFinished = false;
            _autoLastDrainUtc = DateTime.MinValue;
            _autoRunnerError = "";
            _autoRunner = null;
            _autoActive = true;
            _autoCurrent = null;
            _genCts = new CancellationTokenSource();

            foreach (AutoJob pending in jobs)
            {
                pending.SubmitPrompt = WithTileName(_autoPrompt, TileName(pending.Graphic, pending.IsLand), includeName);
            }

            string repaintTemplate;
            string fluxTemplate;
            try
            {
                repaintTemplate = ComfyClient.LoadTemplate(ComfyClient.RepaintTemplatePath);
                fluxTemplate = ComfyClient.LoadTemplate(ComfyClient.FluxTemplatePath);
            }
            catch (Exception ex)
            {
                _autoActive = false;
                _autoTotal -= jobs.Count;
                Say($"Auto templates missing: {ex.Message}");
                return;
            }

            var batch = new List<AutoJob>(jobs);
            // Rescue any live-appended leftover that landed just as the
            // previous runner exited (tiny race: append bumped totals but no
            // consumer remained). Totals already include them, so include
            // without bumping again; normally empty.
            while (_autoLiveQueue.TryDequeue(out AutoJob leftover))
            {
                batch.Add(leftover);
            }

            string url = ComfyClient.ResolveUrl();
            CancellationToken ct = _genCts.Token;
            Say($"Auto {_autoDone}/{_autoTotal} submitted to ComfyUI (continuous)...");
            _autoRunner = Task.Run(() => RunAutoBatchAsync(
                batch, url, repaintTemplate, fluxTemplate,
                flux, sampler, scheduler, steps, cfg, denoise, mask, ct,
                depthEnabled, depthStrength, depthStart, depthEnd), ct);
        }

        /// <summary>
        /// Bulk repaint: every static or multi part (or, in Land mode, every
        /// land tile)
        /// within 17 tiles, deduped to unique graphics. Graphics with an
        /// existing override/.art or HD PNG are skipped (the repaint
        /// equivalent of the moge cache-hit), as are graphics already
        /// queued this session; the rest submit ALL prompts up front, then
        /// poll each prompt id in parallel and commit each image as it
        /// returns (max 2/frame drain; Close/Clear All cancels). Each
        /// completion writes its own override file immediately
        /// (Art/Statics or Art/Land per run kind, land diamond enforced);
        /// Clear All deletes the auto-created files again. Single-target
        /// flow is untouched. One kind per run: the Target combobox at
        /// queue time. The "Auto Queue (continuous)" checkbox calls this
        /// for the first batch and rescans on movement after it drains.
        /// </summary>
        private void StartAutoQueue()
        {
            if (_genTask != null && !_genTask.IsCompleted)
            {
                Say("Already generating...");
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
            bool includeName = _includeNameBox != null && _includeNameBox.IsChecked;
            bool depthEnabled = _depthBox != null && _depthBox.IsChecked;
            float depthStrength = _depthStrengthSlider != null ? _depthStrengthSlider.Value / 100f : ComfyClient.DefaultDepthStrength;
            float depthStart = _depthStartSlider != null ? _depthStartSlider.Value / 100f : ComfyClient.DefaultDepthStart;
            float depthEnd = _depthEndSlider != null ? _depthEndSlider.Value / 100f : ComfyClient.DefaultDepthEnd;
            if (depthEnabled && (depthStart < 0f || depthStart > 1f || depthEnd < 0f || depthEnd > 1f || depthStart > depthEnd))
            {
                Say("Depth start/end must be 0-100 with start <= end.");
                return;
            }

            bool landMode = LandMode;
            int seedBase = 0;
            bool seedGiven = int.TryParse(_seedBox.Text?.Trim(), out seedBase) && seedBase >= 0;

            string kindName = landMode ? "land" : "static";
            HashSet<ushort> found = CollectInRangeGraphics(landMode);

            if (found.Count == 0)
            {
                Say(landMode ? "No land tiles within 17 tiles." : "No statics within 17 tiles.");
                return;
            }

            var sorted = new List<ushort>(found);
            sorted.Sort();
            _autoPending.Clear();
            int skipped = 0;
            int nogfx = 0;
            int dup = 0;
            int seedIndex = 0;
            foreach (ushort g in sorted)
            {
                string key = AutoKey(g, landMode);
                if (_autoQueuedKeys.Contains(key))
                {
                    dup++;
                    continue;
                }

                if (HasExistingPaint(g, landMode))
                {
                    skipped++;
                    continue;
                }

                ComfyClient.ArtShot shot = null;
                try
                {
                    shot = ComfyClient.ExtractArtShot(g, landMode);
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
                byte[] depthPng = null;
                if (depthEnabled)
                {
                    try
                    {
                        depthPng = ComfyClient.BuildDepthMapPng(shot.WorkPng);
                    }
                    catch
                    {
                        nogfx++;
                        continue;
                    }
                }

                _autoPending.Add(new AutoJob { Shot = shot, Graphic = g, Seed = seed, IsLand = landMode, DepthPng = depthPng });
                _autoQueuedKeys.Add(key);
            }

            int queued = _autoPending.Count;
            GameActions.Print(World,
                $"Repaint auto ({kindName}): {found.Count} graphic(s), {skipped} with overrides/HD, {dup} already queued, {queued} to generate"
                + (nogfx > 0 ? $", {nogfx} without artwork" : ""));
            if (queued == 0)
            {
                if (World.Player != null)
                {
                    _autoLastPX = World.Player.X;
                    _autoLastPY = World.Player.Y;
                    _autoHasPos = true;
                }

                Say(skipped > 0
                    ? $"Auto done: {skipped} already repainted, nothing to generate."
                    : "Nothing with artwork to queue.");
                return;
            }

            if (World.Player != null)
            {
                _autoLastPX = World.Player.X;
                _autoLastPY = World.Player.Y;
                _autoHasPos = true;
            }

            _autoPrompt = prompt;
            _autoFlux = flux;
            _autoSampler = sampler;
            _autoScheduler = scheduler;
            _autoSteps = steps;
            _autoCfg = cfg;
            _autoDenoise = denoise;
            _autoMask = mask;
            _autoIncludeName = includeName;
            _autoIsLand = landMode;
            _autoDepthEnabled = depthEnabled;
            _autoDepthStrength = depthStrength;
            _autoDepthStart = depthStart;
            _autoDepthEnd = depthEnd;
            _autoDepthFallback = false;
            _autoTotal = queued;
            _autoDone = 0;
            _autoSubmitted = 0;
            _autoSubmitsFinished = false;
            _autoLastDrainUtc = DateTime.MinValue;
            _autoRunnerError = "";
            while (_autoResults.TryDequeue(out _)) { }
            while (_autoLiveQueue.TryDequeue(out _)) { }
            _autoRunner = null;
            _autoActive = true;
            _autoCurrent = null;
            _genCts = new CancellationTokenSource();

            // Per-graphic prompts keep the tiledata name + include-name
            // toggle exactly like the serial path did; templates load here
            // on the main thread (Godot FileAccess stays off workers).
            foreach (AutoJob pending in _autoPending)
            {
                pending.SubmitPrompt = WithTileName(_autoPrompt, TileName(pending.Graphic, pending.IsLand), includeName);
            }

            string repaintTemplate;
            string fluxTemplate;
            try
            {
                repaintTemplate = ComfyClient.LoadTemplate(ComfyClient.RepaintTemplatePath);
                fluxTemplate = ComfyClient.LoadTemplate(ComfyClient.FluxTemplatePath);
            }
            catch (Exception ex)
            {
                _autoActive = false;
                Say($"Auto templates missing: {ex.Message}");
                return;
            }

            var batch = new List<AutoJob>(_autoPending);
            _autoPending.Clear();
            string url = ComfyClient.ResolveUrl();
            CancellationToken ct = _genCts.Token;
            Say($"Auto 0/{_autoTotal} submitted to ComfyUI...");
            _autoRunner = Task.Run(() => RunAutoBatchAsync(
                batch, url, repaintTemplate, fluxTemplate,
                flux, sampler, scheduler, steps, cfg, denoise, mask, ct,
                depthEnabled, depthStrength, depthStart, depthEnd), ct);
        }

        /// <summary>
        /// Streaming pipeline worker: submits queue prompts one by one
        /// while a concurrent poll loop sweeps whatever is submitted so
        /// far, matching results back by prompt id. Each finished download
        /// (or per-item error) is enqueued to <see cref="_autoResults"/>
        /// the moment its poll completes -- nothing waits for all-done,
        /// so Update() commits images ingame one-by-one as they finish.
        /// Truly continuous: after the initial <paramref name="batch"/> the
        /// submit loop keeps draining <see cref="_autoLiveQueue"/> (walks
        /// mid-batch append there with live totals) without restart, staying
        /// alive while generations are in flight and closing when truly idle
        /// so Drain can say Watching. One retry per item on submit; transient
        /// poll errors retry until the per-run timeout. Commits happen on the
        /// main thread via <see cref="_autoResults"/> so Godot Images and
        /// override files stay off this worker. Throttle: sequential submits
        /// plus a pause whenever the server already holds more than
        /// <see cref="ComfyClient.AutoQueuePauseThreshold"/> prompts; the
        /// pause only holds submits (sliced 250ms waits, still
        /// cancellable) while the poll loop and the main-thread Drain keep
        /// flowing, so a busy server never blocks already-finished items.
        /// Polls sweep at 1s with at most
        /// <see cref="ComfyClient.AutoMaxInFlight"/> concurrent history
        /// reads in flight.
        /// </summary>
        private async Task RunAutoBatchAsync(
            List<AutoJob> batch, string url, string repaintTemplate, string fluxTemplate,
            bool flux, string sampler, string scheduler, int steps, float cfg,
            float denoise, bool mask, CancellationToken ct,
            bool depthEnabled = false, float depthStrength = ComfyClient.DefaultDepthStrength,
            float depthStart = ComfyClient.DefaultDepthStart, float depthEnd = ComfyClient.DefaultDepthEnd)
        {
            // Shared submit ledger: the submit loop below appends prompt
            // ids under lock while PollSubmittedAsync sweeps snapshots of
            // the same list. Lock scope is list surgery only; downloads
            // and history reads run outside it.
            var submitted = new List<AutoSubmitted>();
            var submittedLock = new object();
            var semaphore = new SemaphoreSlim(ComfyClient.AutoMaxInFlight);
            string backend = flux ? "Flux Klein" : "DreamShaper";
            var deadline = DateTime.UtcNow.AddMinutes(20);
            _autoSubmitsFinished = false;

            // Concurrent poll loop: every finished poll is enqueued as a
            // single result the moment it completes, so Drain commits it
            // on the next Update even while later submits are still
            // queueing (or throttled by a busy server).
            async Task PollSubmittedAsync()
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    List<AutoSubmitted> snapshot;
                    lock (submittedLock)
                    {
                        snapshot = new List<AutoSubmitted>(submitted);
                    }

                    if (snapshot.Count > 0)
                    {
                        var finished = new ConcurrentQueue<AutoSubmitted>();
                        var tasks = new List<Task>();
                        foreach (AutoSubmitted item in snapshot)
                        {
                            await semaphore.WaitAsync(ct);
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var files = await ComfyClient.TryFetchFilesAsync(
                                        url, item.PromptId,
                                        new[] { ".png", ".jpg", ".jpeg", ".webp" }, ct);
                                    if (files.Count > 0)
                                    {
                                        byte[] bytes = await ComfyClient.DownloadViewAsync(url, files[0], ct);
                                        _autoResults.Enqueue(new AutoBatchResult
                                        {
                                            Job = item.Job, PromptId = item.PromptId,
                                            Png = bytes, FileName = files[0].FileName,
                                            Prompt = item.Job.SubmitPrompt, Seed = item.Job.Seed,
                                            Backend = backend, Sampler = sampler,
                                            Scheduler = scheduler, Steps = steps, Cfg = cfg,
                                            Denoise = denoise, Mask = mask,
                                            IsLand = item.Job.IsLand,
                                            DepthEnabled = depthEnabled && !_autoDepthFallback, DepthStrength = depthStrength,
                                            DepthStart = depthStart, DepthEnd = depthEnd,
                                            DepthFallback = _autoDepthFallback,
                                        });
                                        finished.Enqueue(item);
                                    }

                                    string execError = await ComfyClient.GetPromptErrorAsync(url, item.PromptId, ct);
                                    if (execError != null)
                                    {
                                        string hint = depthEnabled
                                            ? (flux ? ComfyClient.FluxDepthControlModel : ComfyClient.DepthControlModel)
                                            : null;
                                        Exception err = ComfyClient.ThrowForExecutionError(execError, item.PromptId, hint);
                                        _autoResults.Enqueue(new AutoBatchResult
                                        {
                                            Job = item.Job, PromptId = item.PromptId,
                                            Error = err.Message,
                                            Prompt = item.Job.SubmitPrompt, Seed = item.Job.Seed,
                                            Backend = backend, Sampler = sampler,
                                            Scheduler = scheduler, Steps = steps, Cfg = cfg,
                                            Denoise = denoise, Mask = mask,
                                            IsLand = item.Job.IsLand,
                                            DepthEnabled = depthEnabled && !_autoDepthFallback, DepthStrength = depthStrength,
                                            DepthStart = depthStart, DepthEnd = depthEnd,
                                            DepthFallback = _autoDepthFallback,
                                        });
                                        finished.Enqueue(item);
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch
                                {
                                    // Transient history/download error: retry next sweep.
                                }
                                finally
                                {
                                    try { semaphore.Release(); } catch { }
                                }
                            }, ct));
                        }

                        try
                        {
                            await Task.WhenAll(tasks);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }

                        if (!finished.IsEmpty)
                        {
                            var doneIds = new HashSet<string>();
                            foreach (AutoSubmitted f in finished)
                            {
                                doneIds.Add(f.PromptId);
                            }

                            int remainingCount;
                            lock (submittedLock)
                            {
                                submitted.RemoveAll(r => doneIds.Contains(r.PromptId));
                                remainingCount = submitted.Count;
                            }

                            _genStatus = $"Auto {_autoDone}/{_autoTotal} ({remainingCount} generating, {_autoSubmitted} submitted)...";
                        }
                    }

                    bool doneSubmitting = _autoSubmitsFinished;
                    int pending;
                    lock (submittedLock)
                    {
                        pending = submitted.Count;
                    }

                    if (doneSubmitting && pending == 0)
                    {
                        break;
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        List<AutoSubmitted> left;
                        lock (submittedLock)
                        {
                            left = new List<AutoSubmitted>(submitted);
                            submitted.Clear();
                        }

                        foreach (AutoSubmitted item in left)
                        {
                            _autoResults.Enqueue(new AutoBatchResult
                            {
                                Job = item.Job, PromptId = item.PromptId,
                                Error = "timed out waiting for ComfyUI",
                                Prompt = item.Job.SubmitPrompt, Seed = item.Job.Seed,
                                Backend = backend, Sampler = sampler, Scheduler = scheduler,
                                Steps = steps, Cfg = cfg, Denoise = denoise,
                                Mask = mask, IsLand = item.Job.IsLand,
                                DepthEnabled = depthEnabled && !_autoDepthFallback, DepthStrength = depthStrength,
                                DepthStart = depthStart, DepthEnd = depthEnd,
                                DepthFallback = _autoDepthFallback,
                            });
                        }

                        break;
                    }

                    if (pending == 0 && !doneSubmitting)
                    {
                        await Task.Delay(500, ct);
                    }
                    else if (pending > 0)
                    {
                        await Task.Delay(1000, ct);
                    }
                    else
                    {
                        await Task.Delay(250, ct);
                    }
                }
            }

            Task pollTask = PollSubmittedAsync();

            try
            {
                // Truly-continuous submit: initial batch plus whatever
                // ScanAndQueueContinuous appends to _autoLiveQueue while this
                // runner is active (walks mid-batch). Totals were already
                // bumped live on append; this loop picks the appended ids up
                // without restart. Caps stay: 32/scan upstream, max in-flight
                // 4 + queue-depth throttle below. Exits only when there is no
                // local pending, no live-appended, and (when continuous on)
                // no generations still in flight -- i.e. truly idle, so Drain
                // can close out to Watching; next move starts a new batch.
                // Toggle off breaks once local + live drain (finishing
                // current queue, in-flight still polls/drains).
                var pendingLocal = new Queue<AutoJob>(batch);
                while (_autoLiveQueue.TryDequeue(out AutoJob pre))
                {
                    pendingLocal.Enqueue(pre);
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    while (_autoLiveQueue.TryDequeue(out AutoJob appended))
                    {
                        pendingLocal.Enqueue(appended);
                    }

                    if (pendingLocal.Count == 0)
                    {
                        if (!_autoContinuous)
                        {
                            break;
                        }

                        int inFlight;
                        lock (submittedLock)
                        {
                            inFlight = submitted.Count;
                        }

                        if (inFlight == 0)
                        {
                            break;
                        }

                        try { await Task.Delay(500, ct); } catch (OperationCanceledException) { throw; }
                        continue;
                    }

                    AutoJob job = pendingLocal.Dequeue();
                    string promptId = null;
                    string lastError = "";
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        try
                        {
                            void SubmitReport(string s)
                            {
                                if (s == ComfyClient.DepthFallbackStatus)
                                {
                                    _autoDepthFallback = true;
                                }
                            }

                            promptId = flux
                                ? await ComfyClient.SubmitFluxEditAsync(
                                    url, fluxTemplate, job.Shot.WorkPng, job.SubmitPrompt,
                                    job.Seed, cfg, steps, sampler, ct,
                                    depthEnabled ? job.DepthPng : null, depthStrength, depthStart, depthEnd,
                                    report: SubmitReport)
                                : await ComfyClient.SubmitRepaintAsync(
                                    url, repaintTemplate, job.Shot.WorkPng, job.SubmitPrompt,
                                    job.Seed, denoise, sampler, scheduler, steps, cfg, ct,
                                    depthEnabled ? job.DepthPng : null, depthStrength, depthStart, depthEnd,
                                    report: SubmitReport);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex.Message;
                            if (attempt == 0)
                            {
                                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { throw; }
                            }
                        }
                    }

                    if (promptId == null)
                    {
                        _autoResults.Enqueue(new AutoBatchResult
                        {
                            Job = job, PromptId = "", Error = lastError,
                            Prompt = job.SubmitPrompt, Seed = job.Seed, Backend = backend,
                            Sampler = sampler, Scheduler = scheduler, Steps = steps,
                            Cfg = cfg, Denoise = denoise, Mask = mask, IsLand = job.IsLand,
                            DepthEnabled = depthEnabled && !_autoDepthFallback, DepthStrength = depthStrength,
                            DepthStart = depthStart, DepthEnd = depthEnd,
                            DepthFallback = _autoDepthFallback,
                        });
                        continue;
                    }

                    lock (submittedLock)
                    {
                        submitted.Add(new AutoSubmitted { Job = job, PromptId = promptId });
                    }

                    _autoSubmitted++;
                    _genStatus = $"Auto {_autoDone}/{_autoTotal} submitted {_autoSubmitted}/{_autoTotal}...";
                    if (_autoSubmitted % ComfyClient.AutoMaxInFlight == 0)
                    {
                        try
                        {
                            int depth = await ComfyClient.GetQueuePendingAsync(url, ct);
                            if (depth > ComfyClient.AutoQueuePauseThreshold)
                            {
                                _genStatus = $"Auto {_autoDone}/{_autoTotal} submitted {_autoSubmitted}/{_autoTotal} - server busy ({depth} queued), throttling submits (finished items still drain)...";
                                // Sliced wait: submits pause here but the
                                // poll loop above and the main-thread Drain
                                // keep flowing, so already-finished items
                                // still commit ingame during the pause.
                                for (int i = 0; i < 8; i++)
                                {
                                    await Task.Delay(250, ct);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                _autoSubmitsFinished = true;
            }

            int queuedNow;
            lock (submittedLock)
            {
                queuedNow = submitted.Count;
            }

            _genStatus = $"Auto {_autoDone}/{_autoTotal} generating ({queuedNow} queued, {_autoSubmitted} submitted)...";
            await pollTask;
        }

        /// <summary>
        /// Main-thread drain: commit finished downloads to their override
        /// + HD files as they arrive, keeping completed/total counts live.
        /// Runs inside Update() every tick; each tick commits at most
        /// <see cref="AutoMaxCommitsPerFrame"/> items so simultaneous
        /// completions apply ingame one-by-one without hitching, and the
        /// 0.25s backup below re-pumps within the same tick budget when a
        /// hitch leaves finished items sitting past the interval. Never
        /// blocks and never waits for all-done; the run only closes out
        /// once the queue is empty and every item is counted.
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
            int budget = AutoMaxCommitsPerFrame;
            while (budget > 0 && _autoResults.TryDequeue(out AutoBatchResult result))
            {
                budget--;
                progressed = true;
                CommitAutoBatchResult(result);
            }

            if (progressed)
            {
                _autoLastDrainUtc = DateTime.UtcNow;
            }

            // 0.25s backup pump: the capped pass above normally leaves
            // leftovers for the next Update (~16ms away); if a hitch ever
            // lets finished items sit past the interval, spend whatever
            // tick budget remains right now instead of waiting another
            // tick. Total per tick never exceeds AutoMaxCommitsPerFrame,
            // and the server-busy submit throttle (worker thread) never
            // touches this path, so finished items drain straight through
            // a throttled submit pause.
            if (budget > 0 && !_autoResults.IsEmpty &&
                DateTime.UtcNow - _autoLastDrainUtc >= AutoDrainBackupInterval)
            {
                while (budget > 0 && _autoResults.TryDequeue(out AutoBatchResult result))
                {
                    budget--;
                    progressed = true;
                    CommitAutoBatchResult(result);
                }

                if (progressed)
                {
                    _autoLastDrainUtc = DateTime.UtcNow;
                }
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
                        _autoPending.Clear();
                        while (_autoLiveQueue.TryDequeue(out _)) { }
                        _autoRunner = null;
                        Say("Auto cancelled.");
                        try
                        {
                            string url = ComfyClient.ResolveUrl();
                            _ = ComfyClient.InterruptAsync(url, CancellationToken.None);
                        }
                        catch
                        {
                        }

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
                        string doneKind = _autoIsLand ? "land" : "static";
                        GameActions.Print(World,
                            $"Repaint auto done ({doneKind}): {_autoDone}/{_autoTotal} repainted.");
                        Say(_autoDepthFallback
                            ? ComfyClient.DepthFallbackStatus
                            : string.IsNullOrEmpty(_autoRunnerError)
                            ? $"Auto done: {_autoDone}/{_autoTotal} {doneKind} repainted."
                                + (_autoContinuous ? " Watching as you move..." : "")
                            : $"Auto done: {_autoDone}/{_autoTotal} ({_autoRunnerError}).");
                    }

                    _autoRunnerError = "";
                }
                else if (_autoResults.IsEmpty && runner.IsFaulted && _autoDone < _autoTotal)
                {
                    // Fatal submit-loop failure with nothing more coming:
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
                string doneKind = _autoIsLand ? "land" : "static";
                GameActions.Print(World,
                    $"Repaint auto done ({doneKind}): {_autoDone}/{_autoTotal} repainted.");
                Say(_autoDepthFallback
                    ? ComfyClient.DepthFallbackStatus
                    : $"Auto done: {_autoDone}/{_autoTotal} {doneKind} repainted."
                        + (_autoContinuous ? " Watching as you move..." : ""));
            }
        }

        /// <summary>
        /// Main-thread commit for one polled image (or its per-item error):
        /// decode, HD pre-downscale save, native resize, silhouette mask,
        /// .art encode + override write + live rescan, exactly like the old
        /// serial OnAutoRepainted but without chaining the next item (items
        /// land in completion order, each counted toward completed/total).
        /// </summary>
        private void CommitAutoBatchResult(AutoBatchResult result)
        {
            if (result == null || result.Job == null)
            {
                _autoDone++;
                return;
            }

            if (result.DepthFallback)
            {
                _autoDepthFallback = true;
            }

            ushort graphic = result.Job.Graphic;
            if (!string.IsNullOrEmpty(result.Error) || result.Png == null)
            {
                Say($"Auto 0x{graphic:X4} failed: {result.Error}");
                _autoDone++;
                return;
            }

            var gen = new GenJob
            {
                Result = new ComfyClient.PngResult { Png = result.Png, FileName = result.FileName },
                Shot = result.Job.Shot, Graphic = graphic, IsLand = result.IsLand,
                Mask = result.Mask, Prompt = result.Prompt, Seed = result.Seed,
                Backend = result.Backend, Sampler = result.Sampler,
                Scheduler = result.Scheduler, Steps = result.Steps,
                Cfg = result.Cfg, Denoise = result.Denoise,
                DepthEnabled = result.DepthEnabled, DepthStrength = result.DepthStrength,
                DepthStart = result.DepthStart, DepthEnd = result.DepthEnd,
            };

            using var image = new Godot.Image();
            if (image.LoadPngFromBuffer(result.Png) != Godot.Error.Ok)
            {
                Say($"Auto 0x{graphic:X4}: result is not a PNG.");
                _autoDone++;
                return;
            }

            if (image.GetFormat() != Godot.Image.Format.Rgba8)
            {
                image.Convert(Godot.Image.Format.Rgba8);
            }

            SaveHdPreDownscale(gen, image);

            if (image.GetWidth() != result.Job.Shot.Width || image.GetHeight() != result.Job.Shot.Height)
            {
                image.Resize(result.Job.Shot.Width, result.Job.Shot.Height, Godot.Image.Interpolation.Trilinear);
            }

            byte[] rgba = image.GetData();
            bool autoMask = result.Mask || result.IsLand;
            if (autoMask && image.GetWidth() == result.Job.Shot.Width && image.GetHeight() == result.Job.Shot.Height)
            {
                for (int i = 0; i < result.Job.Shot.Width * result.Job.Shot.Height; i++)
                {
                    if (result.Job.Shot.Rgba[i * 4 + 3] == 0)
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
                Say($"Auto 0x{graphic:X4}: unencodable: {ex.Message}");
                _autoDone++;
                return;
            }

            try
            {
                string dir = Path.Combine(ArtLoader.OverrideDir(), "Art",
                    result.IsLand ? "Land" : "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{graphic}.art"), art);
                Client.Game.UO.FileManager.Arts.RescanOverrides();
                if (result.IsLand)
                {
                    Client.Game.UO.Arts.RefreshLand(graphic);
                }
                else
                {
                    Client.Game.UO.Arts.RefreshStatic(graphic);
                }

                ThemeManager.Reapply(World);
            }
            catch (Exception ex)
            {
                Say($"Auto 0x{graphic:X4}: override failed: {ex.Message}");
                _autoDone++;
                return;
            }

            bool alreadyTracked = false;
            foreach (var tracked in _autoGraphics)
            {
                if (tracked.Graphic == graphic && tracked.IsLand == result.IsLand)
                {
                    alreadyTracked = true;
                    break;
                }
            }

            if (!alreadyTracked)
            {
                _autoGraphics.Add((graphic, result.IsLand));
            }

            string msg = $"0x{graphic:X4} repainted ({(autoMask ? "masked" : "unmasked")})";
            if (result.DepthEnabled)
            {
                msg += $" depth {result.DepthStrength:0.00} {result.DepthStart:0.00}-{result.DepthEnd:0.00}";
            }

            GameActions.Print(World, "Repaint auto: " + msg);
            _autoDone++;
            Say($"Auto {_autoDone}/{_autoTotal} 0x{graphic:X4}: done.");
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
            bool jobIsLand = job.IsLand;
            bool includeName = _autoIncludeName;
            string prompt = WithTileName(_autoPrompt, TileName(job.Graphic, jobIsLand), includeName);
            bool flux = _autoFlux;
            string sampler = _autoSampler;
            string scheduler = _autoScheduler;
            int steps = _autoSteps;
            float cfg = _autoCfg;
            float denoise = _autoDenoise;
            bool mask = _autoMask;
            ComfyClient.ArtShot shot = job.Shot;
            int seed = job.Seed;
            bool depthEnabled = _autoDepthEnabled;
            float depthStrength = _autoDepthStrength;
            float depthStart = _autoDepthStart;
            float depthEnd = _autoDepthEnd;
            byte[] depthPng = depthEnabled ? job.DepthPng : null;
            string url = ComfyClient.ResolveUrl();
            bool depthFallback = false;
            _genTask = Task.Run(async () =>
            {
                void Report(string s)
                {
                    _genStatus = $"Auto {done + 1}/{total} 0x{graphic:X4}: {s}";
                    if (s == ComfyClient.DepthFallbackStatus)
                    {
                        depthFallback = true;
                    }
                }

                ComfyClient.PngResult result = flux
                    ? await ComfyClient.ImageToImageFluxAsync(
                        url, shot.WorkPng, prompt, seed, cfg, steps, sampler, Report, ct,
                        depthPng: depthPng, depthStrength: depthStrength, depthStart: depthStart, depthEnd: depthEnd)
                    : await ComfyClient.ImageToImageAsync(
                        url, shot.WorkPng, prompt, seed, denoise, Report, ct,
                        sampler: sampler, scheduler: scheduler, steps: steps, cfg: cfg,
                        depthPng: depthPng, depthStrength: depthStrength, depthStart: depthStart, depthEnd: depthEnd);
                return new GenJob
                {
                    Result = result, Shot = shot, Graphic = graphic, IsLand = jobIsLand, Mask = mask,
                    Prompt = prompt, Seed = seed,
                    Backend = flux ? "Flux Klein" : "DreamShaper",
                    Sampler = sampler, Scheduler = scheduler, Steps = steps, Cfg = cfg, Denoise = denoise,
                    DepthEnabled = depthEnabled && !depthFallback, DepthStrength = depthStrength, DepthStart = depthStart, DepthEnd = depthEnd,
                    DepthFallback = depthFallback,
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
                string doneKind = _autoIsLand ? "land" : "static";
                GameActions.Print(World,
                    $"Repaint auto done ({doneKind}): {_autoDone}/{_autoTotal} repainted.");
                Say(_autoDepthFallback
                    ? ComfyClient.DepthFallbackStatus
                    : $"Auto done: {_autoDone}/{_autoTotal} {doneKind} repainted.");
            }
        }

        private void OnAutoRepainted(AutoJob job, GenJob result)
        {
            if (result != null && result.DepthFallback)
            {
                _autoDepthFallback = true;
            }

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

            // Enhanced HD: the pre-downscale frame, before the native
            // resize below (mask at HD, land diamond enforced).
            SaveHdPreDownscale(result, image);

            if (image.GetWidth() != job.Shot.Width || image.GetHeight() != job.Shot.Height)
            {
                image.Resize(job.Shot.Width, job.Shot.Height, Godot.Image.Interpolation.Trilinear);
            }

            byte[] rgba = image.GetData();
            // Land enforces its diamond even unmasked (see OnRepainted).
            bool autoMask = _autoMask || result.IsLand;
            if (autoMask && image.GetWidth() == job.Shot.Width && image.GetHeight() == job.Shot.Height)
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
                string dir = Path.Combine(ArtLoader.OverrideDir(), "Art",
                    result.IsLand ? "Land" : "Statics");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{job.Graphic}.art"), art);
                Client.Game.UO.FileManager.Arts.RescanOverrides();
                if (result.IsLand)
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
                Say($"Auto 0x{job.Graphic:X4}: override failed: {ex.Message}");
                _autoDone++;
                ContinueAutoQueue();
                return;
            }

            bool alreadyTracked = false;
            foreach (var tracked in _autoGraphics)
            {
                if (tracked.Graphic == job.Graphic && tracked.IsLand == result.IsLand)
                {
                    alreadyTracked = true;
                    break;
                }
            }

            if (!alreadyTracked)
            {
                _autoGraphics.Add((job.Graphic, result.IsLand));
            }

            string msg = $"0x{job.Graphic:X4} repainted ({(autoMask ? "masked" : "unmasked")})";
            if (result.DepthEnabled)
            {
                msg += $" depth {result.DepthStrength:0.00} {result.DepthStart:0.00}-{result.DepthEnd:0.00}";
            }

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
        /// unless they share a graphic (same file, same result). Cancels the
        /// parallel batch runner mid-run (submit burst + poll sweeps stop on
        /// the shared CTS) and best-efforts a server /interrupt.
        /// </summary>
        private void ClearAuto()
        {
            bool hadRun = _autoActive || _autoRunner != null || _autoCurrent != null;
            _autoActive = false;
            // Clear All stops the continuous watcher too: otherwise the
            // next player-move tick would rescan and re-queue what was
            // just deleted. The checkbox (and its persisted profile)
            // follows so the gump says what it does.
            _autoContinuous = false;
            try
            {
                _autoSuppressQueueBoxEvent = true;
                if (_autoQueueBox != null)
                {
                    _autoQueueBox.IsChecked = false;
                }
            }
            finally
            {
                _autoSuppressQueueBoxEvent = false;
            }

            try
            {
                Profile profile = ProfileManager.CurrentProfile;
                if (profile != null)
                {
                    profile.RepaintAutoQueue = false;
                }
            }
            catch
            {
            }

            _autoPending.Clear();
            _autoCurrent = null;
            while (_autoLiveQueue.TryDequeue(out _)) { }
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

                try
                {
                    string url = ComfyClient.ResolveUrl();
                    _ = ComfyClient.InterruptAsync(url, CancellationToken.None);
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
                var refreshedLand = new HashSet<ushort>();
                var refreshedStatics = new HashSet<ushort>();
                foreach (var entry in new List<(ushort Graphic, bool IsLand)>(_autoGraphics))
                {
                    try
                    {
                        string path = OverridePathFor(entry.Graphic, entry.IsLand);
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            n++;
                        }

                        // Enhanced HD: the pre-downscale PNG plus row.
                        ArtLoader.DeleteHd(entry.IsLand, entry.Graphic);

                        if (entry.IsLand)
                        {
                            refreshedLand.Add(entry.Graphic);
                        }
                        else
                        {
                            refreshedStatics.Add(entry.Graphic);
                        }
                    }
                    catch
                    {
                    }
                }

                _autoGraphics.Clear();
                try
                {
                    Client.Game.UO.FileManager.Arts.RescanOverrides();
                    foreach (ushort g in refreshedLand)
                    {
                        Client.Game.UO.Arts.RefreshLand(g);
                    }

                    foreach (ushort g in refreshedStatics)
                    {
                        Client.Game.UO.Arts.RefreshStatic(g);
                    }

                    ThemeManager.Reapply(World);
                }
                catch
                {
                }
            }

            _autoQueuedKeys.Clear();

            Say(n > 0 ? $"Cleared {n} auto repaint(s)." : "Nothing to clear.");
        }
    }
}
