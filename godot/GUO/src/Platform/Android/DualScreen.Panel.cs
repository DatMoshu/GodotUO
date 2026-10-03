// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Game.Scenes;
using GUO.Input;
using GUO.Input.Touch;

namespace GUO.Platform.Android
{
    /// <summary>
    /// The second screen on a device with one screen: the same virtual
    /// extension to the right of the main window, shown in a rect inside it.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has no second screen. The owner's
    /// direction of 2026-09-28 (docs/ui/one_screen_panel.md). Nothing about
    /// the shelf changes: gumps sit at <c>MainWidth + x</c>, DualScreen.Draw
    /// renders them into the target, and fingers arrive at
    /// <c>mainWidth + x</c>. Only the ends differ. The target is not read
    /// back and pushed to a display; its texture is shown by a TextureRect
    /// over the main window (a "panel"). And its fingers are the main
    /// window's own, taken in front of the client when they land on that
    /// rect (<see cref="HandleMainInput"/>).
    ///
    /// Three shapes, one at a time:
    /// <list type="bullet">
    /// <item><b>Dock</b>: at the login screen, on the left, full height,
    /// holding the pre-game card, with the login gumps centred in the rest
    /// (MobileProfile.CentreLoginGump). Only when the window has room for
    /// both. Otherwise the card's modal stays the way in.</item>
    /// <item><b>Drawer</b>: in the world, a panel over one edge (the
    /// player's choice), opened and closed by a stone tab with a page arrow on
    /// that edge (a tap, or a drag), by a tap outside it, or by the pad's
    /// Back button. It holds the shelf gumps and the companion tabs, as the
    /// Thor's lower screen does.</item>
    /// <item><b>Split</b>: in the world on a near-square screen (an
    /// unfolded Fold), if the player chose it: the world on the top half,
    /// the panel on the bottom half, always shown.</item>
    /// </list>
    /// On the touch layer it is on by default. On the desktop it is opt-in
    /// (Profile.OneScreenPanel).
    /// </remarks>
    internal sealed partial class DualScreen
    {
        private enum PanelKind { None, Dock, Drawer, Split, Book }
        private int _layoutRevision = -1;

        /// <summary>The login screen's width, which every login gump is laid out for.</summary>
        private const int LoginWidth = 640;

        /// <summary>The dock's width: between these, or not at all (client px).</summary>
        private const int DockMin = 280, DockMax = 400;

        /// <summary>Client px between the dock and the login gump, and around it.</summary>
        private const int DockGap = 8;

        /// <summary>The dock on a desktop login window, which is widened for it (client px).</summary>
        private const int DesktopDock = 320;

        /// <summary>The drawer's width at most: the Thor's lower screen at 2x, which the shelf is packed for.</summary>
        private const int DrawerMax = 620;

        /// <summary>The strip of world left beside an open drawer, to tap it closed (client px).</summary>
        private const int DrawerGap = 48;

        /// <summary>A window this close to square offers the split (an unfolded Fold).</summary>
        private const float SquareRatio = 1.34f;

        /// <summary>Seconds the drawer takes to open or close.</summary>
        private const float SlideSeconds = 0.14f;

        /// <summary>The tab, in art pixels (scaled by UoTheme.PixelScale): a stone frame with a page arrow.</summary>
        private const int TabWidth = 28, TabHeight = 60;

        /// <summary>Window px a finger moves before a press on the tab is a drag.</summary>
        private const float TabSlop = 12f;

        private bool _panel;
        private PanelKind _kind;
        private Rect2I _rect;              // the panel's rect in client px, fully open
        private bool _drawerOpen;
        private float _slide;              // 0 closed .. 1 open (the dock and the split are always 1)
        private CanvasLayer _panelLayer;
        private TextureRect _panelView;
        private ColorRect _panelRule;
        private PanelContainer _tab;
        private TextureRect _chevron;
        private TextureRect _tabGlyph;
        private readonly HashSet<int> _panelFingers = new();
        private int _tabFinger = -1;
        private bool _tabMoved;
        private Vector2 _tabStart;
        private float _tabSlideAtStart;
        private int _swallowFinger = -1;

        /// <summary>The second screen is a panel inside the main window (one screen), not a display.</summary>
        public static bool IsPanel => _instance != null && _instance._panel;

        /// <summary>The drawer is open, or opening (in the world, on one screen).</summary>
        public static bool DrawerOpen => IsPanel && _instance._kind == PanelKind.Drawer && _instance._drawerOpen;

