// SPDX-License-Identifier: BSD-2-Clause

using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using GUO.Input;
using System;
using System.Linq;

namespace GUO.Game.UI.Controls
{
    internal class Combobox : Control
    {
        private readonly byte _font;
        private readonly string[] _items;
        private readonly Label _label;
        private readonly int _maxHeight;
        private int _selectedIndex;

        public Combobox
        (
            int x,
            int y,
            int width,
            string[] items,
            int selected = -1,
            int maxHeight = 200,
            bool showArrow = true,
            string emptyString = "",
            byte font = 9
        )
        {
            X = x;
            Y = y;
            Width = width;
            Height = 25;
            SelectedIndex = selected;
            _font = font;
            _items = items;
            _maxHeight = maxHeight;

            Add
            (
                new ResizePic(0x0BB8)
                {
                    Width = width, Height = Height
                }
            );

            string initialText = selected > -1 ? items[selected] : emptyString;

            bool isAsianLang = string.Compare(Settings.GlobalSettings.Language, "CHT", StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(Settings.GlobalSettings.Language, "KOR", StringComparison.InvariantCultureIgnoreCase) == 0 ||
                string.Compare(Settings.GlobalSettings.Language, "JPN", StringComparison.InvariantCultureIgnoreCase) == 0;

            bool unicode = isAsianLang;
            byte font1 = (byte)(isAsianLang ? 1 : _font);

            Add
            (
                _label = new Label(initialText, unicode, 0x0453, font: font1)
                {
                    X = 2, Y = 5
                }
            );

            if (showArrow)
            {
                Add(new GumpPic(width - 18, 2, 0x00FC, 0));
            }
        }


        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value;

                if (_items != null)
                {
                    _label.Text = _items[value];

                    OnOptionSelected?.Invoke(this, value);
                }
            }
        }


        public event EventHandler<int> OnOptionSelected;


        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            renderLists.AddGumpNoAtlas
            (
                (batcher) =>
                {
                    // work-around to allow clipping children
                    RenderLists comboBoxRenderLists = new();
                    base.AddToRenderLists(comboBoxRenderLists, x, y, ref layerDepth);

                    if (batcher.ClipBegin(x, y, Width, Height))
                    {
                        comboBoxRenderLists.DrawRenderLists(batcher, sbyte.MaxValue);
                        batcher.ClipEnd();
                    }
                    return true;
                }
            );



