// The god view's actions (Admin tab, AD2b): which target each action takes,
// and when Follow has to move the admin's character. GodViewActions.cs runs
// them on the world; this file only decides.
//
// Plain .NET, no ModernUO types: tests/ compiles this file on its own.

using System;

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

    /// <summary>Refusal text if the named character may be moved by the god view, or null.</summary>
    public static string CheckCharacter(string name, bool online, AdminLevel level)
    {
        if (!online)
        {
            return $"'{name}' is not online: log in with your staff character first";
        }

        return level < CharacterLevel
            ? $"'{name}' is {level}; the god view moves only your own staff character ({CharacterLevel} or higher)"
            : null;
    }

    /// <summary>True when Follow must move the character to its target: another facet, or further than FollowRange.</summary>
    public static bool FollowMustMove(int map, int x, int y, int targetMap, int targetX, int targetY) =>
        map != targetMap || Math.Max(Math.Abs(x - targetX), Math.Abs(y - targetY)) > FollowRange;
}