        /// <summary>For the probe: the panel's shape now ("none", "dock", "drawer", "split").</summary>
        public static string PanelShape => IsPanel && Active ? _instance._kind.ToString().ToLowerInvariant() : "none";

        /// <summary>For the probe: the panel's rect in client px, fully open.</summary>
        public static Rect2I PanelRect => IsPanel ? _instance._rect : default;

        /// <summary>For the probe: the tab's rect in window px, when it shows.</summary>
        public static Rect2? TabRect => IsPanel && _instance._tab != null && _instance._tab.Visible
            ? new Rect2(_instance._tab.Position, _instance._tab.Size * _instance._tab.Scale)
            : null;

        /// <summary>
        /// What the second screen is called in captions, capitalised: "Bottom
        /// screen" on a device with one, "Side panel" for the drawer, "Bottom
        /// panel" for the split. The first screen is <see cref="FirstName"/>.
        /// </summary>
        public static string SecondName => !IsPanel ? "Bottom screen" : _instance._kind == PanelKind.Split ? "Bottom panel" : "Side panel";

        /// <summary>The main screen in captions: "Top screen" beside a real second screen, "Main screen" beside the panel.</summary>
        public static string FirstName => IsPanel ? "Main screen" : "Top screen";

        /// <summary>Whether the panel is on screen at all now (the dock, the split, a drawer not fully closed).</summary>
        private bool PanelVisible => _panel && Active && _kind != PanelKind.None && _slide > 0f;

        /// <summary>
        /// Client px the login gumps are moved right by, for the dock: the
        /// dock's width and its gap, or 0 when there will be no dock. Worked
        /// out from the window alone, so the login gumps are placed right
        /// before the dock is up.
        /// </summary>
        public static int PregameDockReserve
        {
            get
            {
                // The desktop's login window is still the old size when the
                // gumps are placed; it will be the dock's and the login's.
                if (!TouchInput.Enabled)
                {
                    return DesktopDockWidth > 0 ? DesktopDock + DockGap : 0;
                }

                int dock = PregameDockWidth();
                return dock > 0 ? dock + DockGap : 0;
            }
        }

        /// <summary>
        /// On the desktop with the panel on, the client px the login window is
        /// widened by for the dock; 0 otherwise (LoginScene.Load).
        /// </summary>
        public static int DesktopDockWidth =>
            _instance != null && _instance._panel && !ForcedOff && !TouchInput.Enabled && DualScreenSettings.Current.PanelOn
                ? DesktopDock + 2 * DockGap
                : 0;

        /// <summary>
        /// The touch layer's screen scale, one step down when that is what
        /// makes room for the dock beside the login screen on a landscape
        /// window (the unfolded Fold at 2076 wide: 3 leaves 52 px, 2 leaves
        /// 398). Never below 2, and never when the panel is off.
        /// </summary>
        public static int ScaleForPanel(Vector2I window, int scale)
        {
            if (!ForcedOff && window.X > window.Y && scale > 2 && DualScreenSettings.Current.PanelOn
                && DockFor(window.X / scale) == 0 && DockFor(window.X / (scale - 1)) > 0)
            {
                GD.Print($"[GUO] one screen: screen scale {scale - 1}, not {scale}, so the side panel fits beside the login screen");
                return scale - 1;
            }

            return scale;
        }

        /// <summary>The dock's width for a client this wide, or 0 when the login gump and a usable dock do not both fit.</summary>
        private static int DockFor(int clientWidth)
        {
            int room = clientWidth - LoginWidth - 2 * DockGap;
            return room >= DockMin ? Math.Min(room, DockMax) : 0;
        }

        private static int PregameDockWidth()
        {
            if (_instance == null || !_instance._panel || ForcedOff || Client.Game == null || !DualScreenSettings.Current.PanelOn)
            {
                return 0;
            }

            return DockFor(Client.Game.ClientBounds.Width);
        }

