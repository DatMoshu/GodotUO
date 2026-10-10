// SPDX-License-Identifier: BSD-2-Clause

using System;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Network;
using GUO.Renderer;
using GUO.Resources;
using GUO.Utility;
using GUO.Compat;
namespace GUO.Game.UI.Gumps
{
    internal class WorldViewportGump : Gump
    {
        public const int BORDER_WIDTH = 5;
        private readonly BorderControl _borderControl;
        private readonly Button _button;
        private bool _clicked;
        private Point _lastSize,
            _savedSize;
        private readonly GameScene _scene;
        private readonly SystemChatControl _systemChatControl;

        public WorldViewportGump(World world, GameScene scene) : base(world, 0, 0)
        {
            _scene = scene;
            AcceptMouseInput = false;
            CanMove = !ProfileManager.CurrentProfile.GameWindowLock;
            CanCloseWithEsc = false;
            CanCloseWithRightClick = false;
            LayerOrder = UILayer.Under;
            X = scene.Camera.Bounds.X - BORDER_WIDTH;
            Y = scene.Camera.Bounds.Y - BORDER_WIDTH;
            _savedSize = _lastSize = new Point(
                scene.Camera.Bounds.Width,
                scene.Camera.Bounds.Height
            );

            _button = new Button(0, 0x837, 0x838, 0x838);

            _button.MouseDown += (sender, e) =>
            {
                if (!ProfileManager.CurrentProfile.GameWindowLock)
                {
                    _clicked = true;
                }
            };

            _button.MouseUp += (sender, e) =>
            {
                if (!ProfileManager.CurrentProfile.GameWindowLock)
                {
                    var s = new Point(Client.Game.ScaleWithDpi(_lastSize.X), Client.Game.ScaleWithDpi(_lastSize.Y));
                    Point n = ResizeGameWindow(s);

                    UIManager.GetGump<OptionsGump>()?.UpdateVideo();

                    if (Client.Game.UO.Version >= ClientVersion.CV_200)
                    {
                        NetClient.Socket.Send_GameWindowSize((uint)n.X, (uint)n.Y);
                    }

                    _clicked = false;
                }
            };

            _button.SetTooltip(ResGumps.ResizeGameWindow);
            Width = scene.Camera.Bounds.Width + BORDER_WIDTH * 2;
            Height = scene.Camera.Bounds.Height + BORDER_WIDTH * 2;

            _borderControl = new BorderControl(0, 0, Width, Height, 4);

            _borderControl.DragEnd += (sender, e) =>
            {
                UIManager.GetGump<OptionsGump>()?.UpdateVideo();
            };

            UIManager.SystemChat = _systemChatControl = new SystemChatControl(
                this,
                BORDER_WIDTH,
                BORDER_WIDTH,
                scene.Camera.Bounds.Width,
                scene.Camera.Bounds.Height
            );

            Add(_borderControl);
            Add(_button);
            Add(_systemChatControl);
            Resize();
        }

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            if (Mouse.IsDragging)
            {
                Point offset = Mouse.LDragOffset;

                _lastSize = _savedSize;

                if (_clicked && offset != Point.Zero)
                {
                    int w = _lastSize.X + offset.X;
                    int h = _lastSize.Y + offset.Y;

                    int targetWidth = 640;
                    int targetHeight = 480;

                    if (w < targetWidth)
                    {
                        w = targetWidth;
                    }

                    if (h < targetHeight)
                    {
                        h = targetHeight;
                    }

                    if (w > Client.Game.ClientBounds.Width - BORDER_WIDTH)
                    {
                        w = Client.Game.ClientBounds.Width - BORDER_WIDTH;
                    }

                    if (h > Client.Game.ClientBounds.Height - BORDER_WIDTH)
                    {
                        h = Client.Game.ClientBounds.Height - BORDER_WIDTH;
                    }

                    _lastSize.X = w;
                    _lastSize.Y = h;
                }

                if (
                    _scene.Camera.Bounds.Width != _lastSize.X
                    || _scene.Camera.Bounds.Height != _lastSize.Y
                )
                {
                    Width = _lastSize.X + BORDER_WIDTH * 2;
                    Height = _lastSize.Y + BORDER_WIDTH * 2;
                    _scene.Camera.Bounds.Width = _lastSize.X;
                    _scene.Camera.Bounds.Height = _lastSize.Y;

                    Resize();
                }
            }
        }

