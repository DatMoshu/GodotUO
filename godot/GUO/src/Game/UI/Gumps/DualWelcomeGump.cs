// SPDX-License-Identifier: BSD-2-Clause

using System;
using Godot;
using Control = GUO.Game.UI.Controls.Control;
using Label = GUO.Game.UI.Controls.Label;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Platform.Android;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// What the second screen shows while the shelf is not in use: before
    /// the player is in the world, and in the world with the shelf turned
    /// off. The GUO sigil, a welcome line with the client version and the
    /// shard, and the second screen's settings, laid out for a finger.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no second screen (ADR-0009). This
    /// is a gump like any other -- UIManager lays it out, hit-tests and
    /// draws it -- placed by DualScreen at X = MainWidth, which is where the
    /// second screen begins, and sized to it. It is never saved (GumpType
    /// None), never moved, and DualScreen disposes it the moment the shelf
    /// takes over. Every change made here goes through DualScreenSettings,
    /// so it lands in the profile when there is one and is carried into
    /// the profile the player logs into when there is not yet.
    /// </remarks>
    internal sealed class DualWelcomeGump : Gump
    {
        private const byte FONT = 0xFF;
        private const ushort HUE_FONT = 0xFFFF;
        private const int Margin = 24;
        private const int SigilSize = 128;
        private const int ScaleButtonBase = 100;

        private static Texture2D _sigil;

        private readonly Checkbox _enabled, _paperdoll, _backpack, _status, _journal, _others;
        private readonly NiceButton[] _scale = new NiceButton[DualScreenSettings.MaxScale + 1];
        private bool _syncing;
        private int _sinceSync;

        public DualWelcomeGump(World world, int width, int height) : base(world, 0, 0)
        {
            CanMove = false;
            CanCloseWithRightClick = false;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;
            LayerOrder = UILayer.Under;
            Width = width;
            Height = height;

            DualScreenSettings.Values v = DualScreenSettings.Current;

            // The sigil, and the welcome beside it.
            Add(new SigilPic(Margin, Margin, SigilSize));

            int textX = Margin + SigilSize + Margin;
            int y = Margin + 8;

            Add(new Label("Welcome to GUO", true, HUE_FONT, 0, FONT, FontStyle.BlackBorder) { X = textX, Y = y });
            y += 30;
            Add(new Label($"Client {CUOEnviroment.Version}", true, HUE_FONT, 0, FONT) { X = textX, Y = y });
            y += 22;
            Add(new Label($"Shard {Settings.GlobalSettings.IP}:{Settings.GlobalSettings.Port}", true, HUE_FONT, 0, FONT) { X = textX, Y = y });
            y += 22;
            Add(new Label($"Second screen {DualScreen.SecondWidth}x{DualScreen.SecondHeight}, {width}x{height} to the client", true, HUE_FONT, width - textX - Margin, FONT) { X = textX, Y = y });

            // The settings block. Rows 30 apart: the checkbox art is 22
            // high, and a finger on a 2x panel gets 60 physical pixels.
            y = Margin + SigilSize + Margin;

            Add(new Label("Second screen", true, HUE_FONT, 0, FONT, FontStyle.BlackBorder) { X = Margin, Y = y });
            y += 28;

            Add(_enabled = Box("Use it as a shelf for gumps while in the world", Margin, y, v.Enabled));
            y += 34;

            Add(new Label("Shelve when opened:", true, HUE_FONT, 0, FONT) { X = Margin, Y = y });
            y += 24;

            int column = Math.Max(200, (width - 2 * Margin) / 2);

            Add(_paperdoll = Box("Paperdoll", Margin, y, v.Paperdoll));
            Add(_status = Box("Status bar", Margin + column, y, v.Status));
            y += 30;
            Add(_backpack = Box("Backpack", Margin, y, v.Backpack));
            Add(_journal = Box("Journal", Margin + column, y, v.Journal));
            y += 30;
            Add(_others = Box("Other gumps (skills, spellbook, containers)", Margin, y, v.Others));
            y += 38;

            Add(new Label("Shelf scale:", true, HUE_FONT, 0, FONT) { X = Margin, Y = y + 6 });

            int bx = Margin + 110;

            for (int i = 0; i <= DualScreenSettings.MaxScale; i++)
            {
                string text = i == 0 ? "As main" : $"{i}x";

                _scale[i] = new NiceButton(bx, y, 84, 30, ButtonAction.Activate, text, 1)
                {
                    ButtonParameter = ScaleButtonBase + i,
                    IsSelected = v.Scale == i,
                };

                Add(_scale[i]);
                bx += 92;
            }

            y += 44;

            Add(new Label(
                "Swap screens is not offered: the world is drawn to the main window only, and pushing it "
                + "through the second display's bitmap path would cost the frame rate ADR-0009 protects.",
                true, HUE_FONT, width - 2 * Margin, FONT) { X = Margin, Y = y });

            _enabled.ValueChanged += (_, _) => Push();
            _paperdoll.ValueChanged += (_, _) => Push();
            _backpack.ValueChanged += (_, _) => Push();
            _status.ValueChanged += (_, _) => Push();
            _journal.ValueChanged += (_, _) => Push();
            _others.ValueChanged += (_, _) => Push();
        }

        private static Checkbox Box(string text, int x, int y, bool isChecked)
        {
            return new Checkbox(0x00D2, 0x00D3, text, FONT, HUE_FONT)
            {
                X = x,
                Y = y,
                IsChecked = isChecked,
            };
        }

        public override void OnButtonClick(int buttonID)
        {
            if (buttonID >= ScaleButtonBase && buttonID <= ScaleButtonBase + DualScreenSettings.MaxScale)
            {
                DualScreenSettings.Edit(v => v.Scale = buttonID - ScaleButtonBase);

                return;
            }

            base.OnButtonClick(buttonID);
        }

        /// <summary>Every widget into the settings in force.</summary>
        private void Push()
        {
            if (_syncing)
            {
                return;
            }

            DualScreenSettings.Edit(v =>
            {
                v.Enabled = _enabled.IsChecked;
                v.Paperdoll = _paperdoll.IsChecked;
                v.Backpack = _backpack.IsChecked;
                v.Status = _status.IsChecked;
                v.Journal = _journal.IsChecked;
                v.Others = _others.IsChecked;
            });
        }

        /// <summary>
        /// Now and then, the settings in force back into the widgets, for a
        /// change made elsewhere (Options, while the panel is up in the world).
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (++_sinceSync < 30)
            {
                return;
            }

            _sinceSync = 0;
            _syncing = true;

            DualScreenSettings.Values v = DualScreenSettings.Current;

            _enabled.IsChecked = v.Enabled;
            _paperdoll.IsChecked = v.Paperdoll;
            _backpack.IsChecked = v.Backpack;
            _status.IsChecked = v.Status;
            _journal.IsChecked = v.Journal;
            _others.IsChecked = v.Others;

            for (int i = 0; i < _scale.Length; i++)
            {
                if (i == v.Scale && !_scale[i].IsSelected)
                {
                    _scale[i].IsSelected = true;
                }
            }

            _syncing = false;
        }

        /// <summary>
        /// The GUO sigil (the project icon, res://icon.png) at a fixed size,
        /// scaled once on the CPU so it is drawn 1:1 and never filtered on
        /// the way to the panel.
        /// </summary>
        private sealed class SigilPic : Control
        {
            public SigilPic(int x, int y, int size)
            {
                X = x;
                Y = y;
                Width = size;
                Height = size;
                AcceptMouseInput = false;

                _sigil ??= LoadSigil(size);
            }

            private static Texture2D LoadSigil(int size)
            {
                try
                {
                    // An exported build carries the imported texture; a run
                    // from source before the editor has imported anything
                    // has only the PNG. Both are the same sigil.
                    Image image = null;

                    if (ResourceLoader.Exists("res://icon.png"))
                    {
                        image = GD.Load<Texture2D>("res://icon.png")?.GetImage();
                    }

                    if (image == null && Godot.FileAccess.FileExists("res://icon.png"))
                    {
                        image = new Image();

                        if (image.LoadPngFromBuffer(Godot.FileAccess.GetFileAsBytes("res://icon.png")) != Error.Ok)
                        {
                            image = null;
                        }
                    }

                    if (image == null)
                    {
                        GD.PrintErr("[GUO] dual screen: no sigil for the welcome panel: res://icon.png not loadable");

                        return null;
                    }

                    if (image.IsCompressed())
                    {
                        image.Decompress();
                    }

                    if (image.GetWidth() != size || image.GetHeight() != size)
                    {
                        image.Resize(size, size, Image.Interpolation.Lanczos);
                    }

                    if (image.GetFormat() != Image.Format.Rgba8)
                    {
                        image.Convert(Image.Format.Rgba8);
                    }

                    return ImageTexture.CreateFromImage(image);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[GUO] dual screen: no sigil for the welcome panel: {ex.Message}");

                    return null;
                }
            }

            public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
            {
                float layerDepth = layerDepthRef;

                if (IsDisposed)
                {
                    return false;
                }

                Texture2D texture = _sigil;

                if (texture != null)
                {
                    Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);
                    var source = new Rectangle(0, 0, (int) texture.GetWidth(), (int) texture.GetHeight());

                    renderLists.AddGumpNoAtlas(batcher =>
                    {
                        batcher.Draw(texture, new Rectangle(x, y, Width, Height), source, hueVector, layerDepth);

                        return true;
                    });
                }

                return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            }
        }
    }
}
