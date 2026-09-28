// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.UI.Gumps;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// Which client gumps have a Modern view (ADR-0024), and the one decision
/// of whether a gump opens as it: asked by UIManager.Add for every gump.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO. Modern only ever applies with the
/// touch layer on, so the desktop always gets the Classic gump, 1:1. A shard
/// gump never has a Modern view. A profile can turn Modern off
/// (Profile.ModernGumpsOff), and a Modern view's "Classic view" opens the
/// ported gump once (<see cref="OpenClassicNext"/>).
/// </remarks>
internal static class ModernGumps
{
    private static readonly Dictionary<Type, Func<World, ModernGump>> _factories = new()
    {
        [typeof(OptionsGump)] = world => new ModernOptions(world),
        [typeof(PartyGump)] = world => new ModernParty(world),
        [typeof(StandardSkillsGump)] = world => new ModernSkills(world),
        [typeof(SkillGumpAdvanced)] = world => new ModernSkills(world),
        [typeof(SpellbookGump)] = world => new ModernSpellbook(world),
        [typeof(MarkersManagerGump)] = world => new ModernMarkers(world),
        [typeof(CombatBookGump)] = world => new ModernAbilities(world),
    };

    private static readonly Dictionary<Type, ModernGump> _open = new();
    private static Type _classicNext;

    /// <summary>Whether this gump type has a Modern view.</summary>
    public static bool Has(Type classic) => classic != null && _factories.ContainsKey(classic);

    /// <summary>Whether a gump of this type opens as its Modern view now.</summary>
    public static bool Chosen(Type classic) =>
        TouchInput.Enabled && Has(classic) && !(ProfileManager.CurrentProfile?.ModernGumpsOff ?? false) && UoTheme.Ready;

    /// <summary>The next gump of this type opens Classic: a Modern view's "Classic view".</summary>
    public static void OpenClassicNext(Type classic) => _classicNext = classic;

    /// <summary>
    /// The UIManager.Add hook: when the gump has a Modern view and it is
    /// chosen, open that instead and say so; the caller does not add the
    /// classic one. False leaves the gump to open as it always does.
    /// </summary>
    public static bool TryOpenInstead(Gump gump)
    {
        if (gump == null || gump.IsFromServer)
        {
            return false;
        }

        Type type = gump.GetType();

        if (_classicNext == type)
        {
            _classicNext = null;
            return false;
        }

        if (!Chosen(type) || gump.World == null)
        {
            return false;
        }

        if (!_open.TryGetValue(type, out ModernGump view) || !Godot.GodotObject.IsInstanceValid(view))
        {
            view = _factories[type](gump.World);
            _open[type] = view;
            Client.Game.AddChild(view);
        }

        // A view may decline this particular gump (a Mastery book): Classic, then.
        if (!view.Accept(gump))
        {
            return false;
        }

        view.Open();
        TouchInput.Note($"modern: {type.Name} opened as its Modern view");

        return true;
    }
}
