// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Compat;
using System;
using System.Collections.Generic;
using System.Xml;

namespace GUO.Game.UI.Gumps
{
    internal class Gump : Control
    {
        public Gump(World world, uint local, uint server)
        {
            World = world;
            LocalSerial = local;
            ServerSerial = server;
            AcceptMouseInput = false;
            AcceptKeyboardInput = false;
        }

        public World World { get; }

        // PORT DEVIATION (GUO): optional display transform, independent of server layout.
        public float PresentationScale { get; set; } = 1f;
        public bool PresentationLocked { get; set; }
        public bool PresentationPlaced { get; set; }
        internal Point? MainPresentationPosition, SecondPresentationPosition;

        public bool CanBeSaved => GumpType != Gumps.GumpType.None;

        public virtual GumpType GumpType { get; }

        public bool InvalidateContents { get; set; }

        public uint MasterGumpSerial { get; set; }


        public override void Update()
        {
            if (InvalidateContents)
            {
                UpdateContents();
                InvalidateContents = false;
            }

            if (ActivePage == 0)
            {
                ActivePage = 1;
            }

            base.Update();
        }

        public override void Dispose()
        {
            Item it = World.Items.Get(LocalSerial);

            if (it != null && it.Opened)
            {
                it.Opened = false;
            }

            base.Dispose();
        }


        public virtual void Save(XmlTextWriter writer)
        {
            writer.WriteAttributeString("type", ((int) GumpType).ToString());
            writer.WriteAttributeString("x", X.ToString());
            writer.WriteAttributeString("y", Y.ToString());
            writer.WriteAttributeString("serial", LocalSerial.ToString());
            writer.WriteAttributeString("ui_scale", PresentationScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("ui_scale_locked", PresentationLocked.ToString());
            writer.WriteAttributeString("ui_placed", PresentationPlaced.ToString());
        }

        public void SetInScreen()
        {
            Rectangle windowBounds = Client.Game.ClientBounds;
            // PORT DEVIATION (GUO): the second screen extends the window to
            // the right while it is active (zero otherwise); see DualScreen.
            windowBounds.Width += GUO.Platform.Android.DualScreen.ExtraWidth;
            Rectangle bounds = Bounds;
            bounds.X += windowBounds.X;
            bounds.Y += windowBounds.Y;

            if (windowBounds.Intersects(bounds))
            {
                return;
            }

            X = 0;
            Y = 0;
        }

        public virtual void Restore(XmlElement xml)
        {
            if (float.TryParse(xml.GetAttribute("ui_scale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float scale) && float.IsFinite(scale))
                PresentationScale = Math.Clamp(scale, GUO.Input.Touch.GumpPresentation.MinScale, GUO.Input.Touch.GumpPresentation.MaxScale);
            PresentationLocked = bool.TryParse(xml.GetAttribute("ui_scale_locked"), out bool locked) && locked;
            PresentationPlaced = bool.TryParse(xml.GetAttribute("ui_placed"), out bool placed) && placed;
        }

        public void RequestUpdateContents()
        {
            InvalidateContents = true;
        }

        protected virtual void UpdateContents()
        {
        }

        protected override void OnDragEnd(int x, int y)
        {
            Point position = Location;
            int halfWidth = Width - (Width >> 2);
            int halfHeight = Height - (Height >> 2);

            if (X < -halfWidth)
            {
                position.X = -halfWidth;
            }

            if (Y < -halfHeight)
            {
                position.Y = -halfHeight;
            }

            // PORT DEVIATION (GUO): the second screen extends the window to
            // the right while it is active (zero otherwise); see DualScreen.
            if (X > Client.Game.ClientBounds.Width + GUO.Platform.Android.DualScreen.ExtraWidth - (Width - halfWidth))
            {
                position.X = Client.Game.ClientBounds.Width + GUO.Platform.Android.DualScreen.ExtraWidth - (Width - halfWidth);
            }

            if (Y > Client.Game.ClientBounds.Height - (Height - halfHeight))
            {
                position.Y = Client.Game.ClientBounds.Height - (Height - halfHeight);
            }

            Location = position;
            if (GUO.Input.Touch.GumpPresentation.Active
                && (PresentationScale != 1f || GUO.Platform.Android.DualScreen.ShelfOn))
                GUO.Input.Touch.GumpPresentation.Clamp(this);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            return IsVisible && base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        public override void OnButtonClick(int buttonID)
        {
            if (!IsDisposed && LocalSerial != 0)
            {
                List<uint> switches = new List<uint>();
                List<Tuple<ushort, string>> entries = new List<Tuple<ushort, string>>();

                foreach (Control control in Children)
                {
                    switch (control)
                    {
                        case Checkbox checkbox when checkbox.IsChecked:
                            switches.Add(control.LocalSerial);

                            break;

                        case StbTextBox textBox:
                            entries.Add(new Tuple<ushort, string>((ushort) textBox.LocalSerial, textBox.Text));

                            break;
                    }
                }

                GameActions.ReplyGump
                (
                    LocalSerial,
                    // Seems like MasterGump serial does not work as expected.
                    /*MasterGumpSerial != 0 ? MasterGumpSerial :*/ ServerSerial,
                    buttonID,
                    switches.ToArray(),
                    entries.ToArray()
                );

                if (CanMove)
                {
                    UIManager.SavePosition(ServerSerial, Location);
                }
                else
                {
                    UIManager.RemovePosition(ServerSerial);
                }

                Dispose();
            }
        }

        protected override void CloseWithRightClick()
        {
            if (!CanCloseWithRightClick)
            {
                return;
            }

            if (ServerSerial != 0)
            {
                OnButtonClick(0);
            }

            base.CloseWithRightClick();
        }

        public override void ChangePage(int pageIndex)
        {
            // For a gump, Page is the page that is drawing.
            ActivePage = pageIndex;
        }
    }
}
