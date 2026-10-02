// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Linq;

namespace GUO.Game.Scripting;

internal sealed record ScriptCommand(string Name, string Signature, string Description);
internal sealed record StarterScript(string Id, string Title, string Category, string Description, string Source);
internal sealed record ScriptCompletion(int Start, string Prefix, string[] Candidates);

/// <summary>Editor metadata and original GUO starter scripts. Never auto-executes a template.</summary>
internal static class ScriptCatalog
{
    public static readonly ScriptCommand[] Commands =
    {
        new("sysmsg", "sysmsg 'text' [hue]", "Print a message in your own journal."),
        new("say", "say 'text' [hue]", "Speak to nearby players and NPCs."),
        new("pause", "pause milliseconds", "Wait without blocking the client."),
        new("wait", "wait milliseconds", "Alias for pause."),
        new("cast", "cast 'spell name'", "Cast a Magery spell; wait for a target before targeting."),
        new("dclick", "dclick serial", "Use an object: self, backpack, decimal or hexadecimal serial."),
        new("waitfortarget", "waitfortarget [timeout_ms]", "Wait for the server's target cursor."),
        new("wft", "wft [timeout_ms]", "Alias for waitfortarget."),
        new("target", "target serial", "Answer the active target cursor."),
        new("stop", "stop", "Stop the current script."),
        new("if", "if expression", "Run a branch when its condition is true; close with endif."),
        new("elseif", "elseif expression", "Test another branch of the same if."),
        new("else", "else", "Run when no previous branch matched."),
        new("endif", "endif", "Close an if block."),
        new("while", "while expression", "Repeat while a condition is true."),
        new("endwhile", "endwhile", "Recheck the while condition."),
        new("for", "for count", "Repeat count times; index starts at zero."),
        new("foreach", "foreach 'variable' in 'list'", "Visit each value in a list."),
        new("endfor", "endfor", "Finish a for or foreach iteration."),
        new("break", "break", "Leave the innermost loop."),
        new("continue", "continue", "Advance to the next iteration."),
        new("replay", "replay", "Restart this script until stopped."),
        new("loop", "loop", "Alias for replay."),
        new("setvar", "setvar 'name' value", "Set a script variable."),
        new("setvariable", "setvariable 'name' value", "Alias for setvar."),
        new("unsetvar", "unsetvar 'name'", "Remove a script variable."),
        new("unsetvariable", "unsetvariable 'name'", "Alias for unsetvar."),
        new("createlist", "createlist 'name'", "Create an empty list if it does not exist."),
        new("clearlist", "clearlist 'name'", "Remove all values from a list."),
        new("removelist", "removelist 'name'", "Remove a list."),
        new("pushlist", "pushlist 'name' value", "Append a value to a list."),
        new("poplist", "poplist 'name' value|front|back", "Remove a matching value or an end value."),
        new("createtimer", "createtimer 'name'", "Create a timer if it does not exist."),
        new("settimer", "settimer 'name' elapsed_ms", "Set a timer's elapsed milliseconds."),
        new("removetimer", "removetimer 'name'", "Remove a timer.")
    };

    public static readonly string[] Keywords =
        { "if", "elseif", "else", "endif", "while", "endwhile", "for", "foreach", "endfor", "break", "continue", "replay", "and", "or", "not", "as" };

    public static readonly StarterScript[] Starters =
    {
        new("welcome", "Your first script", "Getting started", "Print two private messages with a pause between them.",
            "// Your first GUO script. Only you see these messages.\nsysmsg 'Hello from GUO'\npause 1000\nsysmsg 'Ready for adventure'\n"),
        new("open-backpack", "Open backpack", "Inventory", "Open your backpack using the normal client action.",
            "// Open your own backpack.\ndclick 'backpack'\n"),
        new("heal-self", "Heal yourself", "Magery", "Requires Heal, mana and reagents (or shard equivalents). Stops if the cast does not produce a target.",
            "// Cast Heal and wait up to five seconds for the server.\ncast 'heal'\nwaitfortarget 5000\ntarget 'self'\n"),
        new("cure-self", "Cure yourself", "Magery", "Requires Cure, mana and reagents. Uses the spell on yourself.",
            "// Cure yourself.\ncast 'cure'\nwaitfortarget 5000\ntarget 'self'\n"),
        new("greater-heal-self", "Greater heal", "Magery", "Requires Greater Heal, sufficient Magery, mana and reagents.",
            "// A stronger heal, when your character can cast it.\ncast 'greater heal'\nwaitfortarget 5000\ntarget 'self'\n"),
        new("vendor-buy", "Vendor buy", "Shopping", "Speak the buy command to a nearby vendor. Purchases remain manual.",
            "// Stand near a vendor, then run this script.\nsay 'vendor buy'\n"),
        new("vendor-sell", "Vendor sell", "Shopping", "Ask a nearby vendor to show the sell window. Sales remain manual.",
            "// Stand near a vendor, then run this script.\nsay 'vendor sell'\n"),
        new("reminder", "Timed reminder", "Getting started", "Wait ten seconds, then print a private reminder. Change the delay or message to suit you.",
            "// Delays are in milliseconds: 10000 means ten seconds.\npause 10000\nsysmsg 'Your reminder'\n")
    };

    public static ScriptCommand Describe(string line)
    {
        string command = line.TrimStart().Split(new[] { ' ', '\t' }, 2)[0].TrimStart('@');
        return Commands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
    }

    // Completion only considers the text before the caret. Never suggest inside comments.
    public static ScriptCompletion Complete(string line, int caret, IEnumerable<string> spells)
    {
        caret = Math.Clamp(caret, 0, line.Length);
        string before = line.Substring(0, caret);
        int start = 0;
        while (start < before.Length && char.IsWhiteSpace(before[start])) start++;
        if (before.AsSpan(start).StartsWith("#") || before.AsSpan(start).StartsWith("//"))
            return new(caret, "", Array.Empty<string>());
        int end = start;
        while (end < before.Length && !char.IsWhiteSpace(before[end])) end++;
        if (end == before.Length)
        {
            string prefix = before.Substring(start);
            return new(start, prefix, Commands.Select(c => c.Name).Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        string command = before.Substring(start, end - start).ToLowerInvariant();
        start = end;
        while (start < before.Length && char.IsWhiteSpace(before[start])) start++;
        char quote = start < before.Length && (before[start] == '\'' || before[start] == '"') ? before[start] : '\0';
        if (quote != '\0') start++;
        string argument = before.Substring(start);
        if ((quote != '\0' && argument.Contains(quote)) || (quote == '\0' && argument.Any(char.IsWhiteSpace)))
            return new(caret, "", Array.Empty<string>());
        IEnumerable<string> choices = command switch
        {
            "cast" => spells,
            "target" or "dclick" => new[] { "self", "backpack" },
            _ => Array.Empty<string>()
        };
        return new(start, argument, choices.Where(n => n.StartsWith(argument, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