            return true;
        }


        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button != MouseButtonType.Left)
            {
                return;
            }

            // PORT DEVIATION (GUO): in a gump drawn scaled (GumpPresentation,
            // Options on touch), the list opens where the box is drawn and at
            // its scale; unscaled it is upstream's arithmetic unchanged.
            float scale = GUO.Input.Touch.GumpPresentation.Scale(this);
            GUO.Compat.Point drawn = GUO.Input.Touch.GumpPresentation.ToScreen(this, new GUO.Compat.Point(ScreenCoordinateX, ScreenCoordinateY + Offset.Y));
            int comboY = drawn.Y;
            int listHeight = (int)(_maxHeight * scale);

            if (comboY < 0)
            {
                comboY = 0;
            }
            else if (comboY + listHeight > Client.Game.ClientBounds.Height)
            {
                comboY = Client.Game.ClientBounds.Height - listHeight;
            }

            var list = new ComboboxGump
            (
                // might crash
                (RootParent as Gump).World,
                drawn.X,
                comboY,
                Width,
                _maxHeight,
                _items,
                _font,
                this
            );

            GUO.Input.Touch.GumpPresentation.Follow(list, this);
            UIManager.Add(list);

            base.OnMouseUp(x, y, button);
        }

        private class ComboboxGump : Gump
        {
            private const int ELEMENT_HEIGHT = 15;
            private const int TOUCH_ROW_HEIGHT = 26; // PORT DEVIATION (GUO): TouchRows; Modern Options' row height


            private readonly Combobox _combobox;

            public ComboboxGump
            (
                World world,
                int x,
                int y,
                int width,
                int maxHeight,
                string[] items,
                byte font,
                Combobox combobox
            ) : base(world, 0, 0)
            {
                CanMove = false;
                AcceptMouseInput = true;
                X = x;
                Y = y;

                IsModal = true;
                LayerOrder = UILayer.Over;
                ModalClickOutsideAreaClosesThisControl = true;

                _combobox = combobox;

                ResizePic background;
                Add(background = new ResizePic(0x0BB8));
                background.AcceptMouseInput = false;

                HoveredLabel[] labels = new HoveredLabel[items.Length];

                bool isAsianLang = string.Compare(Settings.GlobalSettings.Language, "CHT", StringComparison.InvariantCultureIgnoreCase) == 0 ||
                    string.Compare(Settings.GlobalSettings.Language, "KOR", StringComparison.InvariantCultureIgnoreCase) == 0 ||
                    string.Compare(Settings.GlobalSettings.Language, "JPN", StringComparison.InvariantCultureIgnoreCase) == 0;

                bool unicode = isAsianLang;
                byte font1 = (byte)(isAsianLang ? 1 : font);

                for (int i = 0; i < items.Length; i++)
                {
                    string item = items[i];

                    if (item == null)
                    {
                        item = string.Empty;
                    }

                    HoveredLabel label = new HoveredLabel
                    (
                        item,
                        unicode,
                        0x0453,
                        0x0453,
                        0x0453,
                        font: font1
                    )
                    {
                        X = 2,
                        Y = i * ELEMENT_HEIGHT,
                        DrawBackgroundCurrentIndex = true,
                        IsVisible = item.Length != 0,
                        Tag = i
                    };

                    label.MouseUp += LabelOnMouseUp;

                    labels[i] = label;
                }

                // PORT DEVIATION (GUO): on touch each row is a finger's height
                // (TouchRows); with a mouse this is upstream's list unchanged.
                if (GUO.Input.Touch.TouchInput.Enabled)
                {
                    TouchRows(labels, maxHeight, width);

                    return;
                }

                int totalHeight = Math.Min(maxHeight, labels.Max(o => o.Y + o.Height));
                int maxWidth = Math.Max(width, labels.Max(o => o.X + o.Width));

                ScrollArea area = new ScrollArea
                (
                    0,
                    0,
                    maxWidth + 15,
                    totalHeight,
                    true
                );

                foreach (HoveredLabel label in labels)
                {
                    label.Width = maxWidth;
                    area.Add(label);
                }

                Add(area);

                background.Width = maxWidth;
                background.Height = totalHeight;
            }


            public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
            {
                float layerDepth = layerDepthRef + 100f; // Combo list should be over other gumps
                renderLists.AddGumpNoAtlas
                (
                    (batcher) =>
                    {
                        // work-around to allow clipping children
                        RenderLists comboBoxRenderLists = new();
                        base.AddToRenderLists(comboBoxRenderLists, x, y, ref layerDepth);

                        if (batcher.ClipBegin(x, y, Width, Height))
                        {
                            comboBoxRenderLists.DrawRenderLists(batcher, sbyte.MaxValue);
                            batcher.ClipEnd();
                        }
                        return true;
                    }
                );

                return true;
            }

            /// <summary>
            /// PORT DEVIATION (GUO): the list on touch. Rows are TOUCH_ROW_HEIGHT
            /// apart, each one a hit target as tall as the row, the text centred in
            /// it, so a finger picks the row it lands on. Same items, same pick.
            /// </summary>
            private void TouchRows(HoveredLabel[] labels, int maxHeight, int width)
            {
                ResizePic background = (ResizePic) Children[0];
                int maxWidth = Math.Max(width, labels.Max(o => o.X + o.Width));
                int totalHeight = Math.Min(maxHeight, labels.Length * TOUCH_ROW_HEIGHT);

                ScrollArea area = new ScrollArea(0, 0, maxWidth + 15, totalHeight, true);

                foreach (HoveredLabel label in labels)
                {
                    int i = (int) label.Tag;
                    Control row = new HitBox(0, 0, maxWidth, TOUCH_ROW_HEIGHT, null, 0f)
                    {
                        Y = i * TOUCH_ROW_HEIGHT,
                        Tag = i,
                        IsVisible = label.IsVisible
                    };
                    row.MouseUp += LabelOnMouseUp;
                    label.Width = maxWidth;
                    label.Y = (TOUCH_ROW_HEIGHT - label.Height) / 2;
                    row.Add(label);
                    area.Add(row);
                }

                Add(area);

                background.Width = maxWidth;
                background.Height = totalHeight;
            }

            private void LabelOnMouseUp(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtonType.Left)
                {
                    _combobox.SelectedIndex = (int) ((Control) sender).Tag; // PORT DEVIATION (GUO): a touch row is a Control

                    Dispose();
                }
            }
        }
    }
}