        /// <summary>The shape wanted now, and its rect in client px.</summary>
        private PanelKind WantedPanel(out Rect2I rect)
        {
            rect = default;

            if (!DualScreenSettings.Current.PanelOn || Client.Game == null)
            {
                return PanelKind.None;
            }

            Rectangle bounds = Client.Game.ClientBounds;
            int w = bounds.Width, h = bounds.Height;

            if (!(Client.Game.UO?.World?.InGame ?? false))
            {
                int dock = Client.Game.Scene is LoginScene ? DockFor(w) : 0;

                if (dock == 0)
                {
                    return PanelKind.None;
                }

                rect = new Rect2I(0, 0, dock, h);
                return PanelKind.Dock;
            }

            float ratio = (float) Math.Max(w, h) / Math.Max(1, Math.Min(w, h));

            if (AdaptiveLayout.Active)
            {
                var panel = AdaptiveLayout.Layout.Companion;
                // With a physical pad, tent mode uses a full world and the existing drawer.
                if (!panel.Empty)
                {
                    rect = new Rect2I(panel.X, panel.Y, panel.Width, panel.Height);
                    return AdaptiveLayout.Layout.Posture == DevicePosture.Book ? PanelKind.Book : PanelKind.Split;
                }
            }

            if (ratio <= SquareRatio && DualScreenSettings.Current.SquareSplit)
            {
                int top = h / 2;
                rect = new Rect2I(0, top, w, h - top);
                return PanelKind.Split;
            }

            int width = Math.Max(1, Math.Min(DrawerMax, w - DrawerGap));
            rect = new Rect2I(DualScreenSettings.Current.DrawerRight ? w - width : 0, 0, width, h);
            return PanelKind.Drawer;
        }

        /// <summary>Whether the panel wants the second screen up, with the shape it would take.</summary>
        private bool PanelWanted() => WantedPanel(out _) != PanelKind.None;

        /// <summary>Before Activate: the target's size is the panel's, at the main screen's scale.</summary>
        private void SizePanel()
        {
            _kind = WantedPanel(out _rect);
            float dpi = Client.Game.DpiScale;
            _physicalWidth = Math.Max(1, (int) Math.Round(_rect.Size.X * dpi));
            _physicalHeight = Math.Max(1, (int) Math.Round(_rect.Size.Y * dpi));
            _slide = _kind == PanelKind.Drawer ? (_drawerOpen ? 1f : 0f) : 1f;
        }

        /// <summary>Per frame while active: a new shape or size reactivates; the drawer slides.</summary>
        private void UpdatePanel(double delta)
        {
            PanelKind kind = WantedPanel(out Rect2I rect);

            if (kind != _kind || rect != _rect || _layoutRevision != AdaptiveLayout.Revision)
            {
                _layoutRevision = AdaptiveLayout.Revision;
                Input.Touch.TouchInput.CancelGesture();
                if (kind != PanelKind.Drawer)
                {
                    _drawerOpen = false;
                }

                GD.Print($"[GUO] one screen: {_kind.ToString().ToLowerInvariant()} -> {kind.ToString().ToLowerInvariant()} at {rect.Position.X},{rect.Position.Y} {rect.Size.X}x{rect.Size.Y}");
                Deactivate();

                if (kind != PanelKind.None)
                {
                    Activate();
                }

                FillMainWithWorld();

                return;
            }

            if (_kind == PanelKind.Drawer && _tabFinger < 0)
            {
                float target = _drawerOpen ? 1f : 0f;
                float step = (float) delta / SlideSeconds;
                _slide = _slide < target ? Math.Min(target, _slide + step) : Math.Max(target, _slide - step);
            }

            LayOutPanel();
        }

        /// <summary>The panel's rect in client px as shown now: the drawer slid in from its edge.</summary>
        private Rect2 ShownRect()
        {
            if (_kind != PanelKind.Drawer)
            {
                return new Rect2(_rect.Position, _rect.Size);
            }

            float hidden = _rect.Size.X * (1f - _slide);
            float x = DualScreenSettings.Current.DrawerRight ? _rect.Position.X + hidden : _rect.Position.X - hidden;
            return new Rect2(x, _rect.Position.Y, _rect.Size.X, _rect.Size.Y);
        }

