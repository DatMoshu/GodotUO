// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;

namespace GUO.Game.Scripting;

/// <summary>Restricts managed pack scripts at the host boundary. Game-thread only.
/// The owner must revoke the lease when disabling, updating or uninstalling a pack.</summary>
internal sealed class CapabilityScriptHost : IScriptHost
{
    private readonly IScriptHost _host;
    private readonly HashSet<string> _granted;
    private readonly Func<bool> _active;
    public const string RuntimeVersion = "0.1.0";

    public static bool Supported(string capability) => capability is
        "client.message" or "player.speech" or "player.inventory.use" or
        "player.target" or "player.spell.cast" or "world.read" or "journal.read";

    public CapabilityScriptHost(IScriptHost host, IEnumerable<string> granted, Func<bool> active)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _active = active ?? throw new ArgumentNullException(nameof(active));
        _granted = new HashSet<string>(granted ?? throw new ArgumentNullException(nameof(granted)), StringComparer.Ordinal);
        foreach (string capability in _granted)
            if (!Supported(capability)) throw new FormatException("Unsupported capability: " + capability);
    }

    private void Require(string capability)
    {
        if (!_active()) throw new FormatException("Script approval is no longer active.");
        if (!_granted.Contains(capability)) throw new FormatException("Required capability: " + capability);
    }

    private static string CommandCapability(string command) => command switch
    {
        "sysmsg" => "client.message", "say" => "player.speech",
        "dclick" => "player.inventory.use", "target" => "player.target",
        "cast" => "player.spell.cast",
        _ => throw new FormatException("No capability mapping for command: " + command)
    };

    private static string ExpressionCapability(string name) =>
        name is "insysmsg" or "insysmessage" ? "journal.read" : "world.read";

    public bool Connected => _active() && _host.Connected;
    public bool HasTarget { get { Require("player.target"); return _host.HasTarget; } }
    public void ValidateTargetWait() { Require("player.target"); _host.ValidateTargetWait(); }
    public bool HasExpression(string name) => _host.HasExpression(name);
    public void ValidateExpression(string name, IReadOnlyList<string> args)
    {
        Require(ExpressionCapability(name));
        _host.ValidateExpression(name, args);
    }
    public object Evaluate(string name, IReadOnlyList<string> args)
    {
        Require(ExpressionCapability(name));
        return _host.Evaluate(name, args);
    }
    public void Validate(string command, IReadOnlyList<string> args)
    {
        Require(CommandCapability(command));
        _host.Validate(command, args);
    }
    public Action Bind(string command, IReadOnlyList<string> args)
    {
        string capability = CommandCapability(command);
        Require(capability);
        Action action = _host.Bind(command, args);
        return () => { Require(capability); action(); };
    }
}
