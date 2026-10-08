// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.IO.Audio;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// In-game voice casting: target a mobile, hear which voice it has,
    /// cycle the enrolled voices, assign one (saved to voices.json rules),
    /// design a new one for it (slow: Qwen voice design, fire-and-forget),
    /// or make the pick the fallback for every unmapped mobile. Opened with
    /// the "-voice" client command. Spoken lines themselves already flow
    /// through VoiceManager whenever a mapped mobile talks.
    /// </summary>
    internal sealed class VoiceGump : Gump
    {
        private const int W = 320;
        private const int H = 400;

        private readonly Label _mobLabel;
        private readonly Label _currentLabel;
        private readonly Label _voiceLabel;
        private readonly Label _statusLabel;

        private uint _serial;
        private string _mobName = "";
        private ushort _mobBody;
        private readonly List<string> _voices = new();
        private int _voiceIdx = -1;
        private uint _designSerial;

        private enum Buttons
        {
            Target,
            VoicePrev,
            VoiceNext,
            Assign,
            Design,
            Test,
            SetDefault,
            Close
        }

        public VoiceGump(World world)
            : base(world, 140, 120)
        {
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("Voice", true, 0x0481) { X = 12, Y = 8 });

            var close = new NiceButton(W - 30, 6, 22, 20, ButtonAction.Activate, "X")
            {
                ButtonParameter = (int)Buttons.Close,
                IsSelectable = false
            };
            Add(close);

            int y = 34;
            Add(new Label("Mobile:", true, 0x0386) { X = 12, Y = y });
            _mobLabel = new Label("<none>", true, 0x0021, 130) { X = 80, Y = y };
            Add(_mobLabel);
            Add(new NiceButton(210, y - 2, 98, 22, ButtonAction.Activate, "Target")
            {
                ButtonParameter = (int)Buttons.Target,
                IsSelectable = false
            });

            y += 30;
            Add(new Label("Speaks:", true, 0x0386) { X = 12, Y = y });
            _currentLabel = new Label("<none>", true, 0x0021, 200) { X = 80, Y = y };
            Add(_currentLabel);

            y += 30;
            Add(new Label("Voice:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(80, y - 2, 26, 22, ButtonAction.Activate, "<")
            {
                ButtonParameter = (int)Buttons.VoicePrev,
                IsSelectable = false
            });
            _voiceLabel = new Label("<none>", true, 0x0021, 140) { X = 110, Y = y };
            Add(_voiceLabel);
            Add(new NiceButton(262, y - 2, 26, 22, ButtonAction.Activate, ">")
            {
                ButtonParameter = (int)Buttons.VoiceNext,
                IsSelectable = false
            });

            y += 34;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Assign")
            {
                ButtonParameter = (int)Buttons.Assign,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Design")
            {
                ButtonParameter = (int)Buttons.Design,
                IsSelectable = false
            });

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Test")
            {
                ButtonParameter = (int)Buttons.Test,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Set Default")
            {
                ButtonParameter = (int)Buttons.SetDefault,
                IsSelectable = false
            });

            y += 32;
            _statusLabel = new Label(string.Empty, true, 0x0021, W - 24) { X = 12, Y = y };
            Add(_statusLabel);

            Width = W;
            Height = H;
            RefreshVoices();
            UpdateLabels();
            SetInScreen();
            if (_voices.Count == 0)
            {
                Say("No enrolled voices (voice.py enroll).");
            }
        }

        private void RefreshVoices()
        {
            _voices.Clear();
            _voices.AddRange(VoiceManager.EnrolledVoices());
            _voiceIdx = _voices.Count > 0 ? 0 : -1;
        }

        private string CurrentVoice => _voiceIdx >= 0 && _voiceIdx < _voices.Count ? _voices[_voiceIdx] : null;

        private void UpdateLabels()
        {
            _mobLabel.Text = _serial == 0 ? "<none> (Target first)" : $"{_mobName} #{_serial}";
            _currentLabel.Text = _serial == 0
                ? "<none>"
                : VoiceManager.ResolveVoice(_serial, _mobName, _mobBody) ?? "<silent>";
            _voiceLabel.Text = CurrentVoice ?? "<none>";
        }

        private void Say(string s)
        {
            _statusLabel.Text = s;
        }

        public override void Update()
        {
            base.Update();
            if (IsDisposed || _designSerial == 0)
            {
                return;
            }

            // The design worker reports through the shared driver status;
            // pick up completion (or failure) so the gump stops showing the
            // click-time message after the voice lands.
            string st = VoiceManager.Status ?? "";
            if (st.StartsWith("designed"))
            {
                _designSerial = 0;
                RefreshVoices();
                string resolved = _serial == 0 ? null
                    : VoiceManager.ResolveVoice(_serial, _mobName, _mobBody);
                int at = resolved == null ? -1 : _voices.FindIndex(v =>
                    string.Equals(v, resolved, System.StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    _voiceIdx = at;
                }

                UpdateLabels();
                Say(st + ".");
            }
            else if (st.StartsWith("error"))
            {
                _designSerial = 0;
                Say(st);
            }
        }

        public override void OnButtonClick(int buttonID)
        {
            switch (buttonID)
            {
                case (int)Buttons.Target:
                    if (World.TargetManager.IsTargeting)
                    {
                        World.TargetManager.CancelTarget();
                    }

                    World.TargetManager.SetTargeting(
                        (GameObject obj) =>
                        {
                            if (obj is Mobile mob && !ReferenceEquals(mob, World.Player))
                            {
                                _serial = mob.Serial;
                                _mobName = mob.Name ?? "";
                                _mobBody = mob.Graphic;
                                string resolved = VoiceManager.ResolveVoice(_serial, _mobName, _mobBody);
                                int at = _voices.FindIndex(v =>
                                    string.Equals(v, resolved, System.StringComparison.OrdinalIgnoreCase));
                                if (at >= 0)
                                {
                                    _voiceIdx = at;
                                }

                                GameActions.Print(World, $"Voice target: {_mobName} #{_serial} speaks '{resolved ?? "<silent>"}'");
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click a mobile...");
                    break;

                case (int)Buttons.VoicePrev:
                    if (_voices.Count > 0)
                    {
                        _voiceIdx = (_voiceIdx - 1 + _voices.Count) % _voices.Count;
                        UpdateLabels();
                    }
                    break;

                case (int)Buttons.VoiceNext:
                    if (_voices.Count > 0)
                    {
                        _voiceIdx = (_voiceIdx + 1) % _voices.Count;
                        UpdateLabels();
                    }
                    break;

                case (int)Buttons.Assign:
                    if (_serial == 0 || string.IsNullOrEmpty(CurrentVoice))
                    {
                        Say("Target a mobile and pick a voice first.");
                    }
                    else if (VoiceManager.AssignVoice(_serial, CurrentVoice))
                    {
                        Say($"{_mobName} now speaks '{CurrentVoice}'.");
                        GameActions.Print(World, $"Voice: {_mobName} #{_serial} -> '{CurrentVoice}'");
                        UpdateLabels();
                    }
                    else
                    {
                        Say("Assign failed.");
                    }
                    break;

                case (int)Buttons.Design:
                    if (_serial == 0)
                    {
                        Say("Target a mobile first.");
                    }
                    else
                    {
                        VoiceManager.DesignVoice((int)_serial, _mobName, _mobBody, "mobile");
                        _designSerial = _serial;
                        Say($"Designing a voice for {_mobName} (slow)...");
                    }
                    break;

                case (int)Buttons.Test:
                    if (_serial == 0 || string.IsNullOrEmpty(CurrentVoice))
                    {
                        Say("Target a mobile and pick a voice first.");
                    }
                    else
                    {
                        VoiceManager.Speak("Greetings, traveler.", 0, CurrentVoice);
                        Say($"Speaking as '{CurrentVoice}'.");
                    }
                    break;

                case (int)Buttons.SetDefault:
                    if (string.IsNullOrEmpty(CurrentVoice))
                    {
                        Say("Pick a voice first.");
                    }
                    else if (VoiceManager.SetDefaultVoice(CurrentVoice))
                    {
                        Say($"'{CurrentVoice}' voices every unmapped mobile.");
                        UpdateLabels();
                    }
                    else
                    {
                        Say("Default failed.");
                    }
                    break;

                case (int)Buttons.Close:
                    Dispose();
                    break;
            }
        }
    }
}
