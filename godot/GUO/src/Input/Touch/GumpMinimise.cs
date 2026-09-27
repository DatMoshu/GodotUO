// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch;

/// <summary>
/// Gumps minimised to the touch bar (the flick action "Minimise to the touch
/// bar"): hidden on the client only, a chip with their name on the bar, and a
/// tap on the chip shows them again where they were, at the size they were.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. Nothing is sent to the server; the
/// gump stays open and keeps updating, it is just not drawn or hit. A gump that
/// is closed or shown again by other means loses its chip. Minimised state
/// lasts the session: a gump saved while hidden comes back visible next time.
/// </remarks>
internal static class GumpMinimise
{
    private static readonly List<Gump> _gumps = new();

    /// <summary>The minimised gumps, oldest first; closed ones dropped.</summary>
    public static IReadOnlyList<Gump> Gumps
    {
        get
        {
            _gumps.RemoveAll(g => g == null || g.IsDisposed || g.IsVisible);
            return _gumps;
        }
    }

    public static bool Contains(Gump g) => _gumps.Contains(g);

    public static void Minimise(Gump g)
    {
        if (g == null || g.IsDisposed || _gumps.Contains(g))
        {
            return;
        }

        g.IsVisible = false;
        _gumps.Add(g);
    }

    /// <summary>Show the gump again, unchanged in place and size, on top.</summary>
    public static void Restore(Gump g)
    {
        _gumps.Remove(g);

        if (g == null || g.IsDisposed)
        {
            return;
        }

        g.IsVisible = true;
        g.BringOnTop();
    }

    /// <summary>A short name for a chip: the container's item name, else the kind of gump.</summary>
    public static string Title(Gump g)
    {
        string title = g switch
        {
            PaperDollGump => "Paperdoll",
            StatusGumpBase => "Status",
            JournalGump or ResizableJournal => "Journal",
            _ => null,
        };

        if (title == null && (g is ContainerGump || g is GridContainerGump))
        {
            Item item = g.World?.Items.Get(g.LocalSerial);
            title = item == null ? "Container" : !string.IsNullOrEmpty(item.Name) ? item.Name : item.ItemData.Name;
        }

        title ??= g.GetType().Name.Replace("Gump", "");

        // A chip stays a chip: long names are cut.
        return title.Length > 16 ? title.Substring(0, 15) + "…" : title;
    }
}
