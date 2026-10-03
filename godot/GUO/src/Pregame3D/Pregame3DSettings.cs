// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has only its 2D login gumps.

using System;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Configuration;

namespace GUO.Pregame3D;

/// <summary>
/// Whether the 3D pregame (docs/ui/pregame_3d.md) replaces the classic login
/// gumps. Decided once per run, in this order:
/// <list type="number">
/// <item><c>--pregame-3d</c> / <c>--pregame-classic</c> on the command line (this run only);</item>
/// <item><c>pregame3d.json</c> beside settings.json: <c>{"enabled": true|false}</c>;</item>
/// <item>the default: on in an exported Linux build (the Steam Deck build), off
/// everywhere else, so the desktop dev runs and every existing probe keep the
/// classic gumps.</item>
/// </list>
/// <c>--pregame3d-probe</c> turns it on and drives the whole login by synthetic
/// pad events (<see cref="Pregame3DProbe"/>).
/// </summary>
internal static class Pregame3DSettings
{
    private static bool? _enabled;
    private static bool? _probe;

    /// <summary>For probes and tests: force on or off; null follows the rules above.</summary>
    public static bool? Forced { get; set; }

    public static bool Probe => _probe ??= HasArg("--pregame3d-probe");

    public static bool Enabled
    {
        get
        {
            if (Forced is bool forced)
            {
                return forced;
            }

            return _enabled ??= Decide();
        }
    }

    public static string FilePath =>
        Path.Combine(Path.GetDirectoryName(Settings.GetSettingsFilepath()) ?? "", "pregame3d.json");

    private static bool Decide()
    {
        if (HasArg("--pregame3d-probe") || HasArg("--pregame-3d"))
        {
            GD.Print("[GUO] pregame3d: on (command line)");
            return true;
        }

        if (HasArg("--pregame-classic"))
        {
            GD.Print("[GUO] pregame3d: off (command line --pregame-classic)");
            return false;
        }

        try
        {
            string path = FilePath;

            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

                if (doc.RootElement.TryGetProperty("enabled", out JsonElement e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                {
                    bool on = e.GetBoolean();
                    GD.Print($"[GUO] pregame3d: {(on ? "on" : "off")} ({path})");
                    return on;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: {FilePath} unreadable, using the default: {ex.Message}");
        }

        bool deckBuild = OperatingSystem.IsLinux() && OS.HasFeature("template");
        GD.Print($"[GUO] pregame3d: {(deckBuild ? "on" : "off")} (default for this build; --pregame-3d / --pregame-classic or pregame3d.json to choose)");

        return deckBuild;
    }

    /// <summary>Remembers the choice in pregame3d.json (takes effect at the next login screen).</summary>
    public static void Save(bool enabled)
    {
        _enabled = enabled;

        try
        {
            File.WriteAllText(FilePath, $"{{\"enabled\": {(enabled ? "true" : "false")}}}\n");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: could not write {FilePath}: {ex.Message}");
        }
    }

    private static bool HasArg(string arg)
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a == arg)
            {
                return true;
            }
        }

        return false;
    }
}
