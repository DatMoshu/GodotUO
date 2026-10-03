#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GUO.Game;
using GUO.Game.UI.Gumps;
using GUO.UI.Authoring;

/// <summary>Explicit offline preview factories; never reflectively invokes arbitrary gump constructors.</summary>
internal static class ClientGumpLibrary
{
    internal static readonly IReadOnlyDictionary<string, Func<World, Gump>> Factories =
        new Dictionary<string, Func<World, Gump>>(StringComparer.OrdinalIgnoreCase)
        {
            ["StatusGumpOld"] = w => new StatusGumpOld(w),
            ["StatusGumpModern"] = w => new StatusGumpModern(w),
            ["PaperDollGump"] = w => new PaperDollGump(w, w.Player.Serial, false),
            ["JournalGump"] = w => new JournalGump(w),
            ["StandardSkillsGump"] = w => new StandardSkillsGump(w),
            ["OptionsGump"] = w => new OptionsGump(w),
            ["ChatGump"] = w => new ChatGump(w),
            ["PartyGump"] = w => new PartyGump(w, 0, 0, false),
            ["ProfileGump"] = w => new ProfileGump(w, w.Player.Serial, "Character profile", "", "Your story begins here.", true),
        };

    internal static GumpDocument Preview(string name, World world)
    {
        if (world?.Player == null) throw new InvalidOperationException("The editor's offline preview world is not ready.");
        if (!Factories.TryGetValue(name, out var create)) throw new InvalidOperationException("This gump needs a live capture. Open it in the game and run -gumpcapture.");
        Gump gump = null;
        try
        {
            gump = create(world);
            // Layout needs one update (e.g. ExpandableScroll positions its bottom here).
            gump.Update();
            var doc = GumpCapture.Capture(gump);
            if (doc.Elements.Count == 0) throw new InvalidDataException("This gump has no controls in the offline preview; use a live capture.");
            doc.Width = Math.Clamp(Math.Max(doc.Width, doc.Elements.Max(e => e.X + e.Width)), 16, 8192);
            doc.Height = Math.Clamp(Math.Max(doc.Height, doc.Elements.Max(e => e.Y + e.Height)), 16, 8192);
            doc.Name = name;
            doc.Notes.Insert(0, "Built-in preview using the editor's offline character. Use a live capture for your character's exact state.");
            return doc;
        }
        finally { gump?.Dispose(); }
    }
}
#endif
