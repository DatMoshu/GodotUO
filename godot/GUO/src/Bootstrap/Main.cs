namespace GUO.Bootstrap;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Entry point for the ported client. Replaces ClassicUO.Bootstrap, whose job
/// (creating a window, a graphics device and a game loop) Godot now does.
/// </summary>
/// <remarks>
/// What is left here is the part Godot does not do: resolving where the UO
/// client data lives, parsing the flags the launchers pass, and starting the
/// right mode. Keep game logic out of this file — it is plumbing.
/// </remarks>
public partial class Main : Node
{
    /// <summary>Modes the launchers can ask for on the command line.</summary>
    public enum RunMode
    {
        /// <summary>Normal client startup.</summary>
        Play,

        /// <summary>Load and decode data, then quit. No shard connection.</summary>
        Offline,

        /// <summary>Populate the decode cache, then quit.</summary>
        WarmCache,

        /// <summary>Boot, capture one frame to disk, then quit.</summary>
        Screenshot,
    }

    private Options _options;

    public override void _Ready()
    {
        _options = Options.Parse(OS.GetCmdlineUserArgs());

        GD.Print($"[GUO] mode          : {_options.Mode}");
        GD.Print($"[GUO] client data   : {_options.ClientData}");
        GD.Print($"[GUO] cache         : {_options.CacheDir}");

        if (string.IsNullOrWhiteSpace(_options.ClientData))
        {
            Fail(
                "No UO client data directory configured.\n"
                + "Set UO_CLIENT_DATA in launchers\\_shared\\config.bat, or pass\n"
                + "  --client-data \"<path to your UO install>\""
            );
            return;
        }

        if (!DirAccess.DirExistsAbsolute(_options.ClientData))
        {
            Fail($"UO client data directory does not exist: {_options.ClientData}");
            return;
        }

        // Guard against pointing at a folder that is not actually a UO install.
        // tiledata.mul is present in every client version and has no UOP form,
        // which makes it the cheapest reliable probe.
        string probe = _options.ClientData.PathJoin("tiledata.mul");
        if (!FileAccess.FileExists(probe))
        {
            Fail(
                $"'{_options.ClientData}' does not look like a UO install "
                + "(no tiledata.mul).\n"
                + "Run launchers\\pipeline\\01_verify_client_data.bat to diagnose."
            );
            return;
        }

        GD.Print("[GUO] client data looks valid.");

        switch (_options.Mode)
        {
            case RunMode.WarmCache:
                GD.Print("[GUO] TODO: cache warm pass not implemented yet.");
                Quit(0);
                break;

            case RunMode.Screenshot:
                GD.Print("[GUO] TODO: screenshot pass not implemented yet.");
                Quit(0);
                break;

            case RunMode.Offline:
                GD.Print("[GUO] TODO: offline data load not implemented yet.");
                Quit(0);
                break;

            case RunMode.Play:
                GD.Print(
                    $"[GUO] TODO: shard connection to "
                    + $"{_options.ShardHost}:{_options.ShardPort} not implemented yet."
                );
                break;
        }
    }

    private void Fail(string message)
    {
        GD.PrintErr($"[GUO] FATAL: {message}");
        Quit(1);
    }

    private void Quit(int code)
    {
        GetTree().Quit(code);
    }

    /// <summary>
    /// Startup options, resolved from the command line with the environment
    /// as fallback.
    /// </summary>
    /// <remarks>
    /// The precedence deliberately mirrors <c>launchers\_shared\common.bat</c>:
    /// an explicit flag beats an environment variable, which beats nothing.
    /// Keeping the two in step means the game behaves the same whether it was
    /// started from a launcher, from the Godot editor, or from CI.
    /// </remarks>
    public sealed class Options
    {
        public RunMode Mode { get; private set; } = RunMode.Play;

        public string ClientData { get; private set; } = "";

        public string CacheDir { get; private set; } = "";

        public string ShardHost { get; private set; } = "127.0.0.1";

        public int ShardPort { get; private set; } = 2593;

        public string ScreenshotDir { get; private set; } = "";

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options
            {
                ClientData = Env("UO_CLIENT_DATA", ""),
                CacheDir = Env("UO_CACHE_DIR", ""),
                ShardHost = Env("UO_SHARD_HOST", "127.0.0.1"),
            };

            if (int.TryParse(Env("UO_SHARD_PORT", "2593"), out int envPort))
            {
                o.ShardPort = envPort;
            }

            var list = new List<string>(args);
            for (int i = 0; i < list.Count; i++)
            {
                string arg = list[i];
                string Next() => i + 1 < list.Count ? list[++i] : "";

                switch (arg)
                {
                    case "--offline":
                        o.Mode = RunMode.Offline;
                        break;
                    case "--warm-cache":
                        o.Mode = RunMode.WarmCache;
                        break;
                    case "--screenshot":
                        o.Mode = RunMode.Screenshot;
                        break;
                    case "--client-data":
                        o.ClientData = Next();
                        break;
                    case "--cache-dir":
                        o.CacheDir = Next();
                        break;
                    case "--screenshot-dir":
                        o.ScreenshotDir = Next();
                        break;
                    case "--host":
                        o.ShardHost = Next();
                        break;
                    case "--port":
                        if (int.TryParse(Next(), out int p))
                        {
                            o.ShardPort = p;
                        }

                        break;
                    default:
                        GD.Print($"[GUO] ignoring unknown argument: {arg}");
                        break;
                }
            }

            return o;
        }

        private static string Env(string name, string fallback)
        {
            string value = OS.GetEnvironment(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