        private void OpenPanel()
        {
            _panelLayer = new CanvasLayer { Layer = 80, Name = "OneScreenPanel" };
            AddChild(_panelLayer);

            _panelView = new TextureRect
            {
                Texture = _target.Texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                // Rule 7: the scale up is by the client's own whole number, never filtered.
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _panelLayer.AddChild(_panelView);

            // One art pixel of the rule colour where the panel meets the
            // world (docs/ui/uo_godot_style.md, "Rule").
            _panelRule = new ColorRect { Color = new Godot.Color("5c554a"), MouseFilter = Control.MouseFilterEnum.Ignore };
            _panelLayer.AddChild(_panelRule);

            if (_kind == PanelKind.Drawer)
            {
                BuildTab();
            }

            LayOutPanel();
        }

        private void ClosePanel()
        {
            _panelLayer?.QueueFree();
            _panelLayer = null;
            _panelView = null;
            _panelRule = null;
            _tab = null;
            _chevron = null;
            _tabGlyph = null;
            _panelFingers.Clear();
            _tabFinger = -1;
            _swallowFinger = -1;
        }

        /// <summary>
        /// The tab: the card's grey stone frame at the tab's size, with the
        /// classic gumps' gold page arrow (gump 0x15E1, 16x16) pointing the
        /// way the drawer will move, flipped for the other way. Built at art
        /// pixels and scaled by the cards' whole number, as every card is.
        /// </summary>
        private void BuildTab()
        {
            _tab = new PanelContainer
            {
                Theme = UoTheme.Theme,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                CustomMinimumSize = new Vector2(TabWidth, TabHeight),
                Size = new Vector2(TabWidth, TabHeight),
            };
            _tab.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.StoneFrame, 0));

            _chevron = new TextureRect
            {
                Texture = UoTheme.GumpTexture(PageArrow) ?? Chevron(),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            _tab.AddChild(_chevron);
            _panelLayer.AddChild(_tab);

            // Under the tab while a pad is in use: the button that does what
            // a tap on it does (Back; InputGlyphs, ADR-0025).
            _tabGlyph = new TextureRect
            {
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
            };
            _panelLayer.AddChild(_tabGlyph);
        }

        /// <summary>The classic gumps' small gold page arrow, pointing right.</summary>
        private const ushort PageArrow = 0x15E1;

        private static ImageTexture _chevronTexture;

        /// <summary>
        /// Only when the client data has no page arrow: a right-pointing
        /// chevron, 6x11 art px, ink with a one-pixel cream light on its upper
        /// edge, as the client's arrows are lit.
        /// </summary>
        private static ImageTexture Chevron()
        {
            if (_chevronTexture != null)
            {
                return _chevronTexture;
            }

            string[] rows =
            {
                "c.....",
                "#c....",
                "##c...",
                ".##c..",
                "..##c.",
                "...###",
                "..###.",
                ".###..",
                "###...",
                "##....",
                "#.....",
            };

            Image image = Image.CreateEmpty(rows[0].Length, rows.Length, false, Image.Format.Rgba8);

            for (int y = 0; y < rows.Length; y++)
            {
                for (int x = 0; x < rows[y].Length; x++)
                {
                    char c = rows[y][x];
                    image.SetPixel(x, y, c == '#' ? UoTheme.Ink : c == 'c' ? UoTheme.Cream : Colors.Transparent);
                }
            }

            return _chevronTexture = ImageTexture.CreateFromImage(image);
        }

        /// <summary>Put the view, the rule and the tab where the panel is now.</summary>
        private void LayOutPanel()
        {
            if (_panelLayer == null)
            {
                return;
            }

            float dpi = Client.Game.DpiScale;
            Rect2 shown = ShownRect();
            bool right = DualScreenSettings.Current.DrawerRight;

            _panelView.Visible = _slide > 0f;
            _panelView.Position = (shown.Position * dpi).Floor();
            _panelView.Size = shown.Size * dpi;

            int art = Math.Max(1, UoTheme.PixelScale);
            _panelRule.Visible = _slide > 0f;

            switch (_kind)
            {
                case PanelKind.Split:
                    _panelRule.Position = new Vector2(0, shown.Position.Y * dpi - art);
                    _panelRule.Size = new Vector2(shown.Size.X * dpi, art);

                    break;

                case PanelKind.Drawer when right:
                    _panelRule.Position = new Vector2(shown.Position.X * dpi - art, 0);
                    _panelRule.Size = new Vector2(art, shown.Size.Y * dpi);

                    break;

                default:
                    _panelRule.Position = new Vector2(shown.End.X * dpi, 0);
                    _panelRule.Size = new Vector2(art, shown.Size.Y * dpi);

                    break;
            }

            if (_tab == null)
            {
                return;
            }

            _tab.Scale = new Vector2(art, art);
            Vector2 size = new Vector2(TabWidth, TabHeight) * art;

            // On the drawer's inner edge, which is the window's edge while it
            // is closed; a little above the middle, clear of the command bar.
            float edge = right ? shown.Position.X * dpi : shown.End.X * dpi;
            float x = right ? edge - size.X : edge;
            float y = shown.Size.Y * dpi * 0.42f - size.Y / 2;
            _tab.Position = new Vector2(x, y).Floor();

            // It points the way a tap will move the drawer.
            bool pointsRight = right ? _drawerOpen : !_drawerOpen;
            _chevron.FlipH = !pointsRight;

            Texture2D glyph = Input.Glyphs.InputGlyphs.For(PadAction.Back);
            _tabGlyph.Visible = glyph != null;

            if (glyph != null)
            {
                int g = Input.Glyphs.InputGlyphs.Size * art;
                _tabGlyph.Texture = glyph;
                _tabGlyph.Size = new Vector2(g, g);
                _tabGlyph.Position = new Vector2(x + (size.X - g) / 2, y + size.Y + 4 * art).Floor();
            }
        }

