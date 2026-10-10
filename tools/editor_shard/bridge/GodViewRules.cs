// The god view's actions (Admin tab, AD2b, AD2c): which target each action
// takes, who acts (the admin's own staff character or the Admin tab's hidden
// presence), and when Follow has to move. GodViewActions.cs runs them on the
// world; this file only decides.
//
// Plain .NET, no ModernUO types: tests/ compiles this file on its own.

using System;
using System.Collections.Generic;

namespace GUO.EditorBridge;

public static class GodViewRules
{
    /// <summary>The level the admin's own character must hold for the god view to move it (Server.AccessLevel.GameMaster).</summary>
    public const AdminLevel CharacterLevel = AdminLevel.GameMaster;

    /// <summary>Follow keeps the character within this many tiles of its target.</summary>
    public const int FollowRange = 2;

    /// <summary>How often Follow looks where its target went.</summary>
    public const int FollowIntervalMs = 500;

    /// <summary>The spawner actions: respawn (clear, then spawn its full count) and clear (remove what it spawned).</summary>
    public static readonly string[] SpawnerActions = { "respawn", "clear" };

    /// <summary>
    /// Refusal text if <paramref name="op"/> may not act on a target of this kind ("player", "npc", "spawner",
    /// "item", or null when there is no such serial), or null. <paramref name="self"/> is true when the target is
    /// the admin's own character.
    /// </summary>
    public static string CheckTarget(string op, string kind, bool self)
    {
        if (kind == null)
        {
            return "nothing in the world has that serial";
        }

        bool mobile = kind is "player" or "npc";
        return op switch
        {
            "admin_goto" => self ? "that is your own character" : null,
            "admin_bring" when self => "that is your own character",
            "admin_bring" => mobile ? null : "only a player or an NPC can be brought",
            "admin_paperdoll" => mobile ? null : "only a player or an NPC has a paperdoll",
            "admin_follow" when self => "that is your own character",
            "admin_follow" => mobile ? null : "only a player or an NPC can be followed",
            "admin_spawner" => kind == "spawner" ? null : "that is not a spawner",
            _ => $"'{op}' is not a god view action",
        };
    }

    /// <summary>
    /// Refusal text if the named character may be moved by the god view, or null: it must be online, staff
    /// (<see cref="CharacterLevel"/> or higher) and no higher than <paramref name="held"/>, the level the connection's
    /// token grants, so an Administrator's tab never moves an Owner.
    /// </summary>
    public static string CheckCharacter(string name, bool online, AdminLevel level, AdminLevel held)
    {
        if (!online)
        {
            return $"'{name}' is not online: log in with your staff character first";
        }

        if (level < CharacterLevel)
        {
            return $"'{name}' is {level}; the god view moves only your own staff character ({CharacterLevel} or higher)";
        }

        return level > held
            ? $"'{name}' is {level}, above this tab's {held}: the god view moves only a character at or below the tab's own level"
            : null;
    }

    /// <summary>Who acts for a god view action: an online staff character, or the Admin tab's hidden presence.</summary>
    public enum Actor
    {
        Refused,
        Character,
        Presence,
    }

    /// <summary>
    /// Picks who acts when the request names no character. <paramref name="hidden"/>: the request asked for the
    /// hidden presence. <paramref name="staff"/>: the online staff characters at or below the connection's level.
    /// One of them: that one; none: the hidden presence, so the actions work with nobody logged in; more: refused,
    /// naming them. <paramref name="named"/> is the request's "as" (null or empty when left out); naming one and
    /// asking for the presence together is refused.
    /// </summary>
    public static Actor Pick(string named, bool hidden, IReadOnlyList<string> staff, out string error)
    {
        error = null;
        if (!string.IsNullOrEmpty(named))
        {
            if (hidden)
            {
                error = "name a character in 'as' or ask for the hidden presence, not both";
                return Actor.Refused;
            }

            return Actor.Character;
        }

        if (hidden || staff.Count == 0)
        {
            return Actor.Presence;
        }

        if (staff.Count == 1)
        {
            return Actor.Character;
        }

        error = $"{staff.Count} staff characters are online; pick yours ({string.Join(", ", staff)}), or the Admin tab's hidden presence";
        return Actor.Refused;
    }

    /// <summary>
    /// Refusal text if the hidden presence may not do <paramref name="op"/> now, or null. Bring here needs a spot to
    /// bring to: the presence's own (<paramref name="placed"/>, set by Go there or Follow) or one in the request
    /// (<paramref name="spotGiven"/>).
    /// </summary>
    public static string CheckPresence(string op, bool placed, bool spotGiven) =>
        op == "admin_bring" && !placed && !spotGiven
            ? "the Admin tab's hidden presence has no spot yet: Go there first, or give a facet, x and y to bring it to"
            : null;

    /// <summary>True when Follow must move the character to its target: another facet, or further than FollowRange.</summary>
    public static bool FollowMustMove(int map, int x, int y, int targetMap, int targetX, int targetY) =>
        map != targetMap || Math.Max(Math.Abs(x - targetX), Math.Abs(y - targetY)) > FollowRange;
}
