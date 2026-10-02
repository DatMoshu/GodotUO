// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Globalization;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Network;

namespace GUO.Game.Scripting;

/// <summary>PORT DEVIATION (GUO): scripts use the same game actions as the UI.</summary>
internal sealed class ClientScriptHost : IScriptHost
{
    private readonly World _world;
    public ClientScriptHost(World world) => _world = world;
    public bool Connected => _world.InGame && NetClient.Socket.IsConnected;
    public bool HasTarget => _world.TargetManager.IsTargeting &&
        (_world.TargetManager.TargetingState == CursorTarget.Object ||
         _world.TargetManager.TargetingState == CursorTarget.Position);

    public void Validate(string command, IReadOnlyList<string> args)
    {
        // Validate command shape without resolving character variables until execution.
        if (command is "sysmsg" or "say")
        {
            if (args.Count < 1 || args.Count > 2) throw new FormatException("Expected text and optional hue.");
        }
        else if (command is "dclick" or "target" or "cast") ScriptRunner.Require(args, 1);
        else throw new FormatException($"Unsupported command '{command}'.");
    }

    public bool HasExpression(string name) => name is "hp" or "hits" or "maxhp" or "maxhits" or "mana" or "maxmana" or
        "stam" or "maxstam" or "dead" or "poisoned" or "hidden" or "paralyzed" or "mounted" or "warmode" or
        "targetexists" or "str" or "dex" or "int" or "weight" or "maxweight" or "followers" or "maxfollowers" or
        "diffhp" or "diffhits" or "diffmana" or "diffstam" or "diffweight" or "name" or "insysmsg" or "insysmessage";

    public object Evaluate(string name, IReadOnlyList<string> args)
    {
        if (name is "insysmsg" or "insysmessage")
        {
            ScriptRunner.Require(args, 1);
            foreach (var entry in JournalManager.Entries)
                if (entry.Text.Contains(args[0], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        ScriptRunner.Require(args, 0);
        var player = _world.Player;
        return name switch
        {
            "hp" or "hits" => player.Hits, "maxhp" or "maxhits" => player.HitsMax,
            "mana" => player.Mana, "maxmana" => player.ManaMax, "stam" => player.Stamina, "maxstam" => player.StaminaMax,
            "dead" => player.IsDead, "poisoned" => player.IsPoisoned, "hidden" => player.IsHidden,
            "paralyzed" => player.IsParalyzed, "mounted" => player.IsMounted, "warmode" => player.InWarMode,
            "targetexists" => HasTarget, "str" => player.Strength, "dex" => player.Dexterity, "int" => player.Intelligence,
            "weight" => player.Weight, "maxweight" => player.WeightMax, "followers" => player.Followers, "maxfollowers" => player.FollowersMax,
            "diffhp" or "diffhits" => player.HitsMax - player.Hits, "diffmana" => player.ManaMax - player.Mana,
            "diffstam" => player.StaminaMax - player.Stamina, "diffweight" => player.WeightMax - player.Weight,
            "name" => player.Name, _ => throw new FormatException("Unsupported expression '" + name + "'.")
        };
    }

    public void ValidateExpression(string name, IReadOnlyList<string> args) =>
        ScriptRunner.Require(args, name is "insysmsg" or "insysmessage" ? 1 : 0);

    public Action Bind(string command, IReadOnlyList<string> args)
    {
        switch (command)
        {
            case "sysmsg":
            case "say":
                if (args.Count < 1 || args.Count > 2) throw new FormatException("Expected quoted text and optional hue.");
                string text = args[0];
                ushort hue = command == "sysmsg" ? (ushort)946 : (ushort)0xFFFF;
                if (args.Count == 2 && !ushort.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out hue))
                    throw new FormatException("Hue must be a decimal number from 0 to 65535.");
                return command == "sysmsg" ? () => GameActions.Print(_world, text, hue) : () => GameActions.Say(text, hue);
            case "dclick":
            case "target":
                ScriptRunner.Require(args, 1);
                string serial = args[0].ToLowerInvariant();
                if (serial != "self" && serial != "backpack") ParseSerial(serial);
                return () =>
                {
                    uint id = serial switch
                    {
                        "self" => _world.Player.Serial,
                        "backpack" => _world.Player.FindItemByLayer(Layer.Backpack)?.Serial ?? 0,
                        _ => ParseSerial(serial)
                    };
                    if (id == 0 || _world.Get(id) == null) throw new InvalidOperationException("Object is unavailable.");
                    if (command == "dclick") GameActions.DoubleClick(_world, id);
                    else
                    {
                        if (!HasTarget) throw new InvalidOperationException("No server target cursor is active.");
                        _world.TargetManager.Target(id);
                    }
                };
            case "cast":
                ScriptRunner.Require(args, 1);
                foreach (var spell in SpellsMagery.GetAllSpells)
                {
                    if (!string.Equals(spell.Value.Name, args[0], StringComparison.OrdinalIgnoreCase)) continue;
                    int id = spell.Key;
                    return () => GameActions.CastSpell(id);
                }
                throw new FormatException("Unknown Magery spell; only Magery is supported in this preview.");
            default:
                throw new FormatException($"Unsupported command '{command}'. This is a limited CE preview.");
        }
    }

    private static uint ParseSerial(string value)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? value.Substring(2) : value,
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out uint serial) || serial == 0 || serial == uint.MaxValue)
            throw new FormatException("Expected self, backpack, or a nonzero decimal/hex serial.");
        return serial;
    }
}