        // ==========================
        // === Opening, closing =====
        // ==========================

        /// <summary>Open or close the drawer (the pad's Back button, a tap on the tab). Nothing without one.</summary>
        public static void ToggleDrawer()
        {
            if (!IsPanel || !Active || _instance._kind != PanelKind.Drawer)
            {
                return;
            }

            SetDrawer(!_instance._drawerOpen);
        }

        public static void SetDrawer(bool open)
        {
            if (!IsPanel || !Active || _instance._kind != PanelKind.Drawer || _instance._drawerOpen == open)
            {
                return;
            }

            _instance._drawerOpen = open;
            GD.Print($"[GUO] one screen: drawer {(open ? "open" : "closed")}");
        }

        /// <summary>For the probe: the drawer settled where it is going.</summary>
        public static bool DrawerSettled => !IsPanel || _instance._kind != PanelKind.Drawer || _instance._slide == (_instance._drawerOpen ? 1f : 0f);

        // ==========================
        // === Fingers ==============
        // ==========================

        /// <summary>
        /// The main window's pointer events, from GameController before the
        /// client sees them. On the tab: the drawer opens, closes or follows
        /// the finger. On the panel: the event is moved onto the virtual
        /// extension and delivered as the second screen's are. A press
        /// outside an open drawer closes it, unless an item is held (that is
        /// a drop in the world). True when consumed.
        /// </summary>
        public static bool HandleMainInput(InputEvent e)
        {
            if (_instance == null || !_instance._panel || !Active || _instance._kind == PanelKind.None || Suspended)
            {
                return false;
            }

            return _instance.PanelInput(e);
        }

        private bool PanelInput(InputEvent e)
        {
            int index;
            Vector2 at;
            bool? pressed = null; // true a press, false a release, null a move

            switch (e)
            {
                case InputEventScreenTouch t:
                    index = t.Index;
                    at = t.Position;
                    pressed = t.Pressed;

                    break;

                case InputEventScreenDrag d:
                    index = d.Index;
                    at = d.Position;

                    break;

                case InputEventMouseButton b:
                    // On the touch layer the mouse is Godot's copy of a finger
                    // (or the desktop's stand-in for one, which Godot also
                    // sends as a touch): the touch is what counts.
                    if (TouchInput.Enabled)
                    {
                        return OnPanelOrTab(b.Position);
                    }

                    index = 0;
                    at = b.Position;

                    if (b.ButtonIndex == MouseButton.Left)
                    {
                        pressed = b.Pressed;
                    }

                    break;

                case InputEventMouseMotion m:
                    if (TouchInput.Enabled)
                    {
                        return OnPanelOrTab(m.Position);
                    }

                    index = 0;
                    at = m.Position;

                    break;

                default:
                    return false;
            }

            bool leftButton = e is InputEventScreenTouch or InputEventScreenDrag
                || e is InputEventMouseButton { ButtonIndex: MouseButton.Left }
                || (e is InputEventMouseMotion mm && (mm.ButtonMask & MouseButtonMask.Left) != 0);

            // --- the tab ---
            if (_tab != null && leftButton)
            {
                if (pressed == true && _tabFinger < 0 && TabHit(at))
                {
                    _tabFinger = index;
                    _tabStart = at;
                    _tabMoved = false;
                    _tabSlideAtStart = _slide;

                    return true;
                }

                if (index == _tabFinger)
                {
                    float travel = (at.X - _tabStart.X) * (DualScreenSettings.Current.DrawerRight ? -1f : 1f);

                    if (pressed == null)
                    {
                        _tabMoved |= Math.Abs(at.X - _tabStart.X) > TabSlop;

                        if (_tabMoved)
                        {
                            _slide = Math.Clamp(_tabSlideAtStart + travel / (_rect.Size.X * Client.Game.DpiScale), 0f, 1f);
                            LayOutPanel();
                        }

                        return true;
                    }

                    if (pressed == false)
                    {
                        _tabFinger = -1;

                        if (_tabMoved)
                        {
                            // Where it was let go of decides, a third of the way.
                            SetDrawer(_slide > (_tabSlideAtStart > 0.5f ? 0.66f : 0.33f));
                        }
                        else
                        {
                            ToggleDrawer();
                        }

                        return true;
                    }
                }
            }

            if (index == _swallowFinger && leftButton)
            {
                if (pressed == false)
                {
                    _swallowFinger = -1;
                }

                return true;
            }

            if (!PanelVisible)
            {
                return false;
            }

            bool inside = ShownRect().HasPoint(at / Client.Game.DpiScale);
            bool captured = _panelFingers.Contains(index);

            if (pressed == true && inside)
            {
                _panelFingers.Add(index);
                DeliverPanel(e, Map(at));

                return true;
            }

            if (captured)
            {
                // A finger that came down on the panel stays the panel's; once
                // it leaves the rect it is where it is on the main screen, so
                // an item carried out of the drawer is dropped under it.
                if (pressed == false)
                {
                    _panelFingers.Remove(index);
                }

                DeliverPanel(e, inside ? Map(at) : at);

                return true;
            }

            if (inside && pressed == null && !leftButton)
            {
                // The desktop mouse hovering the panel: tooltips, the highlight.
                DeliverPanel(e, Map(at));

                return true;
            }

            if (inside && pressed == null && leftButton)
            {
                // A finger that came down on the world and moved onto the
                // panel (carrying an item to the backpack in the drawer).
                DeliverPanel(e, Map(at));

                return true;
            }

            if (inside && pressed == false)
            {
                // Let go over the panel after coming from the world: the drop.
                DeliverPanel(e, Map(at));

                return true;
            }

            if (pressed == true && _kind == PanelKind.Drawer && _drawerOpen && leftButton
                && !(Client.Game.UO?.GameCursor?.ItemHold.Enabled ?? false))
            {
                SetDrawer(false);
                _swallowFinger = index;

                return true;
            }

            return false;
        }

