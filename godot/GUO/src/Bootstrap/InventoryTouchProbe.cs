// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Input.Touch;
using LegacyControl = GUO.Game.UI.Controls.Control;

namespace GUO.Host;

internal static partial class TouchProbe
{
    /// <summary>UX-03: real shard item actions driven through synthetic fingers.</summary>
    internal static async System.Threading.Tasks.Task RunInventory(Node host, string directory)
    {
        Checks.Clear();
        Platform.Android.DualScreenSettings.PanelForced = false;
        await Frames(host, 200);
        TouchInput.Enabled = false;
        bool emulation = Godot.Input.EmulateTouchFromMouse;
        Godot.Input.EmulateTouchFromMouse = false;
        InputProbe.PointerScale = GUO.Client.Game.DpiScale;
        try { await InputProbe.EnterTheWorld(host, 0); }
        finally
        {
            InputProbe.PointerScale = 1f;
            Godot.Input.EmulateTouchFromMouse = emulation;
        }
        var world = GUO.Client.Game.UO.World;
        Check("inventory probe reached the world", world.InGame);
        if (!world.InGame) { Finish(); return; }
        // Login enforces the desktop's 640x480 logical minimum. Native phone
        // acceptance needs its requested portrait surface after that scene exits.
        if (GameController.PinnedWindowSize is Vector2I requested)
        {
            host.GetWindow().MinSize = Vector2I.Zero;
            host.GetWindow().Size = requested;
            await Frames(host, 30);
            Check("native viewport matches the requested size", host.GetWindow().Size == requested,
                $"requested {requested}, actual {host.GetWindow().Size}");
        }
        var profile = ProfileManager.CurrentProfile;
        foreach (Gump g in UIManager.Gumps.ToArray())
            if (g is not WorldViewportGump && g is not TopBarGump) g.Dispose();
        TouchInput.Enabled = false;
        profile.TouchUi = null;
        InputMode.Note(new InputEventKey { Keycode = Key.Space, Pressed = true });
        Check("desktop Automatic remains off", !TouchInventory.Active);
        profile.TouchUi = true;
        Check("explicit On enables touch inventory on desktop", TouchInventory.Active);
        profile.TouchUi = false;
        TouchInput.Enabled = true;
        InputMode.Note(new InputEventScreenTouch { Pressed = true });
        Check("explicit Off wins over touch", !TouchInventory.Active);
        profile.TouchUi = null;
        Check("Automatic follows the touch pointer", TouchInventory.Active);
        profile.FlickUp = profile.FlickDown = profile.FlickLeft = profile.FlickRight = 0;
        profile.GridContainerSlotSize = 32; // Touch must still provide 44px slots.
        profile.GridContainers = false; // The shared Touch UI gate is sufficient.
        var doll = new PaperDollGump(world, world.Player.Serial, true);
        UIManager.Add(doll);
        doll.X = 4; doll.Y = GumpPresentation.FullHeightTop() + 4;
        await Frames(host, 30);
        Check("paperdoll uses touch layout", doll.IsTouchLayout);
        Check("paperdoll header clears the top bar", doll.Y >= GumpPresentation.FullHeightTop());
        var equipment = doll.Children.Where(c => c.GetType().Name == "EquipmentSlot").ToArray();
        Check("every equipment target is at least 44px", equipment.Length >= 21 && equipment.All(c => c.Width >= 44 && c.Height >= 44));
        Check("all action targets are at least 44px", doll.Children.OfType<GUO.Game.UI.Controls.NiceButton>().All(c => c.Width >= 44 && c.Height >= 44));
        var bag = world.Player.FindItemByLayer(Layer.Backpack);
        Check("character has a backpack", bag != null);
        if (bag == null) { Finish(); return; }
        await Tap(host, DrawnCentre(doll.Children.OfType<GUO.Game.UI.Controls.NiceButton>().First(b => b.ButtonParameter == 107)));
        await Frames(host, 90);
        var grid = UIManager.GetGump<GridContainerGump>(bag.Serial);
        Check("backpack button opens the touch grid", grid != null);
        if (grid == null) { Finish(); return; }
        if (GUO.Client.Game.ClientBounds.Width >= GUO.Client.Game.ClientBounds.Height)
        { grid.X = Math.Max(doll.X + doll.Width + 8, GUO.Client.Game.ClientBounds.Width - grid.Width - 4); grid.Y = doll.Y; }
        await Frames(host, 15);
        var slots = grid.Children.Where(c => c.GetType().Name == "GridSlot").ToArray();
        Check("touch slots clamp a saved 32px preference to 44px", slots.Length > 0 && slots.All(s => s.Width >= 44 && s.Height >= 44));
        if (GUO.Client.Game.ClientBounds.Width < GUO.Client.Game.ClientBounds.Height)
            Check("portrait backpack stays below the paperdoll", grid.Y >= doll.Y + GumpPresentation.Height(doll));

        // Native rendered proof, no data or profile contents in the log.
        if (!string.IsNullOrWhiteSpace(directory))
        {
            DirAccess.MakeDirRecursiveAbsolute(directory);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using Image frame = host.GetViewport().GetTexture().GetImage();
            frame.SavePng(directory.PathJoin($"inventory-{frame.GetWidth()}x{frame.GetHeight()}.png"));
        }

        LegacyControl filled = slots.FirstOrDefault(s => s.LocalSerial != 0);
        Check("backpack has an item for long press", filled != null);
        if (filled != null)
        {
            TouchInput.Trace.Clear();
            await Hold(host, DrawnCentre(filled), TouchInput.LongPressMs + 100);
            Check("holding an inventory item requests context without pickup",
                TouchInput.Trace.Contains("long press on inventory item -> context menu") && !GUO.Client.Game.UO.GameCursor.ItemHold.Enabled);
            UIManager.GetGump<PopupMenuGump>()?.Dispose();
        }

        // A real equipped garment, restored after the round trip. Drag starts
        // inside its square, not on opaque artwork; the server acknowledges both moves.
        var worn = equipment.FirstOrDefault(s => s.LocalSerial != 0 && s.LocalSerial != bag.Serial);
        Check("character has equipment for drag round trip", worn != null);
        if (worn != null)
        {
            uint serial = worn.LocalSerial;
            world.Items.TryGetValue(serial, out var item);
            Layer layer = item?.Layer ?? Layer.Invalid;
            async System.Threading.Tasks.Task FingerDrag(Vector2 from, Vector2 to)
            {
                TouchInput.CancelGesture();
                Touch(0, from, true); await Frames(host, 3);
                for (int i = 1; i <= 12; i++)
                { Drag(0, from.Lerp(to, i / 12f), (to - from) / 12f); await Frames(host, 3); }
                await Frames(host, 25);
                Touch(0, to, false); await Frames(host, 60);
            }
            var empty = slots.FirstOrDefault(s => s.LocalSerial == 0) ?? slots.Last();
            await FingerDrag(DrawnCentre(worn), DrawnCentre(empty));
            world.Items.TryGetValue(serial, out item);
            bool unequipped = item?.Container == bag.Serial && !GUO.Client.Game.UO.GameCursor.ItemHold.Enabled;
            Check("finger drag unequips into backpack (server acknowledged)", unequipped);
            if (unequipped)
            {
                var source = grid.Children.FirstOrDefault(c => c.GetType().Name == "GridSlot" && c.LocalSerial == serial);
                if (source != null) await FingerDrag(DrawnCentre(source), DrawnCentre(worn));
                Check("finger drag equips into the empty layer (server acknowledged)", world.Player.FindItemByLayer(layer)?.Serial == serial);
            }
            // A failed gesture must still return the character's equipment.
            if (world.Player.FindItemByLayer(layer)?.Serial != serial)
            {
                TouchInput.CancelGesture();
                if (!GUO.Client.Game.UO.GameCursor.ItemHold.Enabled) GameActions.PickUp(world, serial, 0, 0, 1);
                await Frames(host, 30);
                if (GUO.Client.Game.UO.GameCursor.ItemHold.Enabled) GameActions.Equip(world, world.Player.Serial);
                await Frames(host, 40);
            }
        }

        // Both fingers begin inside full slot hit areas; a second contact
        // claims the pinch before either contact can become an item drag.
        await Frames(host, 35);
        float scale = grid.PresentationScale;
        float zoom = GUO.Client.Game.Scene.Camera.Zoom;
        var centre = DrawnCentre(grid.Children.Where(c => c.GetType().Name == "GridSlot").ElementAt(2));
        var a = centre - new Vector2(36, 0); var b = centre + new Vector2(36, 0);
        TouchInput.CancelGesture();
        TouchInput.Trace.Clear();
        Touch(0, a, true); await Frames(host, 2);
        Touch(1, b, true); await Frames(host, 2);
        for (int i = 0; i < 6; i++)
        { a -= new Vector2(8, 0); b += new Vector2(8, 0); Drag(0, a, new Vector2(-8, 0)); Drag(1, b, new Vector2(8, 0)); await Frames(host, 2); }
        Touch(1, b, false); Touch(0, a, false); await Frames(host, 10);
        Check("pinch resizes backpack without world zoom", grid.PresentationScale > scale && GUO.Client.Game.Scene.Camera.Zoom == zoom,
            $"scale {scale}->{grid.PresentationScale}, zoom {zoom}->{GUO.Client.Game.Scene.Camera.Zoom}, modal {UIManager.IsModalOpen}; " + string.Join(" | ", TouchInput.Trace));
        if (GUO.Client.Game.ClientBounds.Width < GUO.Client.Game.ClientBounds.Height)
            Check("pinch preserves paperdoll clearance", grid.Y >= doll.Y + GumpPresentation.Height(doll));
        Finish();
        GD.Print($"[GUO] inventory touch probe: {(Passed ? "PASS" : "FAIL")}");
    }
}
