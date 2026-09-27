// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Xml;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.UI.Gumps
{
    /// <summary>
    /// A container as a grid of square slots, one item per slot, sized for a
    /// finger: the mobile default in place of the classic container art.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): not in ClassicUO; docs/ui/grid_container.md is
    /// the design. PacketHandlers.OpenContainer adds the classic ContainerGump
    /// as upstream does and then calls <see cref="ReplaceClassic"/>, which
    /// swaps it for this gump when the profile's GridContainers is on. That
    /// one line is the only upstream hunk: this gump watches its container's
    /// contents itself (a signature compared every frame) rather than being
    /// told by the five places upstream calls
    /// GetGump&lt;ContainerGump&gt;(...).RequestUpdateContents(), and it
    /// reports GumpType.Container so gumps.xml saves it and restores it
    /// through the classic path, which reopens the container and lands here.
    ///
    /// Order is the client's alone; the server's x,y are never written back.
    /// Every action goes through the GameActions calls ContainerGump and
    /// ItemGump use: tap for the name, double-tap to use, drag to pick up,
    /// drop on the grid to put in, drop on a bag's slot to put in the bag,
    /// long-press (the touch layer's right-click) for the context menu.
    /// </remarks>
    internal class GridContainerGump : Gump
    {
        private const int COLUMNS = 6;
        private const int PAD = 4;
        private const int GAP = 2;
        private const int ROW_HEIGHT = 20;
        private const int MIN_SLOT = 32;
        private const int MAX_SLOT = 80;

        /// <summary>Containers switched to the classic view this session.</summary>
        private static readonly HashSet<uint> _classic = new HashSet<uint>();

        private readonly Label _title;
        private readonly StbTextBox _filter;
        private readonly NiceButton _sortButton, _classicButton, _prev, _next;
        private readonly Label _pageLabel;
        private readonly AlphaBlendControl _background;
        private readonly List<GridSlot> _slots = new List<GridSlot>();

        private int _sort;
        private int _page;
        private int _pageCount = 1;
        private long _signature = -1;
        private string _lastFilter = string.Empty;

        public GridContainerGump(World world) : base(world, 0, 0) { }

        public GridContainerGump(World world, uint serial, ushort graphic) : base(world, serial, 0)
        {
            Graphic = graphic;
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;

            Add(_background = new AlphaBlendControl(0.92f));

            Add(_title = new Label(string.Empty, true, 0x0481, 0, 1, FontStyle.BlackBorder) { X = PAD, Y = PAD + 2 });

            Add(
                _classicButton = new NiceButton(0, PAD, 56, ROW_HEIGHT, ButtonAction.Activate, "Classic")
                {
                    ButtonParameter = 0,
                    IsSelectable = false
                }
            );

            Add(
                _sortButton = new NiceButton(0, PAD + ROW_HEIGHT + GAP, 64, ROW_HEIGHT, ButtonAction.Activate, SortName(0))
                {
                    ButtonParameter = 1,
                    IsSelectable = false
                }
            );

            Add(
                new ResizePic(0x0BB8)
                {
                    X = PAD,
                    Y = PAD + ROW_HEIGHT + GAP,
                    Width = 120,
                    Height = ROW_HEIGHT,
                    AcceptMouseInput = false
                }
            );

            Add(
                _filter = new StbTextBox(1, 24, 112, true, FontStyle.None, 0x0386)
                {
                    X = PAD + 4,
                    Y = PAD + ROW_HEIGHT + GAP + 2,
                    Width = 112,
                    Height = ROW_HEIGHT - 2
                }
            );

            Add(_prev = new NiceButton(PAD, 0, 40, ROW_HEIGHT, ButtonAction.Activate, "<") { ButtonParameter = 2, IsSelectable = false });
            Add(_next = new NiceButton(0, 0, 40, ROW_HEIGHT, ButtonAction.Activate, ">") { ButtonParameter = 3, IsSelectable = false });
            Add(_pageLabel = new Label(string.Empty, true, 0x0481, 0, 1, FontStyle.BlackBorder));

            Layout(0);
        }

        /// <summary>The classic gump graphic this container opened with; kept for gumps.xml.</summary>
        public ushort Graphic { get; }

        public override GumpType GumpType => GumpType.Container;

        private static int SlotSize =>
            Math.Clamp(ProfileManager.CurrentProfile?.GridContainerSlotSize ?? 44, MIN_SLOT, MAX_SLOT);

        /// <summary>Whether this container opens as a grid.</summary>
        public static bool Wants(Item item, ushort graphic)
        {
            return item != null
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.GridContainers
                && !item.IsCorpse
                && graphic != ContainerGump.CORPSES_GUMP
                && graphic != 0x091A // chessboard
                && graphic != 0x092E // backgammon
                && !_classic.Contains(item.Serial);
        }

        /// <summary>
        /// Called by PacketHandlers.OpenContainer right after it adds the
        /// classic gump: when the container should be a grid, the classic
        /// gump goes and a grid takes its place.
        /// </summary>
        public static void ReplaceClassic(World world, uint serial)
        {
            ContainerGump classic = UIManager.GetGump<ContainerGump>(serial);
            Item item = world.Items.Get(serial);

            if (classic == null || !Wants(item, classic.Graphic))
            {
                return;
            }

            GridContainerGump old = UIManager.GetGump<GridContainerGump>(serial);
            GridContainerGump grid = new GridContainerGump(world, serial, classic.Graphic);
            Point at;

            if (old != null)
            {
                at = old.Location;
                grid._sort = old._sort;
                old.Dispose();
            }
            else if (ContainerPlacement.Active(ProfileManager.CurrentProfile))
            {
                // The classic gump was placed for its own art; place again
                // for the grid's size, ignoring the classic gump it replaces.
                classic.IsVisible = false;
                at = ContainerPlacement.Place(serial, new Point(grid.Width, grid.Height), classic.Location, UIManager.GetGumpCachePosition(serial, out _));
            }
            else
            {
                at = classic.Location;
            }

            classic.Dispose();

            grid.X = at.X;
            grid.Y = at.Y;
            UIManager.Add(grid);
            grid.SetInScreen();
        }

        public override void OnButtonClick(int buttonID)
        {
            switch (buttonID)
            {
                case 0:
                    // Back to the art, for this container, for this session:
                    // reopening it is the server's to do, as upstream would.
                    _classic.Add(LocalSerial);
                    Dispose();
                    GameActions.DoubleClick(World, LocalSerial);

                    break;

                case 1:
                    _sort = (_sort + 1) % 3;
                    _sortButton.TextLabel.Text = SortName(_sort);
                    _signature = -1;

                    break;

                case 2:
                    _page = Math.Max(0, _page - 1);
                    _signature = -1;

                    break;

                case 3:
                    _page = Math.Min(_pageCount - 1, _page + 1);
                    _signature = -1;

                    break;

                default:
                    base.OnButtonClick(buttonID);

                    break;
            }
        }

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            Item container = World.Items.Get(LocalSerial);

            if (container == null || container.IsDestroyed)
            {
                Dispose();

                return;
            }

            string filter = _filter.Text ?? string.Empty;

            if (filter != _lastFilter)
            {
                _lastFilter = filter;
                _page = 0;
                _signature = -1;
            }

            long signature = Signature(container);

            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(container);
            }

            if (
                UIManager.MouseOverControl != null
                && UIManager.MouseOverControl.RootParent == this
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.HighlightContainerWhenSelected
            )
            {
                SelectedObject.SelectedContainer = container;
            }
        }

        /// <summary>
        /// Changes whenever an item comes, goes, or changes what the grid
        /// shows of it; cheap enough to take every frame.
        /// </summary>
        private static long Signature(Item container)
        {
            long h = 17;

            for (LinkedObject i = container.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                h = h * 31 + it.Serial;
                h = h * 31 + it.DisplayedGraphic;
                h = h * 31 + it.Hue;
                h = h * 31 + it.Amount;
            }

            return h;
        }

        private void Rebuild(Item container)
        {
            string title = string.IsNullOrEmpty(container.Name) ? container.ItemData.Name : container.Name;
            _title.Text = string.IsNullOrEmpty(title) ? "Container" : title;

            List<Item> items = new List<Item>();

            for (LinkedObject i = container.Items; i != null; i = i.Next)
            {
                Item it = (Item)i;

                if (Shows(container, it) && Matches(it, _lastFilter))
                {
                    items.Add(it);
                }
            }

            if (_sort == 1)
            {
                items.Sort((a, b) => string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase));
            }
            else if (_sort == 2)
            {
                items.Sort((a, b) => a.DisplayedGraphic != b.DisplayedGraphic ? a.DisplayedGraphic.CompareTo(b.DisplayedGraphic) : a.Hue.CompareTo(b.Hue));
            }

            int rows = Math.Max(2, (items.Count + COLUMNS - 1) / COLUMNS);
            int maxRows = MaxRows();
            int perPage = maxRows * COLUMNS;

            _pageCount = Math.Max(1, (items.Count + perPage - 1) / perPage);
            _page = Math.Clamp(_page, 0, _pageCount - 1);
            rows = Math.Min(rows, maxRows);

            foreach (GridSlot s in _slots)
            {
                s.Dispose();
            }

            _slots.Clear();

            int size = SlotSize;
            int top = PAD + 2 * (ROW_HEIGHT + GAP);

            for (int n = 0; n < rows * COLUMNS; n++)
            {
                int index = _page * perPage + n;
                Item it = index < items.Count ? items[index] : null;

                GridSlot slot = new GridSlot(this, it, size)
                {
                    X = PAD + n % COLUMNS * (size + GAP),
                    Y = top + n / COLUMNS * (size + GAP)
                };

                _slots.Add(slot);
                Add(slot);
            }

            Layout(rows);
        }

        /// <summary>The same items ContainerGump.ItemsOnAdded draws.</summary>
        private static bool Shows(Item container, Item item)
        {
            if (item.Amount <= 0)
            {
                return false;
            }

            Layer layer = (Layer)item.ItemData.Layer;

            if (container.IsCorpse && item.Layer > 0 && !Constants.BAD_CONTAINER_LAYERS[(int)layer])
            {
                return false;
            }

            return !(item.ItemData.IsWearable && (layer == Layer.Face || layer == Layer.Beard || layer == Layer.Hair));
        }

        private static bool Matches(Item item, string filter)
        {
            return string.IsNullOrWhiteSpace(filter)
                || NameOf(item).IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NameOf(Item item)
        {
            return string.IsNullOrEmpty(item.Name) ? item.ItemData.Name ?? string.Empty : item.Name;
        }

        private static string SortName(int sort) =>
            sort switch
            {
                1 => "By name",
                2 => "By type",
                _ => "Unsorted"
            };

        /// <summary>
        /// As many rows as fit between the top bar and the touch bar, so a
        /// page never runs off a phone's screen.
        /// </summary>
        private static int MaxRows()
        {
            int height = Client.Game.ClientBounds.Height;
            float bar = GUO.Input.Touch.TouchInput.Bar?.ReservedFraction ?? 0f;
            int free = height - (int)Math.Ceiling(bar * height) - 40;
            int chrome = PAD * 2 + 3 * (ROW_HEIGHT + GAP);

            return Math.Clamp((free - chrome) / (SlotSize + GAP), 1, 12);
        }

        private void Layout(int rows)
        {
            int size = SlotSize;
            int width = PAD * 2 + COLUMNS * size + (COLUMNS - 1) * GAP;
            int top = PAD + 2 * (ROW_HEIGHT + GAP);
            int gridHeight = Math.Max(rows, 2) * (size + GAP) - GAP;
            bool paged = _pageCount > 1;
            int height = top + gridHeight + PAD + (paged ? ROW_HEIGHT + GAP : 0);

            Width = _background.Width = width;
            Height = _background.Height = height;

            _classicButton.X = width - PAD - _classicButton.Width;
            _sortButton.X = width - PAD - _sortButton.Width;

            _prev.IsVisible = _next.IsVisible = _pageLabel.IsVisible = paged;
            _prev.Y = _next.Y = height - PAD - ROW_HEIGHT;
            _next.X = width - PAD - _next.Width;
            _pageLabel.Text = $"{_page + 1} / {_pageCount}";
            _pageLabel.X = (width - _pageLabel.Width) / 2;
            _pageLabel.Y = _prev.Y + 2;
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            // A drop on the grid itself (the background, or an empty slot):
            // into this container, where the server chooses.
            if (button == MouseButtonType.Left && !UIManager.IsMouseOverWorld)
            {
                DropHeld(LocalSerial, 0xFFFF, 0xFFFF);
            }
        }

        /// <summary>
        /// Drops the held item as ContainerGump.OnMouseUp would: only within
        /// reach of the container's root, with the refusal sound otherwise.
        /// </summary>
        internal void DropHeld(uint into, int x, int y)
        {
            var hold = Client.Game.UO.GameCursor.ItemHold;

            if (!hold.Enabled || hold.IsFixedPosition)
            {
                return;
            }

            Item container = World.Items.Get(LocalSerial);
            Entity root = container != null ? World.Get(container.RootContainer) : null;

            if (root == null || root.Distance > Constants.DRAG_ITEMS_DISTANCE)
            {
                Client.Game.Audio.PlaySound(0x0051);

                return;
            }

            GameActions.DropItem(hold.Serial, x, y, 0, into);
            Mouse.CancelDoubleClick = true;
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (IsDisposed || !IsVisible)
            {
                return false;
            }

            base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            float layerDepth = layerDepthRef;
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    batcher.DrawRectangle(SolidColorTextureCache.GetTexture(Color.Gray), x, y, Width, Height, hueVector, layerDepth);

                    return true;
                }
            );

            return true;
        }

        public override void Save(XmlTextWriter writer)
        {
            // Written as a ContainerGump would be, so gumps.xml restores it
            // through ContainerGump.Restore, which reopens the container.
            base.Save(writer);
            writer.WriteAttributeString("graphic", Graphic.ToString());
            writer.WriteAttributeString("isminimized", false.ToString());
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);
            Dispose();
        }

        public override void Dispose()
        {
            Item item = World.Items.Get(LocalSerial);

            if (item != null)
            {
                if (World.Player != null && ProfileManager.CurrentProfile?.OverrideContainerLocationSetting == 3)
                {
                    UIManager.SavePosition(item, Location);
                }

                for (LinkedObject i = item.Items; i != null; i = i.Next)
                {
                    Item child = (Item)i;

                    if (child.Container == item)
                    {
                        UIManager.GetGump<ContainerGump>(child)?.Dispose();
                        UIManager.GetGump<GridContainerGump>(child)?.Dispose();
                    }
                }
            }

            base.Dispose();
        }

        /// <summary>
        /// One slot: the whole square is the hit area, which is what a
        /// finger needs, unlike ItemGump's pixel test against the art.
        /// </summary>
        private sealed class GridSlot : Control
        {
            private readonly GridContainerGump _gump;
            private readonly Label _amount;

            public GridSlot(GridContainerGump gump, Item item, int size)
            {
                _gump = gump;
                LocalSerial = item?.Serial ?? 0;
                Width = Height = size;
                AcceptMouseInput = true;
                CanMove = false;
                WantUpdateSize = false;
                // A long-press on an item is its context menu, not "close".
                CanCloseWithRightClick = item == null;

                if (item == null)
                {
                    return;
                }

                if (_gump.World.ClientFeatures.TooltipsEnabled)
                {
                    SetTooltip(item);
                }

                if (item.ItemData.IsStackable && item.Amount > 1)
                {
                    Add(_amount = new Label(item.Amount.ToString(), true, 0x0481, 0, 1, FontStyle.BlackBorder));
                    _amount.X = size - _amount.Width - 2;
                    _amount.Y = size - _amount.Height;
                }
            }

            private Item Item => LocalSerial != 0 ? _gump.World.Items.Get(LocalSerial) : null;

            public override void Update()
            {
                if (IsDisposed)
                {
                    return;
                }

                base.Update();

                if (LocalSerial == 0 || !_gump.World.InGame)
                {
                    return;
                }

                // ItemGump's pick-up test: a held press that has moved, or
                // one held past the double-click window.
                if (
                    !Client.Game.UO.GameCursor.ItemHold.Enabled
                    && Mouse.LButtonPressed
                    && UIManager.LastControlMouseDown(MouseButtonType.Left) == this
                    && (
                        Mouse.LastLeftButtonClickTime != 0xFFFF_FFFF
                            && Mouse.LastLeftButtonClickTime != 0
                            && Mouse.LastLeftButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK < Time.Ticks
                        || Dragged()
                    )
                )
                {
                    PickUp();
                }
                else if (MouseIsOver)
                {
                    SelectedObject.Object = Item;
                }
            }

            private static bool Dragged()
            {
                Point offset = Mouse.LDragOffset;

                return Math.Abs(offset.X) >= Constants.MIN_PICKUP_DRAG_DISTANCE_PIXELS
                    || Math.Abs(offset.Y) >= Constants.MIN_PICKUP_DRAG_DISTANCE_PIXELS;
            }

            private void PickUp()
            {
                Item item = Item;

                if (item == null)
                {
                    return;
                }

                ref readonly var art = ref Client.Game.UO.Arts.GetArt(item.DisplayedGraphic);

                GameActions.PickUp(_gump.World, LocalSerial, art.UV.Width >> 1, art.UV.Height >> 1);
            }

            protected override void OnMouseUp(int x, int y, MouseButtonType button)
            {
                World world = _gump.World;
                Item item = Item;

                if (button == MouseButtonType.Right)
                {
                    if (item != null)
                    {
                        GameActions.OpenPopupMenu(LocalSerial);
                    }

                    return;
                }

                if (button != MouseButtonType.Left)
                {
                    return;
                }

                if (world.TargetManager.IsTargeting && !Client.Game.UO.GameCursor.ItemHold.Enabled && item != null)
                {
                    world.TargetManager.Target(LocalSerial);
                    Mouse.CancelDoubleClick = true;

                    return;
                }

                if (Client.Game.UO.GameCursor.ItemHold.Enabled)
                {
                    var hold = Client.Game.UO.GameCursor.ItemHold;

                    // On a bag: into the bag. On the same stackable: onto
                    // the stack, as ContainerGump does. Anywhere else: into
                    // this container.
                    if (item != null && item.ItemData.IsContainer)
                    {
                        _gump.DropHeld(item.Serial, 0xFFFF, 0xFFFF);
                    }
                    else if (item != null && item.ItemData.IsStackable && item.Graphic == hold.Graphic)
                    {
                        _gump.DropHeld(item.Serial, item.X, item.Y);
                    }
                    else
                    {
                        _gump.DropHeld(_gump.LocalSerial, 0xFFFF, 0xFFFF);
                    }

                    return;
                }

                if (item != null && !world.DelayedObjectClickManager.IsEnabled)
                {
                    Point off = Mouse.LDragOffset;

                    world.DelayedObjectClickManager.Set(
                        LocalSerial,
                        Mouse.Position.X - off.X - ScreenCoordinateX,
                        Mouse.Position.Y - off.Y - ScreenCoordinateY,
                        Time.Ticks + Mouse.MOUSE_DELAY_DOUBLE_CLICK
                    );
                }
            }

            protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
            {
                if (button != MouseButtonType.Left || LocalSerial == 0 || _gump.World.TargetManager.IsTargeting)
                {
                    return false;
                }

                GameActions.DoubleClick(_gump.World, LocalSerial);

                return true;
            }

            public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
            {
                if (IsDisposed)
                {
                    return false;
                }

                float layerDepth = layerDepthRef;
                Item item = Item;
                bool lit = MouseIsOver || item != null && item.Serial == SelectedObject.SelectedContainer?.Serial;
                Vector3 frame = ShaderHueTranslator.GetHueVector(0);
                Vector3 fill = ShaderHueTranslator.GetHueVector(0, false, lit ? 0.35f : 0.45f);

                renderLists.AddGumpNoAtlas(
                    batcher =>
                    {
                        batcher.Draw(
                            SolidColorTextureCache.GetTexture(lit ? Color.Yellow : Color.Black),
                            new Rectangle(x, y, Width, Height),
                            fill,
                            layerDepth
                        );
                        batcher.DrawRectangle(SolidColorTextureCache.GetTexture(Color.Gray), x, y, Width, Height, frame, layerDepth);

                        return true;
                    }
                );

                if (item != null)
                {
                    ref readonly var art = ref Client.Game.UO.Arts.GetArt(item.DisplayedGraphic);
                    Rectangle bounds = Client.Game.UO.Arts.GetRealArtBounds(item.DisplayedGraphic);

                    if (art.Texture != null && bounds.Width > 0 && bounds.Height > 0)
                    {
                        // Native size when it fits; otherwise shrunk, whole,
                        // to fit. Never filtered: the batcher samples nearest.
                        int room = Width - 4;
                        float fit = Math.Min(1f, Math.Min(room / (float)bounds.Width, room / (float)bounds.Height));
                        int w = Math.Max(1, (int)(bounds.Width * fit));
                        int h = Math.Max(1, (int)(bounds.Height * fit));
                        Rectangle dest = new Rectangle(x + (Width - w) / 2, y + (Height - h) / 2, w, h);
                        Rectangle src = new Rectangle(art.UV.X + bounds.X, art.UV.Y + bounds.Y, bounds.Width, bounds.Height);
                        Vector3 hue = ShaderHueTranslator.GetHueVector(item.Hue, item.ItemData.IsPartialHue, 1f);
                        var texture = art.Texture;

                        renderLists.AddGumpWithAtlas(
                            batcher =>
                            {
                                batcher.Draw(texture, dest, src, hue, layerDepth);

                                return true;
                            }
                        );
                    }
                }

                // The amount label draws over the art.
                return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
            }
        }
    }
}
