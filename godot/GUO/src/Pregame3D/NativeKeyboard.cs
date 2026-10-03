// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Diagnostics;
using System.IO;
using Godot;

namespace GUO.Pregame3D;

/// <summary>
/// The device's own on-screen keyboard, when there is one, and the facts
/// about the device that decide it (and the pregame's window):
/// <list type="bullet">
/// <item>Steam Deck: env <c>SteamDeck=1</c>, or the DMI table —
/// <c>/sys/class/dmi/id/board_name</c> or <c>product_name</c> "Jupiter" (LCD)
/// or "Galileo" (OLED), with <c>sys_vendor</c> "Valve" when only that much is known;</item>
/// <item>Game Mode (gamescope): env <c>SteamGamepadUI=1</c>, or
/// <c>XDG_CURRENT_DESKTOP=gamescope</c>, or <c>GAMESCOPE_WAYLAND_DISPLAY</c> set;</item>
/// <item>under Steam: either of the above, or env <c>SteamAppId</c> /
/// <c>SteamClientLaunch</c>, or a running <c>steam</c> process.</item>
/// </list>
/// Under Steam the keyboard is Steam's (<c>steam://open/keyboard</c>, typing
/// real key events into the focused window, closed with
/// <c>steam://close/keyboard</c>); on Android the OS keyboard
/// (DisplayServer.VirtualKeyboardShow). Anything else has none, and the
/// pregame's own <see cref="OnScreenKeyboard"/> grid is the fallback. A
/// scripted run (no focus, the probe) never opens a native keyboard: it would
/// pop up on whoever's desktop the run shares.
/// </summary>
internal static class NativeKeyboard
{
    private static bool? _deck, _gameMode, _steam;
    private static bool _told;

    public static bool IsSteamDeck => _deck ??= DetectDeck();

    public static bool IsGameMode => _gameMode ??= Env("SteamGamepadUI") == "1"
        || string.Equals(Env("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Env("GAMESCOPE_WAYLAND_DISPLAY"));

    public static bool UnderSteam => _steam ??= IsSteamDeck || IsGameMode
        || !string.IsNullOrEmpty(Env("SteamAppId")) || !string.IsNullOrEmpty(Env("SteamClientLaunch")) || SteamRunning();

    /// <summary>Which keyboard a field gets: "steam", "android" or "grid" (ours).</summary>
    public static string Kind
    {
        get
        {
            string kind;

            if (Dry)
            {
                kind = "steam";
            }
            else if (Pregame3DSettings.Probe || GUO.Host.Main.NoFocus || Env("GUO_NATIVE_KEYBOARD") == "0")
            {
                kind = "grid";
            }
            else if (OperatingSystem.IsAndroid())
            {
                kind = "android";
            }
            else if (OperatingSystem.IsLinux() && UnderSteam && SteamRunning())
            {
                kind = "steam";
            }
            else
            {
                kind = "grid";
            }

            if (!_told)
            {
                _told = true;
                GD.Print($"[GUO] pregame3d: keyboard = {kind} (Steam Deck {IsSteamDeck}, Game Mode {IsGameMode}, under Steam {UnderSteam}"
                    + $"{(kind == "grid" && (Pregame3DSettings.Probe || GUO.Host.Main.NoFocus) ? "; scripted run, native keyboard off" : "")})");
            }

            return kind;
        }
    }

    public static bool Available => Kind != "grid";

    /// <summary>GUO_NATIVE_KEYBOARD=dry: the Steam path taken, its URLs logged and not run (for a probe).</summary>
    private static bool Dry => Env("GUO_NATIVE_KEYBOARD") == "dry";

    public static void Show(string text)
    {
        switch (Kind)
        {
            case "steam":
                Steam("steam://open/keyboard");
                break;
            case "android":
                DisplayServer.VirtualKeyboardShow(text ?? "");
                break;
        }
    }

    public static void Hide()
    {
        switch (Kind)
        {
            case "steam":
                Steam("steam://close/keyboard");
                break;
            case "android":
                DisplayServer.VirtualKeyboardHide();
                break;
        }
    }

    /// <summary>
    /// Hands a steam:// URL to the running Steam client (<c>steam steam://...</c>), its
    /// output left alone. Never starts Steam: with no steam process nothing is run
    /// (Kind has already fallen back to the grid keyboard).
    /// </summary>
    private static void Steam(string url)
    {
        if (Dry)
        {
            GD.Print($"[GUO] pregame3d: (dry) steam {url}");
            return;
        }

        if (!SteamRunning())
        {
            GD.Print($"[GUO] pregame3d: no steam process, {url} not run");
            return;
        }

        try
        {
            using Process p = Process.Start(new ProcessStartInfo("steam", url)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (p != null)
            {
                GD.Print($"[GUO] pregame3d: steam {url}");
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[GUO] pregame3d: steam {url} failed: {ex.Message}");
        }
    }

    private static bool DetectDeck()
    {
        if (Env("SteamDeck") == "1")
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        string board = Dmi("board_name"), product = Dmi("product_name"), vendor = Dmi("sys_vendor");

        bool Named(string s) => s.Contains("Jupiter", StringComparison.OrdinalIgnoreCase) || s.Contains("Galileo", StringComparison.OrdinalIgnoreCase);

        return Named(board) || Named(product) && vendor.Contains("Valve", StringComparison.OrdinalIgnoreCase) || Named(product) && vendor.Length == 0;
    }

    private static string Dmi(string name)
    {
        try
        {
            return File.ReadAllText("/sys/class/dmi/id/" + name).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static double _steamCheckedAt = -100;
    private static bool _steamUp;

    /// <summary>A steam process is running (looked for at most every few seconds: Kind asks often).</summary>
    private static bool SteamRunning()
    {
        double now = System.Environment.TickCount64 / 1000.0;

        if (now - _steamCheckedAt < 5)
        {
            return _steamUp;
        }

        _steamCheckedAt = now;

        return _steamUp = ScanForSteam();
    }

    private static bool ScanForSteam()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                string comm = Path.Combine(dir, "comm");

                if (File.Exists(comm) && File.ReadAllText(comm).Trim() == "steam")
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static string Env(string name) => System.Environment.GetEnvironmentVariable(name) ?? "";
}
