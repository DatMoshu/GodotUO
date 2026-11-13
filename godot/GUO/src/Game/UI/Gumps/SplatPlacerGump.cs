// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using GUO.Assets;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Renderer;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// In-game splat placer: pick a staged splat, target a world tile, tune
    /// scale and yaw live, then Place it and Save the manifest. Opened with
    /// the "-splat" client command. Testing tool, not shipped UI: placements
    /// are client-side only (GUO_SPLAT_STAGE/splats.json) and never touch the
    /// shard. Ground clicks do not answer targeting, so target a nearby
    /// object and nudge X/Y/Z to the exact tile.
    /// </summary>
    internal sealed class SplatPlacerGump : Gump
    {
        private const int W = 300;
        private const int H = 600;
        private const string PreviewKey = "gump:preview";

        private readonly Label _modelLabel;
        private readonly Label _tileLabel;
        private readonly Label _xLabel;
        private readonly Label _yLabel;
        private readonly Label _zLabel;
        private readonly Label _scaleLabel;
        private readonly Label _yawLabel;
        private readonly Label _graphicLabel;
        private readonly PreviewPic _beforePic;
        private readonly Label _statusLabel;

        private List<string> _models = new List<string>();
        private int _modelIdx = -1;
        private bool _hasTile;
        private int _tx, _ty, _tz;
        private float _scale = 44f;
        private float _yaw = 180f;
        private ushort _targetGraphic;
        private GameObject _targetObj;
        private readonly List<PlacedEntry> _placed = new List<PlacedEntry>();
        private sealed class PlacedEntry
        {
            public string Name;
            public SplatPlacement At;
        }

        private enum Buttons
        {
            Target,
            Select,
            ModelPrev,
            ModelNext,
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
            Place,
            Save,
            Rescan,
            ReplaceAll,
            Restore,
            Close
        }

        public SplatPlacerGump(World world)
            : base(world, 120, 120)
        {
            CanMove = true;
            CanCloseWithRightClick = true;

            Add(new AlphaBlendControl(0.85f)
            {
                Width = W, Height = H, AcceptMouseInput = true, CanMove = true
            });

            Add(new Label("Splat Placer", true, 0x0481) { X = 12, Y = 8 });

            var close = new NiceButton(W - 30, 6, 22, 20, ButtonAction.Activate, "X")
            {
                ButtonParameter = (int)Buttons.Close,
                IsSelectable = false
            };
            Add(close);

            int y = 34;
            Add(new Label("Model:", true, 0x0386) { X = 12, Y = y });
            Add(new NiceButton(70, y - 2, 26, 22, ButtonAction.Activate, "<")
            {
                ButtonParameter = (int)Buttons.ModelPrev,
                IsSelectable = false
            });
            _modelLabel = new Label("<none>", true, 0x0021, 150) { X = 100, Y = y };
            Add(_modelLabel);
            Add(new NiceButton(252, y - 2, 26, 22, ButtonAction.Activate, ">")
            {
                ButtonParameter = (int)Buttons.ModelNext,
                IsSelectable = false
            });

            y += 30;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Target")
            {
                ButtonParameter = (int)Buttons.Target,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Select")
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
            Add(new Label("Native art:", true, 0x0386) { X = 12, Y = y });
            y += 18;
            _beforePic = new PreviewPic { X = 12, Y = y, Width = 132, Height = 132 };
            Add(_beforePic);
            y += 140;

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

            y += 34;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Place")
            {
                ButtonParameter = (int)Buttons.Place,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Save")
            {
                ButtonParameter = (int)Buttons.Save,
                IsSelectable = false
            });

            y += 32;
            Add(new NiceButton(12, y, 130, 24, ButtonAction.Activate, "Rescan")
            {
                ButtonParameter = (int)Buttons.Rescan,
                IsSelectable = false
            });
            Add(new NiceButton(158, y, 130, 24, ButtonAction.Activate, "Replace All")
            {
                ButtonParameter = (int)Buttons.ReplaceAll,
                IsSelectable = false
            });

            y += 32;
            _statusLabel = new Label(string.Empty, true, 0x0021, W - 24) { X = 12, Y = y };
            Add(_statusLabel);

            Width = W;
            Height = H;
            RefreshModels();
            UpdateLabels();
            SetInScreen();
            if (_models.Count == 0)
            {
                Say("No staged splats (GUO_SPLAT_STAGE?).");
            }
        }

        public override GumpType GumpType => GumpType.SplatPlacer;

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

        private void RefreshModels()
        {
            _models = Renderer.SplatStage.StagedNames();
            if (_models.Count == 0)
            {
                _modelIdx = -1;
            }
            else if (_modelIdx < 0 || _modelIdx >= _models.Count)
            {
                _modelIdx = 0;
            }
        }

        private string CurrentModel => _modelIdx >= 0 && _modelIdx < _models.Count ? _models[_modelIdx] : null;

        private GameScene Scene => Client.Game.GetScene<GameScene>();

        private void UpdateLabels()
        {
            _modelLabel.Text = CurrentModel ?? "<none>";
            _tileLabel.Text = _hasTile ? $"{_tx}, {_ty}, {_tz}" : "<none> (Target first)";
            _xLabel.Text = _hasTile ? _tx.ToString() : "–";
            _yLabel.Text = _hasTile ? _ty.ToString() : "–";
            _zLabel.Text = _hasTile ? _tz.ToString() : "–";
            _scaleLabel.Text = _scale.ToString("0.#");
            _yawLabel.Text = _yaw.ToString("0.#");
            _graphicLabel.Text = _targetGraphic == 0
                ? "<none>"
                : $"0x{_targetGraphic:X4} ({_targetGraphic})";
        }

        private void Say(string s)
        {
            _statusLabel.Text = s;
        }

        /// <summary>
        /// The Before cell follows the selection: the victim's own artwork,
        /// read locally without a generation service.
        /// </summary>
        private void RefreshBeforePic()
        {
            byte[] png = null;
            if (_targetGraphic != 0 && _targetObj != null && !_targetObj.IsDestroyed)
            {
                try
                {
                    png = ArtPreview.ExtractPng(_targetGraphic, _targetObj is Land);
                }
                catch
                {
                }
            }

            _beforePic?.SetPng(png);
        }

        private void RefreshPreview()
        {
            if (IsDisposed || !_hasTile || CurrentModel == null)
            {
                return;
            }

            GameScene scene = Scene;
            if (scene == null || !scene.PlaceSplat(PreviewKey, CurrentModel, _tx, _ty, _tz, _scale, _yaw))
            {
                Say("Preview failed.");
            }
        }

        public override void OnButtonClick(int buttonID)
        {
            bool preview = true;
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
                            if (obj != null)
                            {
                                _tx = obj.X;
                                _ty = obj.Y;
                                _tz = obj.Z;
                                _hasTile = true;
                                _targetGraphic = obj.Graphic;
                                _targetObj = obj;
                                GameActions.Print(World,
                                    $"SplatPlacer tile: {_tx}, {_ty}, {_tz} graphic 0x{_targetGraphic:X4}");
                                RefreshBeforePic();
                                RefreshPreview();
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click a tile to place the staged model...");
                    preview = false;
                    break;

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
                                // Select picks the replace victim: no staged
                                // preview moves, only the graphic readout.
                                _tx = obj.X;
                                _ty = obj.Y;
                                _tz = obj.Z;
                                _hasTile = true;
                                _targetGraphic = obj.Graphic;
                                _targetObj = obj;
                                GameActions.Print(World,
                                    $"SplatPlacer selected: 0x{_targetGraphic:X4} at {_tx}, {_ty}, {_tz}");
                                RefreshBeforePic();
                            }
                            UpdateLabels();
                        },
                        (uint)CursorTarget.CallbackTarget,
                        TargetType.Neutral);
                    Say("Click the object to replace...");
                    preview = false;
                    break;

                case (int)Buttons.ModelPrev:
                    if (_models.Count > 0)
                    {
                        _modelIdx = (_modelIdx - 1 + _models.Count) % _models.Count;
                        UpdateLabels();
                    }
                    break;

                case (int)Buttons.ModelNext:
                    if (_models.Count > 0)
                    {
                        _modelIdx = (_modelIdx + 1) % _models.Count;
                        UpdateLabels();
                    }
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

                case (int)Buttons.Place:
                    if (!_hasTile || CurrentModel == null)
                    {
                        Say("Target a tile and pick a model first.");
                    }
                    else
                    {
                        GameScene scene = Scene;
                        string key = $"placed:{CurrentModel}:{_tx},{_ty}";
                        if (scene != null && scene.PlaceSplat(key, CurrentModel, _tx, _ty, _tz, _scale, _yaw))
                        {
                            _placed.Add(new PlacedEntry
                            {
                                Name = CurrentModel,
                                At = new SplatPlacement
                                {
                                    Name = CurrentModel,
                                    Facet = World.MapIndex,
                                    X = _tx,
                                    Y = _ty,
                                    Z = _tz,
                                    Scale = _scale,
                                    Yaw = _yaw,
                                    LockZ = true,
                                },
                            });
                            string msg = $"{CurrentModel} placed at {_tx}, {_ty} (live)";
                            Say(msg);
                            GameActions.Print(World, "SplatPlacer: " + msg);
                        }
                        else
                        {
                            Say("Place failed.");
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
                            foreach (PlacedEntry e in _placed)
                            {
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

                case (int)Buttons.Restore:
                    if (_targetGraphic != 0)
                    {
                        ThemeManager.ClearSplatSkin(_targetGraphic);
                        string dir = SplatStage.ResolveDir();
                        if (!string.IsNullOrEmpty(dir))
                        {
                            ThemeManager.SaveSkinTheme(dir);
                        }
                    }

                    if (_targetObj != null && !_targetObj.IsDestroyed)
                    {
                        _targetObj.AllowedToDraw = true;
                        Say($"Restored 0x{_targetGraphic:X4}.");
                    }
                    else
                    {
                        Say("Nothing to restore.");
                    }
                    preview = false;
                    break;

                case (int)Buttons.ReplaceAll:
                    {
                        string splat = CurrentModel;
                        if (_targetGraphic == 0)
                        {
                            Say("Select an object first.");
                        }
                        else if (string.IsNullOrEmpty(splat))
                        {
                            Say("Pick a staged model first.");
                        }
                        else
                        {
                            ThemeManager.SetSplatSkin(_targetGraphic, splat);
                            string dir = SplatStage.ResolveDir();
                            bool saved = !string.IsNullOrEmpty(dir) && ThemeManager.SaveSkinTheme(dir);
                            string msg = $"All 0x{_targetGraphic:X4} -> {splat}" + (saved ? " (rule saved)" : "");
                            Say(msg + ".");
                            GameActions.Print(World, "SplatPlacer: " + msg);
                        }
                    }
                    preview = false;
                    break;

                case (int)Buttons.Rescan:
                    RefreshModels();
                    UpdateLabels();
                    Say($"{_models.Count} staged splat(s).");
                    preview = false;
                    break;

                case (int)Buttons.Close:
                    preview = false;
                    Dispose();
                    break;
            }
            if (preview)
            {
                RefreshPreview();
            }
        }

    }
}
