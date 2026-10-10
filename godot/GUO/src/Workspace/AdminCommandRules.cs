// GUO addition, not a port. AD3: which server commands the Admin tab's Commands palette runs on a click and which
// ask for a typed confirm first. Plain .NET (no Godot or ModernUO types): the editor bridge links this file and
// refuses a dangerous command that arrives without its confirm word; the editor asks for that word before it sends
// one; tools/editor_shard/bridge/tests compiles it as it stands.

using System;
using System.Collections.Generic;
using System.Linq;

namespace GUO.Workspace;

/// <summary>
/// The dangerous list (Moshu, 2026-10-09): shutting the server down, wiping the world, deleting accounts, decorating
/// or generating the whole world, and moving or deleting many things at once. Everything else runs on a click.
/// A dangerous command runs only with its confirm word typed: the command's own name (<c>wipe</c>), or for a
/// mass command its scope and command (<c>global delete</c>).
/// </summary>
internal static class AdminCommandRules
{
    /// <summary>ModernUO's command prefix (CommandSystem.Prefix).</summary>
    public const string Prefix = "[";

    /// <summary>The longest command line the bridge takes.</summary>
    public const int MaxLength = 512;

    /// <summary>One dangerous command: which kind of danger, the word that confirms it, and why, in plain words.</summary>
    public sealed record Danger(string Kind, string Confirm, string Why);

    // Stops the server. ModernUO's [Restart kills the process without a save and starts a new one that the run bar
    // does not know; Shutdown is the same on the backends that have it.
    private static readonly HashSet<string> Shutdown = new(StringComparer.OrdinalIgnoreCase) { "shutdown", "restart" };

    // Deletes everything in an area or on a facet.
    private static readonly HashSet<string> Wipe = new(StringComparer.OrdinalIgnoreCase)
    {
        "wipe", "wipeitems", "wipenpcs", "wipemultis", "clearall", "clearfacet", "clearxy",
    };

    // ModernUO deletes accounts only from its admin gump (and the tab leaves delete out, AD5); other backends name it so.
    private static readonly HashSet<string> DeleteAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "deleteaccount", "deleteaccounts", "delaccount", "accountdelete",
    };

    // Puts (or takes away) decoration, doors, signs, teleporters, moongates, champions and spawners all over the world.
    private static readonly HashSet<string> Decorate = new(StringComparer.OrdinalIgnoreCase)
    {
        "doorgen", "signgen", "telgen", "shtelgen", "telgendelete", "moongen", "secretlocgen", "genchamps", "gengauntlet",
        "remgauntlet", "genkhaldun", "genleverpuzzle", "genstealarties", "removestealarties", "generatefactions",
        "importspawners",
    };

    // ModernUO's command scopes that reach many objects at once (Commands/Generic/Implementors); Single, Serial,
    // Self and Multi act on what is targeted one at a time.
    private static readonly HashSet<string> MassScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "global", "facet", "region", "area", "group", "range", "screen", "online", "contained", "ipaddress",
    };

    // The generic commands that move, change or delete what they reach.
    private static readonly HashSet<string> MassMoves = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "remove", "rm", "kill", "set", "increase", "inc", "teleport", "tele", "bringtopack",
    };

    private static readonly string[] SecretWords = { "password", "token", "secret", "passphrase" };

    /// <summary>The words of a command line, without the prefix ("[wipe items" gives wipe, items).</summary>
    public static string[] Words(string text)
    {
        string t = (text ?? "").Trim();
        if (t.StartsWith(Prefix, StringComparison.Ordinal))
        {
            t = t[Prefix.Length..].TrimStart();
        }

        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The command's name (its first word), or "".</summary>
    public static string Name(string text) => Words(text) is { Length: > 0 } w ? w[0] : "";

    /// <summary>The line as the server takes it: trimmed, with the prefix in front.</summary>
    public static string Normalise(string text)
    {
        string t = (text ?? "").Trim();
        return t.Length == 0 || t.StartsWith(Prefix, StringComparison.Ordinal) ? t : Prefix + t;
    }

    /// <summary>Refusal text if the line cannot be sent as a command, or null.</summary>
    public static string Check(string text)
    {
        string t = (text ?? "").Trim();
        if (Words(t).Length == 0)
        {
            return "type a command, as in game: [where";
        }

        if (t.Length > MaxLength)
        {
            return $"a command line holds at most {MaxLength} characters";
        }

        return t.Any(c => c is '\r' or '\n' || char.IsControl(c) && c != '\t') ? "a command is one line" : null;
    }

    /// <summary>Why the line is on the dangerous list, or null when it runs on a click.</summary>
    public static Danger Classify(string text)
    {
        string[] w = Words(text);
        if (w.Length == 0)
        {
            return null;
        }

        string name = w[0].ToLowerInvariant();
        if (Shutdown.Contains(name))
        {
            return new Danger("shutdown", name, "It stops the server: everyone online is disconnected, and the world is not saved first.");
        }

        if (Wipe.Contains(name))
        {
            return new Danger("wipe", name, "It deletes everything it reaches, in an area or on a whole facet.");
        }

        if (DeleteAccounts.Contains(name))
        {
            return new Danger("delete accounts", name, "It deletes accounts and their characters for good.");
        }

        if (Decorate.Contains(name) || name.StartsWith("decorate", StringComparison.Ordinal))
        {
            return new Danger("global decorate", name, "It changes the whole world at once: decoration, doors, signs, teleporters or spawners everywhere.");
        }

        if (MassScopes.Contains(name) && w.Length > 1 && MassMoves.Contains(w[1]))
        {
            string confirm = name + " " + w[1].ToLowerInvariant();
            return new Danger("mass moves", confirm, $"It moves, changes or deletes everything '{name}' reaches, not one thing you point at.");
        }

        return null;
    }

    /// <summary>True when <paramref name="typed"/> is the danger's confirm word (case and spacing aside).</summary>
    public static bool Confirms(Danger danger, string typed) =>
        danger != null && string.Join(' ', (typed ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Equals(danger.Confirm, StringComparison.OrdinalIgnoreCase);

    /// <summary>ModernUO's access levels by value (Server.AccessLevel), lowest first.</summary>
    public static readonly string[] Levels = { "Player", "Counselor", "GameMaster", "Seer", "Administrator", "Developer", "Owner" };

    /// <summary>
    /// Refusal text if the online character <paramref name="name"/> (at <paramref name="level"/>) may not run the tab's
    /// commands for a connection holding <paramref name="held"/>, or null. A character runs them only when it is staff
    /// (Counselor or higher, as ModernUO's own commands) and not above the connection's own level.
    /// </summary>
    public static string CheckCharacter(string name, bool online, int level, int held)
    {
        if (!online)
        {
            return $"'{name}' is not online: log in with your staff character first, or leave 'run as' empty";
        }

        if (level < 1)
        {
            return $"'{name}' is a player: commands run as a staff character (Counselor or higher)";
        }

        return level > held
            ? $"'{name}' is {LevelName(level)}; this tab holds {LevelName(held)}, so it runs commands only as a character at or below that"
            : null;
    }

    private static string LevelName(int level) => level >= 0 && level < Levels.Length ? Levels[level] : level.ToString();

    /// <summary>
    /// The line as a log may hold it: a command whose name says password, token or secret ([password new new) keeps
    /// its name only.
    /// </summary>
    public static string ForLog(string text)
    {
        string name = Name(text);
        return SecretWords.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) ? $"{Prefix}{name} ***" : Normalise(text);
    }
}
