namespace GUO.Host;

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

        /// <summary>Draw through the batcher, check the pixels, then quit.</summary>
        BatcherProbe,

        /// <summary>Decode a sample of art and show it. No client startup.</summary>
        ArtSample,
    }

    private Options _options;

    public override void _Ready()
    {
        _options = Options.Parse(OS.GetCmdlineUserArgs());

        // Before the client-data checks on purpose: the batcher probe draws
        // synthetic art and has nothing to do with a UO install, so it must
        // still run on a machine that has none.
        if (_options.Mode == RunMode.BatcherProbe)
        {
            BatcherProbe.Run(this);
            return;
        }

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
                if (LoadAndShow(draw: true))
                {
                    // The frame has to actually be rendered before it can be
                    // read back, so capture after the next draw rather than
                    // here.
                    CallDeferred(nameof(CaptureAndQuit));
                }
                else
                {
                    Quit(1);
                }

                break;

            case RunMode.Offline:
                // Both checks run even if the first fails, so one pass reports
                // everything that is wrong rather than only the first thing.
                bool dataOk = LoadAndShow(draw: false);
                bool resourcesOk = ResourceProbe.Verify();
                Quit(dataOk && resourcesOk ? 0 : 1);
                break;

            case RunMode.Play:
                StartClient();

                if (_options.ShardCommands.Count > 0)
                {
                    ShardCommandsThenQuit();
                }
                else if (_options.TradePartner)
                {
                    TradePartnerThenQuit();
                }
                else if (_options.InputProbe)
                {
                    // A fixed settle, not a fraction of the budget: the login
                    // screen is up well inside 200 frames, and scaling this
                    // with --shot-after meant asking for a later shot pushed
                    // the whole sequence back and captured less of it.
                    ProbeThenQuit();
                }
                else if (_options.ShotAfter > 0)
                {
                    CaptureAfterFrames(_options.ShotAfter);
                }

                break;

            case RunMode.ArtSample:
                // What Play used to do, kept because it is a cheap check that
                // the reader stack can open a real install and produce real
                // pixels, with none of the client's startup in the way.
                if (!LoadAndShow(draw: true))
                {
                    Quit(1);
                }

                break;
        }
    }

    /// <summary>
    /// Starts the ported client proper: upstream's <c>Main.Boot</c>, which
    /// reads settings, applies the command line, and ends by handing a
    /// GameController to the scene tree.
    /// </summary>
    /// <remarks>
    /// The arguments are rebuilt rather than passed through, because the two
    /// command lines are different shapes: the launchers speak
    /// <c>--client-data</c> and resolve it from config.bat, and upstream's
    /// parser speaks <c>-uopath</c> and expects a settings.json to have
    /// written it. Only the settings that have to agree are forwarded.
    ///
    /// The working directory moves first, and before anything in the client
    /// namespace is touched: CUOEnviroment.ExecutablePath is a static readonly
    /// initialised from Environment.CurrentDirectory, and it is where
    /// settings.json, the logs and the screenshots go. Left alone that is
    /// whatever directory the launcher happened to start Godot from.
    /// </remarks>
    private void StartClient()
    {
        string dataDir = GuoDataDirectory();

        System.IO.Directory.CreateDirectory(dataDir);
        System.Environment.CurrentDirectory = dataDir;

        GD.Print($"[GUO] client home   : {dataDir}");

        var args = new List<string>
        {
            "-uopath", _options.ClientData,
            "-clientversion", _options.ClientVersion,
            "-language", _options.Language,
            "-ip", _options.ShardHost,
            "-port", _options.ShardPort.ToString(),
        };

        Bootstrap.Boot(null, args.ToArray());
    }

    /// <summary>
    /// Where the client keeps settings.json, its logs and its profiles. Not a
    /// new setting: it is the parent of the configured cache directory, which
    /// every launcher already resolves the same way.
    /// </summary>
    private string GuoDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.CacheDir))
        {
            string parent = System.IO.Path.GetDirectoryName(
                _options.CacheDir.TrimEnd('/', '\\')
            );

            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return ProjectSettings.GlobalizePath("user://");
    }

    /// <summary>
    /// Loads the client data through the ported reader stack, and optionally
    /// puts a sample of decoded art on screen.
    /// </summary>
    private bool LoadAndShow(bool draw)
    {
        var probe = new UoDataProbe();
        AddChild(probe);

        if (!probe.LoadClientData(_options.ClientData, _options.ClientVersion, _options.Language))
        {
            return false;
        }

        if (draw)
        {
            int shown = probe.ShowSample();
            if (shown == 0)
            {
                // Archives opened but decoded nothing: a reader bug, not a
                // configuration problem, and worth failing loudly over.
                GD.PrintErr("[GUO] FATAL: client data loaded but no art decoded.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the rendered frame back and writes it to disk, then quits. Used
    /// by <c>launchers\dev\screenshot.bat</c> so visual claims can be backed
    /// by an artefact instead of an assertion.
    /// </summary>
    /// <summary>
    /// Runs the client for <paramref name="frames"/> frames, then captures and
    /// quits -- how a claim about what the real client puts on screen gets an
    /// artefact behind it, with nobody watching the window.
    /// </summary>
    /// <summary>
    /// Runs the input probe to the end, captures the last frame, and exits
    /// with the probe's verdict.
    /// </summary>
    /// <remarks>
    /// The exit code is the point. Before this, the probe printed what it saw
    /// and a person decided whether that was good; a run that quietly stopped
    /// at a loading screen looked the same as a run that played the game. It
    /// is a test now, and --shot-after is not needed with it: the run ends
    /// when the probe does.
    /// </remarks>
    /// <summary>
    /// Log in and type the commands the launcher passed, then quit. The dev
    /// shard takes its administration in game; see ShardCommands.
    /// </summary>
    private async void ShardCommandsThenQuit()
    {
        await ShardCommands.Run(this, _options.ShardCommands);
        Quit(ShardCommands.Passed ? 0 : 1);
    }

    /// <summary>
    /// Log in as somebody else and wait to be traded with, then quit. Started
    /// by the probe, which is the only thing that wants it; see TradePartner.
    /// </summary>
    private async void TradePartnerThenQuit()
    {
        await TradePartner.Run(this);
        Quit(0);
    }

    private async void ProbeThenQuit()
    {
        InputProbe.EndureSeconds = _options.EndureSeconds;

        await InputProbe.Run(this, 200);

        await CaptureFrame();

        Quit(InputProbe.Passed ? 0 : 1);
    }

    private async void CaptureAfterFrames(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        CaptureAndQuit();
    }

    private async void CaptureAndQuit()
    {
        Quit(await CaptureFrame() ? 0 : 1);
    }

    /// <returns>Whether the frame reached the disk.</returns>
    private async System.Threading.Tasks.Task<bool> CaptureFrame()
    {
        // One full frame must complete before the viewport holds anything.
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);

        Image frame = GetViewport().GetTexture().GetImage();

        string dir = string.IsNullOrWhiteSpace(_options.ScreenshotDir)
            ? "user://screenshots"
            : _options.ScreenshotDir;

        DirAccess.MakeDirRecursiveAbsolute(dir);
        string path = dir.PathJoin($"guo_{Time.GetUnixTimeFromSystem():F0}.png");

        Error err = frame.SavePng(path);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[GUO] FATAL: could not write screenshot to {path}: {err}");

            return false;
        }

        GD.Print($"[GUO] screenshot -> {ProjectSettings.GlobalizePath(path)}");

        return true;
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

        /// <summary>
        /// Frames to let run before capturing, then quit. Zero means never.
        /// Screenshot mode draws one fixed frame and can capture immediately;
        /// the client cannot, because its first frames are spent loading and
        /// building the login scene, and a shot taken then is a black window.
        /// </summary>
        public int ShotAfter { get; private set; }

        /// <summary>Drive the running client with synthesised input.</summary>
        public bool InputProbe { get; private set; }

        /// <summary>
        /// Seconds the probe keeps playing at the end of its run, to see
        /// whether a long session drifts. Zero means it does not.
        /// </summary>
        public int EndureSeconds { get; private set; }

        /// <summary>
        /// Log in as a second player and accept trades, rather than play.
        /// </summary>
        public bool TradePartner { get; private set; }

        /// <summary>
        /// Lines to type into the game window once the character is in the
        /// world, in order. Used to administer the local dev shard, which
        /// takes its commands in game.
        /// </summary>
        public List<string> ShardCommands { get; } = new();

        /// <summary>Dotted client version, e.g. "7.0.107.76".</summary>
        public string ClientVersion { get; private set; } = "7.0.107.76";

        /// <summary>Cliloc language suffix; "enu" for English.</summary>
        public string Language { get; private set; } = "enu";

        public static Options Parse(IEnumerable<string> args)
        {
            var o = new Options
            {
                ClientData = Env("UO_CLIENT_DATA", ""),
                CacheDir = Env("UO_CACHE_DIR", ""),
                ShardHost = Env("UO_SHARD_HOST", "127.0.0.1"),
                ClientVersion = Env("UO_CLIENT_VERSION", "7.0.107.76"),
                Language = Env("UO_LANGUAGE", "enu"),
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
                    case "--batcher-probe":
                        o.Mode = RunMode.BatcherProbe;
                        break;
                    case "--art-sample":
                        o.Mode = RunMode.ArtSample;
                        break;
                    case "--play":
                        o.Mode = RunMode.Play;
                        break;
                    case "--input-probe":
                        o.InputProbe = true;
                        break;
                    case "--trade-partner":
                        o.TradePartner = true;
                        break;
                    case "--endure":
                        if (int.TryParse(Next(), out int endure))
                        {
                            o.EndureSeconds = endure;
                        }

                        break;
                    case "--shard-command":
                        o.ShardCommands.Add(Next());
                        break;
                    case "--shot-after":
                        if (int.TryParse(Next(), out int frames))
                        {
                            o.ShotAfter = frames;
                        }

                        break;
                    case "--client-data":
                        o.ClientData = Next();
                        break;
                    case "--cache-dir":
                        o.CacheDir = Next();
                        break;
                    case "--client-version":
                        o.ClientVersion = Next();
                        break;
                    case "--language":
                        o.Language = Next();
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
