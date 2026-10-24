// SPDX-License-Identifier: BSD-2-Clause
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Compat;
using System;
using System.Runtime.CompilerServices;

namespace GUO.Input.Touch;

/// <summary>UX-03's shared presentation gate. Null follows the primary pointer.</summary>
internal static class TouchInventory
{
    private sealed class Placement { internal int Width, Height; }
    private static readonly ConditionalWeakTable<Gump, Placement> Placed = new();
    internal static bool Active => ProfileManager.CurrentProfile?.TouchUi
        ?? (TouchInput.Enabled && (InputMode.Current == InputKind.Touch
            || !Godot.DisplayServer.IsTouchscreenAvailable() && InputMode.Current != InputKind.Gamepad));

    // A phone stacks containers below the equipment, rather than cascading
    // across it. Landscape and the dual-screen shelf retain their placement.
    internal static Rectangle Room(Gump g)
    {
        Rectangle room = GumpPresentation.DisplayBounds(GumpPresentation.OnSecond(g));
        if (!Active || Platform.Android.DualScreen.ShelfOn || room.Width >= room.Height) return room;
        int top = GumpPresentation.FullHeightTop() + 4;
        if (g is GridContainerGump)
            foreach (Gump other in UIManager.Gumps)
                if (other is PaperDollGump { IsTouchLayout: true, IsDisposed: false, IsVisible: true })
                    top = Math.Max(top, other.Y + GumpPresentation.Height(other) + 8);
        int bottom = room.Bottom;
        room.Y = Math.Min(top, bottom - 44);
        room.Height = bottom - room.Y;
        return room;
    }

    internal static void Place(Gump g)
    {
        if (!Active || Platform.Android.DualScreen.ShelfOn) return;
        Rectangle room = Room(g);
        if (room.Width >= GumpPresentation.DisplayBounds(false).Height)
        {
            if (g is PaperDollGump { IsTouchLayout: true })
                g.Y = Math.Max(g.Y, GumpPresentation.FullHeightTop() + 4);
            return;
        }
        int width = GumpPresentation.Width(g), height = GumpPresentation.Height(g);
        if (Placed.TryGetValue(g, out var previous) && previous.Width == room.Width && previous.Height == room.Height
            && g.Y >= room.Y && g.Y + height <= room.Bottom && g.X >= room.X && g.X + width <= room.Right) return;
        g.X = room.X + Math.Max(4, (room.Width - width) / 2);
        g.Y = room.Y;
        // Weak keys do not retain disposed windows or their world after logout.
        Placement placement = Placed.GetValue(g, static _ => new Placement());
        placement.Width = room.Width;
        placement.Height = room.Height;
    }
}
