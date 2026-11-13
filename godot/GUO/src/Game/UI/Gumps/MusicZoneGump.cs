// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GUO.Assets;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.IO.Audio;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// Ambient audio zones: rectangular areas with a playlist of prepared
    /// tracks, on a Music/SFX layer switch (own zones, player and manifest
    /// each; neither cancels the other). Corner A/B capture the player tile,
    /// the playlist is filenames under the music dir. Save writes
    /// music.json/sfx.json; the scene players cycle tracks while the player
    /// stands inside. Opened with "-audiozones" ("-musiczone" still works,
    /// "-sfxzone" starts on the SFX layer).
    /// </summary>
    internal sealed class MusicZoneGump : Gump
    {
        private const int W = 340;
        private const int H = 510;

        private readonly Label _zoneLabel;
        private readonly Label _rectLabel;
        private readonly StbTextBox _nameBox;
        private readonly StbTextBox _playlistBox;
        private readonly Label _filesLabel;
        private readonly Label _hereLabel;
        private readonly Label _statusLabel;
        private readonly ZoneOutline _outline;
        private readonly Combobox _layerPick;
        private bool _layerSfx;

        private int _zoneIdx = -1;
        private int _ax, _ay;
        private bool _hasA;
        private enum Buttons
        {
            ZonePrev,
            ZoneNext,
            CornerA,
            CornerB,
            New,
            Delete,
            Save,
            ClearTracks,
            ShowZone,
            Close
        }

        public MusicZoneGump(World world, bool sfx = false)
            : base(world, 140, 100)
        {
            _layerSfx = sfx;
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("Audio Zones", true, 0x0481) { X = 12, Y = 8 });

            var close = new NiceButton(W - 30, 6, 22, 20, ButtonAction.Activate, "X")
            {
                ButtonParameter = (int)Buttons.Close,
                IsSelectable = false
            };
            Add(close);

            int y = 34;
            Add(new Label("Zone:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "<")
            {
                ButtonParameter = (int)Buttons.ZonePrev,
                IsSelectable = false
            });
            _zoneLabel = new Label("<new>", true, 0x0021, 150) { X = 100, Y = y };
            Add(_zoneLabel);
            Add(new NiceButton(262, y - 2, 26, 22, ButtonAction.Activate, ">")
            {
                ButtonParameter = (int)Buttons.ZoneNext,
                IsSelectable = false
            });

            y += 30;
            Add(new Label("Layer:", true, 0x0386) { X = 12, Y = y });
            _layerPick = new Combobox(70, y - 2, 130, new[] { "Music", "SFX" }, _layerSfx ? 1 : 0);
            _layerPick.OnOptionSelected += (s, i) => SwitchLayer(i == 1);
            Add(_layerPick);

            y += 30;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Corner A here")
            {
                ButtonParameter = (int)Buttons.CornerA,
                IsSelectable = false
            });
            Add(new NiceButton(168, y, 130, 24, ButtonAction.Activate, "Corner B here")
            {
                ButtonParameter = (int)Buttons.CornerB,
                IsSelectable = false
            });

            y += 30;
            _rectLabel = new Label("<no corners>", true, 0x0021, W - 24) { X = 12, Y = y };
            Add(_rectLabel);

            y += 26;
            Add(new Label("Name:", true, 0x0386) { X = 12, Y = y });
            _nameBox = new StbTextBox(1, 60, 180, true, FontStyle.None, 0x0021)
            {
                X = 100, Y = y, Width = 180, Height = 22
            };
            Add(_nameBox);

            y += 30;
            Add(new Label("Playlist (files):", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(262, y - 2, 56, 22, ButtonAction.Activate, "Clear")
            {
                ButtonParameter = (int)Buttons.ClearTracks,
                IsSelectable = false
            });
            y += 22;
            _playlistBox = new StbTextBox(1, 500, W - 24, true, FontStyle.None, 0x0021)
            {
                X = 12, Y = y, Width = W - 24, Height = 44, Multiline = true
            };
            Add(_playlistBox);

            y += 52;
            _filesLabel = new Label("", true, 0x0386, W - 24) { X = 12, Y = y };
            Add(_filesLabel);

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "New Zone")
            {
                ButtonParameter = (int)Buttons.New,
                IsSelectable = false
            });
            Add(new NiceButton(168, y, 130, 24, ButtonAction.Activate, "Delete")
            {
                ButtonParameter = (int)Buttons.Delete,
                IsSelectable = false
            });

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Save")
            {
                ButtonParameter = (int)Buttons.Save,
                IsSelectable = false
            });

            y += 32;
            Add(new Label("You are here:", true, 0x0386) { X = 12, Y = y });
            _hereLabel = new Label("(...)", true, 0x0021, 200) { X = 110, Y = y };
            Add(_hereLabel);

            y += 26;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Show Zone")
            {
                ButtonParameter = (int)Buttons.ShowZone,
                IsSelectable = false
            });

            y += 32;
            _statusLabel = new Label(string.Empty, true, 0x0021, W - 24) { X = 12, Y = y };
            Add(_statusLabel);
            _outline = new ZoneOutline { X = 0, Y = 0 };
            Add(_outline);

            Width = W;
            Height = H;
            RefreshFiles();
            LoadZone(0);
            SetInScreen();
        }

        /// <summary>Music and sfx are separate layers (own zones, player and manifest) sharing one engine.</summary>
        private ZoneAudio.Layer Layer => _layerSfx ? ZoneAudio.Sfx : ZoneAudio.Music;

        private List<ZoneAudio.Zone> Zones => Layer.Zones;

        private ZoneAudio.Zone Current => _zoneIdx >= 0 && _zoneIdx < Zones.Count ? Zones[_zoneIdx] : null;

        private void LoadZone(int idx)
        {
            SyncBoxToZone();
            _zoneIdx = Zones.Count == 0 ? -1 : ((idx % Zones.Count) + Zones.Count) % Zones.Count;
            ZoneAudio.Zone z = Current;
            if (z != null)
            {
                _ax = z.X0;
                _ay = z.Y0;
                _hasA = true;
                _nameBox.SetText(z.Name);
                _playlistBox.SetText(string.Join(",", z.Tracks));
            }
            else
            {
                _nameBox.SetText("");
                _playlistBox.SetText("");
            }

            UpdateLabels();
        }

        private void SyncBoxToZone()
        {
            ZoneAudio.Zone z = Current;
            if (z == null)
            {
                return;
            }

            z.Name = _nameBox.Text?.Trim() ?? "";
            z.Tracks.Clear();
            foreach (string part in (_playlistBox.Text ?? "").Split(','))
            {
                string track = part.Trim();
                if (!string.IsNullOrEmpty(track))
                {
                    z.Tracks.Add(track);
                }
            }
        }

        private void UpdateLabels()
        {
            ZoneAudio.Zone z = Current;
            _zoneLabel.Text = z == null ? "<new>" : (string.IsNullOrEmpty(z.Name) ? $"zone {_zoneIdx}" : z.Name);
            _rectLabel.Text = !_hasA ? "<no corners>"
                : (z != null ? $"{z.X0},{z.Y0} - {z.X1},{z.Y1}" : $"{_ax},{_ay} - ?");
        }

        private void RefreshFiles()
        {
            List<string> files = Layer.TrackFiles();
            _filesLabel.Text = files.Count == 0 ? "no tracks yet" : string.Join(",", files);
        }

        private void Say(string s)
        {
            _statusLabel.Text = s;
        }

        /// <summary>Swap the music/sfx layer (own zones, player, manifest).</summary>
        private void SwitchLayer(bool sfx)
        {
            if (_layerSfx == sfx)
            {
                return;
            }

            _layerSfx = sfx;
            _outline.Hide();
            LoadZone(0);
            RefreshFiles();
            Say(sfx ? "SFX layer (own zones and tracks)." : "Music layer (own zones and tracks).");
        }

        /// <summary>The zones the player stands in right now (any zone, even trackless).</summary>
        private string HereZoneName()
        {
            PlayerMobile player = World.Player;
            if (player == null)
            {
                return "(no player)";
            }

            string m = FirstInside(ZoneAudio.Music.Zones);
            string s = FirstInside(ZoneAudio.Sfx.Zones);
            if (m == null && s == null)
            {
                return "(wilderness)";
            }

            return $"music:{m ?? "-"} sfx:{s ?? "-"}";
        }

        private string FirstInside(List<ZoneAudio.Zone> zones)
        {
            PlayerMobile player = World.Player;
            foreach (ZoneAudio.Zone z in zones)
            {
                if (z.Contains(World.MapIndex, player.X, player.Y))
                {
                    return string.IsNullOrEmpty(z.Name) ? "(unnamed)" : z.Name;
                }
            }

            return null;
        }

        public override void Update()
        {
            base.Update();
            if (IsDisposed)
            {
                return;
            }

            string here = HereZoneName();
            if (_hereLabel.Text != here)
            {
                _hereLabel.Text = here;
            }

        }

        public override void OnButtonClick(int buttonID)
        {
            switch (buttonID)
            {
                case (int)Buttons.ZonePrev:
                    LoadZone(_zoneIdx - 1);
                    break;

                case (int)Buttons.ZoneNext:
                    LoadZone(_zoneIdx + 1);
                    break;

                case (int)Buttons.CornerA:
                    if (World.Player != null)
                    {
                        _ax = World.Player.X;
                        _ay = World.Player.Y;
                        _hasA = true;
                        ZoneAudio.Zone z = Current;
                        if (z != null)
                        {
                            z.X0 = _ax;
                            z.Y0 = _ay;
                        }

                        UpdateLabels();
                        Say($"Corner A: {_ax},{_ay}.");
                    }
                    break;

                case (int)Buttons.CornerB:
                    if (World.Player != null && _hasA)
                    {
                        ZoneAudio.Zone zz = Current;
                        if (zz != null)
                        {
                            zz.X1 = World.Player.X;
                            zz.Y1 = World.Player.Y;
                        }
                        else
                        {
                            _pendingB = (World.Player.X, World.Player.Y);
                        }

                        UpdateLabels();
                        Say($"Corner B: {World.Player.X},{World.Player.Y}.");
                    }
                    else
                    {
                        Say("Set corner A first.");
                    }
                    break;

                case (int)Buttons.New:
                    CreateZoneFromDraft();
                    break;

                case (int)Buttons.Delete:
                    {
                        ZoneAudio.Zone z = Current;
                        if (z == null)
                        {
                            Say("Nothing to delete.");
                        }
                        else
                        {
                            Zones.Remove(z);
                            LoadZone(0);
                            Say("Zone deleted (Save to persist).");
                        }
                    }
                    break;

                case (int)Buttons.Save:
                    // Saving without pressing New Zone still keeps the draft rect.
                    if (Current == null)
                    {
                        CreateZoneFromDraft();
                    }

                    SyncBoxToZone();
                    if (Current == null)
                    {
                        Say("Nothing to save (New Zone needs a name + corner A).");
                    }
                    else
                    {
                        Say(Layer.Save(Zones) ? "Zones saved." : "Save failed.");
                    }

                    RefreshFiles();
                    break;

                case (int)Buttons.ClearTracks:
                    _playlistBox.SetText("");
                    SyncBoxToZone();
                    Say("Playlist cleared (Save to persist).");
                    break;

                case (int)Buttons.ShowZone:
                    ToggleShowZone();
                    break;

                case (int)Buttons.Close:
                    Dispose();
                    break;
            }
        }

        private (int X, int Y)? _pendingB;

        /// <summary>
        /// Dotted outline of the current (or draft) zone in the world, 20s or
        /// until pressed again. Answers "where is this zone" in game.
        /// </summary>
        private void ToggleShowZone()
        {
            if (_outline.Showing)
            {
                _outline.Hide();
                Say("Zone hidden.");
                return;
            }

            int x0, y0, x1, y1;
            ZoneAudio.Zone z = Current;
            if (z != null)
            {
                x0 = z.X0;
                y0 = z.Y0;
                x1 = z.X1;
                y1 = z.Y1;
            }
            else if (_hasA && World.Player != null)
            {
                x0 = _ax;
                y0 = _ay;
                x1 = World.Player.X;
                y1 = World.Player.Y;
            }
            else
            {
                Say("Select or make a zone first.");
                return;
            }

            if (z != null && z.Facet >= 0 && z.Facet != World.MapIndex)
            {
                Say("That zone is on another facet.");
                return;
            }

            int pz = World.Player?.Z ?? 0;
            _outline.Show(World.MapIndex, x0, y0, x1, y1, pz);
            Say($"Showing {x0},{y0} - {x1},{y1} (20s).");
        }

        /// <summary>
        /// Builds a zone from the draft (name box + corner A + player or
        /// corner B) and selects it. False when the draft is incomplete,
        /// with the reason on the status line.
        /// </summary>
        private bool CreateZoneFromDraft()
        {
            string name = _nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Say("Name the zone first.");
                return false;
            }

            if (!_hasA)
            {
                Say("Set corner A first (stand somewhere).");
                return false;
            }

            int bx = World.Player?.X ?? _ax;
            int by = World.Player?.Y ?? _ay;
            if (_pendingB != null)
            {
                bx = _pendingB.Value.X;
                by = _pendingB.Value.Y;
                _pendingB = null;
            }

            Zones.Add(new ZoneAudio.Zone
            {
                Name = name,
                Facet = World.MapIndex,
                X0 = _ax,
                Y0 = _ay,
                X1 = bx,
                Y1 = by,
            });
            LoadZone(Zones.Count - 1);
            Say($"Zone '{name}' added (facet {World.MapIndex}).");
            return true;
        }

    }
}
