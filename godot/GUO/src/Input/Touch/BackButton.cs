// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;
using Godot;

namespace GUO.Input.Touch
{
    /// <summary>
    /// What Android's Back button does. Godot's default quits the app on the
    /// spot, which on a handheld with a hardware Back key lost the session to
    /// one stray press, so project.godot turns quit_on_go_back off and
    /// GameController hands NOTIFICATION_WM_GO_BACK_REQUEST here instead:
    /// <list type="number">
    /// <item>close the Store window, which covers everything when it is up;</item>
    /// <item>close the top gump, as a right click on it would;</item>
    /// <item>else lower the on-screen keyboard;</item>
    /// <item>else ask the upstream "Quit Ultima Online?" question in the
    /// world, and before it quit as the login gump's Quit button does.</item>
    /// </list>
    /// Gumps shelved on the second screen are left alone: that screen is the
    /// player's standing HUD, not a stack of windows to back out of.
    /// </summary>
    public static class BackButton
    {
        public static void Handle()
        {
            if (CloseStore())
            {
                return;
            }

            if (BarEditor.IsOpen)
            {
                BarEditor.Close();
                GD.Print("[GUO] back: close BarEditor");

                return;
            }

            if (WindowMenu.IsOpen)
            {
                WindowMenu.Close();
                GD.Print("[GUO] back: close WindowMenu");

                return;
            }

            if (CloseTopGump())
            {
                return;
            }

            if (TouchInput.KeyboardShown)
            {
                TouchInput.HideKeyboard();
                GD.Print("[GUO] back: keyboard hidden");

                return;
            }

            if (Client.Game.Scene is GameScene game)
            {
                GD.Print("[GUO] back: quit prompt");
                game.RequestQuitGame();

                return;
            }

            GD.Print("[GUO] back: exit before the world");
            Client.Game.Exit();
        }

        /// <summary>Close a Store window (ADR-0019), a child of the controller, if one is up.</summary>
        private static bool CloseStore()
        {
            foreach (Godot.Node child in Client.Game.GetChildren())
            {
                if (child is GUO.Store.StoreWindow store && !store.IsQueuedForDeletion())
                {
                    store.QueueFree();
                    GD.Print("[GUO] back: close StoreWindow");

                    return true;
                }
            }

            return false;
        }

        private static bool CloseTopGump()
        {
            // UIManager.Gumps runs front to back.
            foreach (Gump gump in UIManager.Gumps)
            {
                if (gump.IsDisposed || !gump.IsVisible || !gump.CanCloseWithRightClick)
                {
                    continue;
                }

                if (DualScreen.Active && gump.X >= DualScreen.MainWidth)
                {
                    continue;
                }

                GD.Print($"[GUO] back: close {gump.GetType().Name}");
                gump.InvokeMouseCloseGumpWithRClick();

                return true;
            }

            return false;
        }
    }
}