        protected override void OnDragEnd(int x, int y)
        {
            base.OnDragEnd(x, y);

            Point position = Location;

            if (position.X + Width - BORDER_WIDTH > Client.Game.ClientBounds.Width)
            {
                position.X = Client.Game.ClientBounds.Width - (Width - BORDER_WIDTH);
            }

            if (position.X < -BORDER_WIDTH)
            {
                position.X = -BORDER_WIDTH;
            }

            if (position.Y + Height - BORDER_WIDTH > Client.Game.ClientBounds.Height)
            {
                position.Y = Client.Game.ClientBounds.Height - (Height - BORDER_WIDTH);
            }

            if (position.Y < -BORDER_WIDTH)
            {
                position.Y = -BORDER_WIDTH;
            }

            Location = position;
            _scene.Camera.Bounds.X = position.X + BORDER_WIDTH;
            _scene.Camera.Bounds.Y = position.Y + BORDER_WIDTH;

            UIManager.GetGump<OptionsGump>()?.UpdateVideo();
            UpdateGameWindowPos();
        }

        protected override void OnMove(int x, int y)
        {
            base.OnMove(x, y);

            _scene.Camera.Bounds.X = ScreenCoordinateX + BORDER_WIDTH;
            _scene.Camera.Bounds.Y = ScreenCoordinateY + BORDER_WIDTH;

            UpdateGameWindowPos();
        }

        private void UpdateGameWindowPos()
        {
            if (_scene != null)
            {
                _scene.UpdateDrawPosition = true;
            }
        }

        private void Resize()
        {
            _borderControl.Width = Width;
            _borderControl.Height = Height;
            _button.X = Width - (_button.Width >> 1);
            _button.Y = Height - (_button.Height >> 1);
            _scene.Camera.Bounds.Width = Width - BORDER_WIDTH * 2;
            _scene.Camera.Bounds.Height = _systemChatControl.Height = Height - BORDER_WIDTH * 2;
            // PORT DEVIATION (GUO): a pad screen widens the world past the
            // window's left edge; the chat keeps to the part that is seen.
            int chatLeft = Math.Clamp(_chatLeft, 0, Math.Max(0, _scene.Camera.Bounds.Width - 1));
            _systemChatControl.X = BORDER_WIDTH + chatLeft;
            _systemChatControl.Width = _chatWidth > 0
                ? Math.Min(_chatWidth, _scene.Camera.Bounds.Width - chatLeft)
                : _scene.Camera.Bounds.Width - chatLeft;
            _systemChatControl.Resize();
            WantUpdateSize = true;

            UpdateGameWindowPos();
        }

        private int _chatLeft, _chatWidth;

        /// <summary>
        /// PORT DEVIATION (GUO): where the system chat sits inside the world
        /// view, in its pixels. A pad screen draws the world wider than the
        /// window and starting off its left edge; this keeps the chat on the
        /// visible part. Zero width is the whole view, as upstream.
        /// </summary>
        public void SetChatArea(int left, int width)
        {
            if (_chatLeft == left && _chatWidth == width)
            {
                return;
            }

            _chatLeft = left;
            _chatWidth = width;
            Resize();
        }

        public void SetGameWindowPosition(Point pos)
        {
            Location = pos;

            _scene.Camera.Bounds.X = ScreenCoordinateX + BORDER_WIDTH;
            _scene.Camera.Bounds.Y = ScreenCoordinateY + BORDER_WIDTH;

            UpdateGameWindowPos();
        }