        /// <summary>On the touch layer, a mouse event over the panel or the tab is swallowed: the touch copy acts.</summary>
        private bool OnPanelOrTab(Vector2 at) => (PanelVisible && ShownRect().HasPoint(at / Client.Game.DpiScale)) || TabHit(at);

        private bool TabHit(Vector2 at)
        {
            if (_tab == null || !_tab.Visible)
            {
                return false;
            }

            // The finger gets a margin as wide as the tab around it.
            Rect2 r = new Rect2(_tab.Position, _tab.Size * _tab.Scale);
            return r.Grow(r.Size.X * 0.5f).HasPoint(at);
        }

        /// <summary>Window px on the panel to window px on the virtual extension.</summary>
        private Vector2 Map(Vector2 at)
        {
            float dpi = Client.Game.DpiScale;
            Vector2 local = at / dpi - ShownRect().Position;

            return new Vector2((MainWidth + local.X) * dpi, local.Y * dpi);
        }

        /// <summary>The event at a new position, delivered as the second screen's are.</summary>
        private static void DeliverPanel(InputEvent e, Vector2 at)
        {
            switch (e)
            {
                case InputEventScreenTouch t:
                    Deliver(new InputEventScreenTouch { Index = t.Index, Position = at, Pressed = t.Pressed, DoubleTap = t.DoubleTap });

                    return;

                case InputEventScreenDrag d:
                    Deliver(new InputEventScreenDrag { Index = d.Index, Position = at, Relative = d.Relative, Velocity = d.Velocity });

                    return;

                case InputEventMouse m:
                {
                    var moved = (InputEventMouse) m.Duplicate();
                    moved.Position = at;
                    moved.GlobalPosition = at;

                    // The pre-game card and the companion tabs take fingers:
                    // the left button is one.
                    InputEvent finger = moved switch
                    {
                        InputEventMouseButton { ButtonIndex: MouseButton.Left } b => new InputEventScreenTouch { Position = at, Pressed = b.Pressed },
                        InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Left) != 0 => new InputEventScreenDrag { Position = at },
                        _ => null,
                    };

                    if (finger != null && (CompanionTabs.HandleInput(finger) || Input.Touch.Pregame.PregameCard.HandleInput(finger)))
                    {
                        return;
                    }

                    if (!WindowMenu.HandleInput(moved))
                    {
                        GodotInput.Handle(moved);
                    }

                    return;
                }
            }
        }
    }
}
