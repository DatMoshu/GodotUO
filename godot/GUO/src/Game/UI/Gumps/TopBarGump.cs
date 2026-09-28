// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Assets;
using GUO.Network;
using GUO.Renderer;
using GUO.Resources;
using GUO.Utility;
using GUO.Utility.Logging;
using GUO.Compat;
namespace GUO.Game.UI.Gumps
{
    internal class TopBarGump : Gump
    {
        private TopBarGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = false;

            // little
            Add(new ResizePic(0x13BE) { Width = 30, Height = 27 }, 2);

            Add(
                new Button(0, 0x15A1, 0x15A1, 0x15A1)
                {
                    X = 5,
                    Y = 3,
                    ToPage = 1
                },
                2
            );

            // big
            int smallWidth = 50;
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x098B);
            if (gumpInfo.Texture != null)
            {
                smallWidth = gumpInfo.UV.Width;
            }

            int largeWidth = 100;

            gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x098D);
            if (gumpInfo.Texture != null)
            {
                largeWidth = gumpInfo.UV.Width;
            }

            int[][] textTable =
            {
                new[] { 0, (int)Buttons.Map },
                new[] { 1, (int)Buttons.Paperdoll },
                new[] { 1, (int)Buttons.Inventory },
                new[] { 1, (int)Buttons.Journal },
                new[] { 0, (int)Buttons.Chat },
                new[] { 0, (int)Buttons.Help },
                new[] { 1, (int)Buttons.WorldMap },
                new[] { 0, (int)Buttons.Info },
                new[] { 0, (int)Buttons.Debug },
                new[] { 1, (int)Buttons.NetStats },
                new[] { 1, (int)Buttons.UOStore },
                new[] { 1, (int)Buttons.GlobalChat }
            };

            var cliloc = Client.Game.UO.FileManager.Clilocs;

            string[] texts =
            {
                cliloc.GetString(3000430, ResGumps.Map),
                cliloc.GetString(3000133, ResGumps.Paperdoll),
                cliloc.GetString(3000431, ResGumps.Inventory),
                cliloc.GetString(3000129, ResGumps.Journal),
                cliloc.GetString(3000131, ResGumps.Chat),
                cliloc.GetString(3000134, ResGumps.Help),
                StringHelper.CapitalizeAllWords(cliloc.GetString(1015233, ResGumps.WorldMap)),
                cliloc.GetString(1079449, ResGumps.Info),
                cliloc.GetString(1042237, ResGumps.Debug),
                cliloc.GetString(3000169, ResGumps.NetStats),
                cliloc.GetString(1158008, ResGumps.UOStore),
                cliloc.GetString(1158390, ResGumps.GlobalChat)
            };

            bool hasUOStore = Client.Game.UO.Version >= ClientVersion.CV_706400;

            ResizePic background;

            Add(background = new ResizePic(0x13BE) { Height = 27 }, 1);

            Add(
                new Button(0, 0x15A4, 0x15A4, 0x15A4)
                {
                    X = 5,
                    Y = 3,
                    ToPage = 2
                },
                1
            );

            int startX = 30;

            // PORT DEVIATION (GUO): on a touch screen narrower than the bar (the
            // Thor's 1920x1080 at 2x is 960 wide; the bar is about 1110), the
            // buttons wrap to another row instead of running off the right edge.
            // Off the touch layer: one false test, and the bar is upstream's.
            int wrapAt = GUO.Input.Touch.TouchInput.Enabled ? GUO.Input.Touch.GumpPresentation.DisplayBounds(false).Width : int.MaxValue;
            int row = 0, widest = 0;
            WrapWidth = wrapAt;

            for (int i = 0; i < textTable.Length; i++)
            {
                if (!hasUOStore && i >= (int)Buttons.UOStore)
                {
                    break;
                }

                // PORT DEVIATION (GUO): the wrap; see above.
                if (startX > 30 && startX + (textTable[i][0] != 0 ? largeWidth : smallWidth) + 2 > wrapAt)
                {
                    widest = System.Math.Max(widest, startX);
                    startX = 30;
                    row++;
                    background.Height = 27 + row * RowStep;
                }

                ushort graphic = (ushort)(textTable[i][0] != 0 ? 0x098D : 0x098B);

                Add(
                    new RighClickableButton(
                        textTable[i][1],
                        graphic,
                        graphic,
                        graphic,
                        texts[i],
                        1,
                        true,
                        0,
                        0x0036
                    )
                    {
                        ButtonAction = ButtonAction.Activate,
                        X = startX,
                        Y = 1 + row * RowStep, // PORT DEVIATION (GUO): the wrap
                        FontCenter = true
                    },
                    1
                );

                startX += (textTable[i][0] != 0 ? largeWidth : smallWidth) + 1;
                background.Width = startX;
            }

            background.Width = System.Math.Max(widest, startX) + 1; // PORT DEVIATION (GUO): the widest row

            //layer
            LayerOrder = UILayer.Over;
        }

        private const int RowStep = 26; // PORT DEVIATION (GUO): a wrapped row, the button's height and a pixel

        /// <summary>PORT DEVIATION (GUO): the screen width the buttons were wrapped for (see the constructor).</summary>
        internal int WrapWidth { get; private set; }

        /// <summary>
        /// PORT DEVIATION (GUO): the touch layer, once a frame: a screen whose
        /// width changed (a rotation, a resize) gets the bar built again for it,
        /// as Options rebuilds it.
        /// </summary>
        internal static void Rewrap(World world)
        {
            TopBarGump gump = UIManager.GetGump<TopBarGump>();

            if (gump == null || gump.IsDisposed || gump.WrapWidth == GUO.Input.Touch.GumpPresentation.DisplayBounds(false).Width)
            {
                return;
            }

            gump.Dispose();
            Create(world);
        }

        public bool IsMinimized { get; private set; }

        public static void Create(World world)
        {
            TopBarGump gump = UIManager.GetGump<TopBarGump>();

            if (gump == null)
            {
                if (
                    ProfileManager.CurrentProfile.TopbarGumpPosition.X < 0
                    || ProfileManager.CurrentProfile.TopbarGumpPosition.Y < 0
                )
                {
                    ProfileManager.CurrentProfile.TopbarGumpPosition = Point.Zero;
                }

                UIManager.Add(
                    gump = new TopBarGump(world)
                    {
                        X = ProfileManager.CurrentProfile.TopbarGumpPosition.X,
                        Y = ProfileManager.CurrentProfile.TopbarGumpPosition.Y
                    }
                );

                if (ProfileManager.CurrentProfile.TopbarGumpIsMinimized)
                {
                    gump.ChangePage(2);
                }
            }
            else
            {
                Log.Error(ResGumps.TopBarGumpAlreadyExists);
            }
        }

        protected override void OnMouseUp(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Right && (X != 0 || Y != 0))
            {
                X = 0;
                Y = 0;

                ProfileManager.CurrentProfile.TopbarGumpPosition = Location;
            }
        }

        public override void OnPageChanged()
        {
            ProfileManager.CurrentProfile.TopbarGumpIsMinimized = IsMinimized = ActivePage == 2;
            WantUpdateSize = true;
        }

        protected override void OnDragEnd(int x, int y)
        {
            base.OnDragEnd(x, y);
            ProfileManager.CurrentProfile.TopbarGumpPosition = Location;
        }

        public override void OnButtonClick(int buttonID)
        {
            switch ((Buttons)buttonID)
            {
                case Buttons.Map:
                    GameActions.OpenMiniMap(World);

                    break;

                case Buttons.Paperdoll:
                    GameActions.OpenPaperdoll(World, World.Player);

                    break;

                case Buttons.Inventory:
                    GameActions.OpenBackpack(World);

                    break;

                case Buttons.Journal:
                    GameActions.OpenJournal(World);

                    break;

                case Buttons.Chat:
                    GameActions.OpenChat(World);

                    break;

                case Buttons.GlobalChat:
                    Log.Warn(ResGumps.ChatButtonPushedNotImplementedYet);
                    GameActions.Print(
                        World,
                        ResGumps.GlobalChatNotImplementedYet,
                        0x23,
                        MessageType.System
                    );

                    break;

                case Buttons.UOStore:
                    if (Client.Game.UO.Version >= ClientVersion.CV_706400)
                    {
                        NetClient.Socket.Send_OpenUOStore();
                    }

                    break;

                case Buttons.Help:
                    GameActions.RequestHelp();

                    break;

                case Buttons.Debug:

                    DebugGump debugGump = UIManager.GetGump<DebugGump>();

                    if (debugGump == null)
                    {
                        debugGump = new DebugGump(World, 100, 100);
                        UIManager.Add(debugGump);
                    }
                    else
                    {
                        debugGump.IsVisible = !debugGump.IsVisible;
                        debugGump.SetInScreen();
                    }

                    break;

                case Buttons.NetStats:
                    NetworkStatsGump netstatsgump = UIManager.GetGump<NetworkStatsGump>();

                    if (netstatsgump == null)
                    {
                        netstatsgump = new NetworkStatsGump(World, 100, 100);
                        UIManager.Add(netstatsgump);
                    }
                    else
                    {
                        netstatsgump.IsVisible = !netstatsgump.IsVisible;
                        netstatsgump.SetInScreen();
                    }

                    break;

                case Buttons.WorldMap:
                    GameActions.OpenWorldMap(World);

                    break;
            }
        }

        private enum Buttons
        {
            Map,
            Paperdoll,
            Inventory,
            Journal,
            Chat,
            Help,
            WorldMap,
            Info,
            Debug,
            NetStats,
            UOStore,
            GlobalChat
        }

        private class RighClickableButton : Button
        {
            public RighClickableButton(
                int buttonID,
                ushort normal,
                ushort pressed,
                ushort over = 0,
                string caption = "",
                byte font = 0,
                bool isunicode = true,
                ushort normalHue = ushort.MaxValue,
                ushort hoverHue = ushort.MaxValue
            ) : base(buttonID, normal, pressed, over, caption, font, isunicode, normalHue, hoverHue)
            { }

            public RighClickableButton(List<string> parts) : base(parts) { }

            protected override void OnMouseUp(int x, int y, MouseButtonType button)
            {
                base.OnMouseUp(x, y, button);
                Parent?.InvokeMouseUp(new Point(x, y), button);
            }
        }
    }
}