        public Point ResizeGameWindow(Point newSize)
        {
            newSize.X = (int)(newSize.X / Client.Game.DpiScale);
            newSize.Y = (int)(newSize.Y / Client.Game.DpiScale);
            // PORT DEVIATION (GUO): the adaptive world must fit its actual pane;
            // the desktop minimum otherwise hides the player beneath the companion panel.
            int targetWidth = GUO.Platform.Android.AdaptiveLayout.Active ? 1 : 640;
            int targetHeight = GUO.Platform.Android.AdaptiveLayout.Active ? 1 : 480;
            if (newSize.X < targetWidth)
            {
                newSize.X = targetWidth;
            }

            if (newSize.Y < targetHeight)
            {
                newSize.Y = targetHeight;
            }

            //Resize();
            _lastSize = _savedSize = newSize;

            if (
                _scene.Camera.Bounds.Width != _lastSize.X
                || _scene.Camera.Bounds.Height != _lastSize.Y
            )
            {
                _scene.Camera.Bounds.Width = _lastSize.X;
                _scene.Camera.Bounds.Height = _lastSize.Y;
                Width = _scene.Camera.Bounds.Width + BORDER_WIDTH * 2;
                Height = _scene.Camera.Bounds.Height + BORDER_WIDTH * 2;

                Resize();
            }

            return newSize;
        }

        /// <summary>
        /// The tiled border and the resize grip. Hidden while a pad screen
        /// cuts the world to the left half, so that chrome cannot flash there.
        /// </summary>
        public void SetFrameVisible(bool visible)
        {
            _borderControl.IsVisible = visible;
            _button.IsVisible = visible;
        }

        public override bool Contains(int x, int y)
        {
            if (
                x >= BORDER_WIDTH
                && x < Width - BORDER_WIDTH * 2
                && y >= BORDER_WIDTH
                && y
                    < Height
                        - BORDER_WIDTH * 2
                        - (
                            _systemChatControl?.TextBoxControl != null
                            && _systemChatControl.IsActive
                                ? _systemChatControl.TextBoxControl.Height
                                : 0
                        )
            )
            {
                return false;
            }

            return base.Contains(x, y);
        }
    }

    internal class BorderControl : Control
    {
        public int BorderSize { get; internal set; }

        const ushort H_BORDER = 0x0A8C;
        const ushort V_BORDER = 0x0A8D;

        public BorderControl(int x, int y, int w, int h, int borderSize)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
            BorderSize = borderSize;
            CanMove = true;
            AcceptMouseInput = true;
        }

        public ushort Hue { get; set; }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            if (Hue != 0)
            {
                hueVector.X = Hue;
                hueVector.Y = 1;
            }
            float layerDepth = layerDepthRef;

            renderLists.AddGumpWithAtlas(
                (batcher) =>
                {
                    ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(H_BORDER);

                    // sopra
                    batcher.DrawTiled(
                        gumpInfo.Texture,
                        new Rectangle(x, y, Width, BorderSize),
                        gumpInfo.UV,
                        hueVector,
                        layerDepth
                    );

                    // sotto
                    batcher.DrawTiled(
                        gumpInfo.Texture,
                        new Rectangle(x, y + Height - BorderSize, Width, BorderSize),
                        gumpInfo.UV,
                        hueVector,
                        layerDepth
                    );

                    gumpInfo = ref Client.Game.UO.Gumps.GetGump(V_BORDER);
                    //sx
                    batcher.DrawTiled(
                        gumpInfo.Texture,
                        new Rectangle(x, y, BorderSize, Height),
                        gumpInfo.UV,
                        hueVector,
                        layerDepth
                    );

                    //dx
                    batcher.DrawTiled(
                        gumpInfo.Texture,
                        new Rectangle(
                            x + Width - BorderSize,
                            y + (gumpInfo.UV.Width >> 1),
                            BorderSize,
                            Height - BorderSize
                        ),
                        gumpInfo.UV,
                        hueVector,
                        layerDepth
                    );
                    return true;
                }
            );

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }
    }
}
