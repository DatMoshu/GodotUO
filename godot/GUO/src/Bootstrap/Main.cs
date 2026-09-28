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

    /// <summary>
    /// Whether this run's window may never take keyboard focus. True for
    /// every scripted run unless <c>--focus</c> says otherwise; read by the
    /// client, which would otherwise treat a window that is never focused as
    /// inactive and throttle itself.
    /// </summary>
    public static bool NoFocus { get; private set; }

    /// <summary>
    /// The earliest point a script gets: the OS window already exists, and
    /// project.godot created it unfocusable (display/window/size/no_focus).
    /// An interactive run takes the flag off here and comes forward; a
    /// scripted run keeps it, and never activates itself later.
    /// </summary>
    /// <remarks>
    /// The flag is on at creation and not set here because the creation is
    /// what steals focus: Windows activates a new window when it is shown,
    /// which happens before any script runs. A flag set from here would be
    /// one frame late, and the owner's keystrokes would already be going to
    /// the wrong window. Deciding in the other direction costs an interactive
    /// run one frame before it has focus, which nobody can see.
    /// </remarks>
    public override void _EnterTree()
    {
        _options = Options.Parse(OS.GetCmdlineUserArgs());
        NoFocus = _options.NoFocus;

        if (NoFocus)
        {
            // Belt and braces: the project setting did this at creation, and
            // an export preset or a stray override.cfg could have lost it.
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.AlwaysOnTop, false);
            GD.Print("[GUO] window        : no focus (scripted run; --focus to opt out)");
        }
        else
        {
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, false);
            DisplayServer.WindowMoveToForeground();
            GD.Print("[GUO] window        : focusable (interactive run; --no-focus to opt out)");
        }
    }

    public override void _Ready()
    {
        // Parsed in _EnterTree; Godot calls that first, and the window flags
        // wanted deciding before anything else ran.
        _options ??= Options.Parse(OS.GetCmdlineUserArgs());

        if (!string.IsNullOrWhiteSpace(_options.Account))
        {
            InputProbe.ProbeAccount = _options.Account;
            InputProbe.ProbePassword = string.IsNullOrEmpty(_options.Password)
                ? _options.Account
                : _options.Password;
        }

        if (!string.IsNullOrWhiteSpace(_options.Character))
        {
            InputProbe.ProbeCharacter = _options.Character;
        }

        GameController.PinnedWindowPosition = _options.WindowPosition;
        GameController.PinnedWindowSize = _options.WindowSize;

        // Before the client-data checks on purpose: the batcher probe draws
        // synthetic art and has nothing to do with a UO install, so it must
        // still run on a machine that has none.
        if (_options.Mode == RunMode.BatcherProbe)
        {
            BatcherProbe.Run(this);
            return;
        }

        GD.Print($"[GUO] mode          : {_options.Mode}");
        GD.Print($"[GUO] cache         : {_options.CacheDir}");

        // ADR-0021: a custom data folder, then the UO install, else the
        // first-run wizard. Every mode below reads what this decides.
        DataSources.Result data = ResolveData();
        if (!data.Ok)
        {
            OnNoValidData(data);
            return;
        }

        RunWithData();
    }

    /// <summary>
    /// Everything that needs the client data, once ResolveData (or the
    /// first-run screen) has decided it.
    /// </summary>
    private void RunWithData()
    {
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

                if (_options.Silent)
                {
                    // A device build is exported with --silent by default:
                    // an APK a tool drives, or a person tries on a handheld,
                    // should not play the Britain theme over whatever is
                    // already on the speaker. Muting the Master bus, not the
                    // client's sound settings, so the profile the player
                    // saves still says "sound on" and an export with --sound
                    // hears it unchanged.
                    AudioServer.SetBusMute(AudioServer.GetBusIndex("Master"), true);
                    GD.Print("[GUO] audio muted (--silent)");
                }
                else if (Scripted && !_options.Sound)
                {
                    // Scripted runs are silent unless asked: four clients at
                    // once would otherwise play four Britain themes over a
                    // person's own audio. Muting the bus, not the client's
                    // sound settings, so the probe still hears a footstep
                    // start -- it reads what is playing, not the speaker.
                    AudioServer.SetBusMute(AudioServer.GetBusIndex("Master"), true);
                    GD.Print("[GUO] audio muted for a scripted run (--sound to hear it)");
                }

                // A phone has no mouse; a desktop asks for the layer by flag.
                // The mouse only stands in for a finger on the desktop: on a
                // device the fingers are real, and emulating more of them
                // from a pointer that is not there would be noise.
                if (_options.Touch || OS.HasFeature("mobile"))
                {
                    bool mobile = OS.HasFeature("mobile");

                    GUO.Input.Touch.TouchInput.Enable(this, emulateTouchFromMouse: !mobile);

                    // The controller applies it just before it loads the
                    // login scene, so the first layout already sees it.
                    GUO.Input.Touch.TouchInput.RequestedScale = _options.ScreenScale;
                    GUO.Input.Touch.TouchInput.TraceToLog = _options.TouchTrace;

                    // Debug: draw the fingers (--show-touches, or the Options toggle).
                    GUO.Input.Touch.TouchOverlay.Forced = _options.ShowTouches;
                    GUO.Input.Touch.TouchOverlay.Setup(this);

                    // The second screen as companion tabs (prototype; --companion-tabs or Options).
                    GUO.Input.Touch.CompanionTabs.Forced = _options.CompanionTabs;
                    GUO.Input.Touch.CompanionTabs.Setup(this);
                }

                // A second display, where the device has one (or the desktop
                // simulates one). Nothing is added to the tree otherwise.
                GUO.Platform.Android.DualScreen.Setup(this, _options.DualSimulate, _options.DualOff);

                // Commands and a probe together: the commands run first
                // (typically "[go" somewhere populated) and the probe follows.
                if (_options.LoginProbe)
                {
                    LoginProbeThenMaybeQuit();
                }
                else if (_options.UiProbe)
                {
                    UiProbeThenQuit();
                }
                else if (_options.TouchProbe)
                {
                    TouchProbeThenQuit();
                }
                else if (_options.MacroProbe)
                {
                    MacroProbeThenQuit();
                }
                else if (_options.UiGallery)
                {
                    UiGalleryThenQuit();
                }
                else if (_options.PresentationParity)
                {
                    PresentationParityThenQuit();
                }
                else if (_options.DualProbe)
                {
                    DualProbeThenMaybeQuit();
                }
                else if (_options.HighlightProbe)
                {
                    HighlightProbeThenQuit();
                }
                else if (_options.ZoomProbe)
                {
                    ZoomProbeThenQuit();
                }
                else if (_options.PortraitProbe)
                {
                    PortraitProbeThenQuit();
                }
                else if (_options.PerfProbe)
                {
                    PerfProbeThenQuit();
                }
                else if (!string.IsNullOrEmpty(_options.PostFxSheet))
                {
                    PostFxSheetThenQuit();
                }
                else if (_options.DoorProbe)
                {
                    DoorProbeThenQuit();
                }
                else if (_options.GamepadProbe)
                {
                    GamepadProbeThenQuit();
                }
                else if (_options.AssetProbe.Length > 0)
                {
                    AssetProbeThenQuit();
                }
                else if (_options.EffectsProbe > 0)
                {
                    EffectsProbeThenQuit();
                }
                else if (_options.ShardCommands.Count > 0 && _options.Stay)
                {
                    ShardCommandsThenStay();
                }
                else if (_options.ShardCommands.Count > 0)
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

        // --background mode[:path]: what the window shows behind the world
        // for this run only; the profile is neither read for it nor written.
        if (!string.IsNullOrWhiteSpace(_options.Background))
        {
            GUO.Renderer.CanvasBackground.Override = GUO.Renderer.CanvasBackgroundSettings.Parse(_options.Background);
            GD.Print($"[GUO] background     : {_options.Background}");
        }

        System.IO.Directory.CreateDirectory(dataDir);
        System.Environment.CurrentDirectory = dataDir;
        // The player's own looks and shaders (ADR-0023).
        GUO.Renderer.PostFx.PostFxLibrary.UserFolder = System.IO.Path.Combine(dataDir, "postfx");

        GD.Print($"[GUO] client home   : {dataDir}");

        var args = new List<string>
        {
            "-uopath", _options.ClientData,
            "-clientversion", _options.ClientVersion,
            "-language", _options.Language,
            "-ip", _options.ShardHost,
            "-port", _options.ShardPort.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(_options.FilesOverride))
        {
            args.AddRange(new[] { "-filesoverride", _options.FilesOverride });
            GD.Print($"[GUO] files override : {_options.FilesOverride}");
        }

        if (_options.AutoLogin && !string.IsNullOrWhiteSpace(_options.Account))
        {
            args.AddRange(new[]
            {
                "-username", _options.Account,
                "-password", string.IsNullOrEmpty(_options.Password) ? _options.Account : _options.Password,
                "-autologin", "true",
                "-skiploginscreen",
            });
            GD.Print($"[GUO] autologin      : {_options.Account}");
        }
        else if (_options.AutoLogin)
        {
            GD.Print("[GUO] autologin      : ignored, no --account");
        }

        if (!string.IsNullOrWhiteSpace(_options.StoreInstall))
        {
            StoreInstallNow(_options.StoreInstall);
        }

        string[] bootArgs = args.ToArray();

        // The boot splash covers the first frames, then crossfades into the
        // login screen; the client boots underneath it when its hold ends.
        if (_options.Splash ?? (!_options.Scripted && SplashIntro.Enabled))
        {
            SplashIntro.Play(this, () => Bootstrap.Boot(null, bootArgs), SplashIntro.ReducedMotion);
        }
        else
        {
            Bootstrap.Boot(null, bootArgs);
        }
    }

    private static void StoreInstallNow(string id)
    {
        try
        {
            using var client = GUO.Store.StoreOptions.CreateClient(System.Environment.GetEnvironmentVariable("UO_STORE_URL") ?? GUO.Store.StoreAddress.Default);
            var entry = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderByDescending(System.Linq.Enumerable.Where(client.FetchIndex().GetAwaiter().GetResult(), e => e.Manifest.Id == id), e => GUO.Store.StorePack.Version(e.Manifest.Version)));

            if (entry == null)
            {
                GD.Print($"[GUO] store install: {id} is not in the store's index");

                return;
            }

            string path = client.Install(entry).GetAwaiter().GetResult();
            GD.Print($"[GUO] store install: {id} {entry.Manifest.Version} ({entry.Manifest.Kind}) installed at {path}");
        }
        catch (Exception ex)
        {
            GD.Print($"[GUO] store install: {id} FAILED: {ex.Message}");
        }
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
    /// <summary>Play mode with something driving it, rather than a person.</summary>
    private bool Scripted =>
        _options.ShardCommands.Count > 0
        || _options.HighlightProbe
        || _options.ZoomProbe
        || _options.PortraitProbe
        || _options.PerfProbe
        || !string.IsNullOrEmpty(_options.PostFxSheet)
        || _options.DoorProbe
        || _options.GamepadProbe
        || _options.AssetProbe.Length > 0
        || _options.EffectsProbe > 0
        || _options.TradePartner
        || _options.InputProbe
        || _options.ShotAfter > 0;

    /// <summary>Type any --shard-command lines before a probe starts.</summary>
    private async System.Threading.Tasks.Task Preamble()
    {
        if (_options.ShardCommands.Count > 0)
        {
            await ShardCommands.Run(this, _options.ShardCommands);
        }
    }

    private async void ShardCommandsThenQuit()
    {
        await ShardCommands.Run(this, _options.ShardCommands);

        if (_options.ObjectsWatch.Length > 0)
        {
            await ObjectsDump.Watch(this, _options.ObjectsWatch);
        }

        if (_options.ObjectsDump.Length > 0)
        {
            ObjectsDump.Write(_options.ObjectsDump);
        }

        // A frame of wherever the commands left the character. "[go" somewhere
        // and photograph it is the only way to look at one particular piece of
        // the world twice -- before a renderer change and after it -- and be
        // sure the two pictures are of the same thing.
        await CaptureFrame();

        Quit(ShardCommands.Passed ? 0 : 1);
    }

    /// <summary>
    /// The commands, then nothing: a person takes over. How
    /// tools\side_by_side puts GUO next to ClassicUO in the same place.
    /// </summary>
    private async void ShardCommandsThenStay()
    {
        await ShardCommands.Run(this, _options.ShardCommands);
        GD.Print($"[GUO] shard commands {(ShardCommands.Passed ? "done" : "FAILED")}; staying (--stay)");
    }

    /// <summary>
    /// Time the world with and without a crowd of blended effects, then quit;
    /// see EffectsProbe.
    /// </summary>
    private async void EffectsProbeThenQuit()
    {
        await Preamble();
        EffectsProbe.Count = _options.EffectsProbe;
        EffectsProbe.Plain = _options.EffectsPlain;
        await EffectsProbe.Run(this);
        await CaptureFrame();
        Quit(EffectsProbe.Passed ? 0 : 1);
    }

    /// <summary>
    /// Hover a meshed tile or static, leave and come back, photograph it, then quit;
    /// see HighlightProbe.
    /// </summary>
    /// <summary>Frame time in the five fixed scenes, then quit; see PerfProbe.</summary>
    private async void PerfProbeThenQuit()
    {
        await PerfProbe.Run(this, _options.PerfOut, _options.PerfLabel, _options.PerfZoom, _options.PerfParity);
        Quit(PerfProbe.Passed ? 0 : 1);
    }

    /// <summary>Photograph and time every post-processing look, then quit; see PostFxProbe.</summary>
    private async void PostFxSheetThenQuit()
    {
        await Preamble();
        await PostFxProbe.Run(this, _options.PostFxSheet);
        Quit(PostFxProbe.Passed ? 0 : 1);
    }

    /// <summary>Turn to portrait and back, measure both, then quit; see PortraitProbe (C9).</summary>
    private async void PortraitProbeThenQuit()
    {
        await PortraitProbe.Run(this);
        Quit(PortraitProbe.Passed ? 0 : 1);
    }

    /// <summary>Time the world at every other zoom step, then quit; see ZoomProbe.</summary>
    private async void ZoomProbeThenQuit()
    {
        await Preamble();
        await ZoomProbe.Run(this);
        Quit(ZoomProbe.Passed ? 0 : 1);
    }

    /// <summary>Walk and confirm/cancel by injected joypad events; see GamepadProbe.</summary>
    private async void GamepadProbeThenQuit()
    {
        await Preamble();
        await GamepadProbe.Run(this);
        Quit(GamepadProbe.Passed ? 0 : 1);
    }

    /// <summary>Shut and open the doors on screen, keeping every frame after; see DoorProbe.</summary>
    private async void DoorProbeThenQuit()
    {
        await Preamble();
        await DoorProbe.Run(this, _options.ScreenshotDir);
        Quit(DoorProbe.Passed ? 0 : 1);
    }

    /// <summary>Decode chosen assets through the client's loaders and quit; see AssetProbe.</summary>
    private async void AssetProbeThenQuit()
    {
        await AssetProbe.Run(this, _options.AssetProbe, _options.AssetProbeIds);
        Quit(AssetProbe.Passed ? 0 : 1);
    }

    private async void HighlightProbeThenQuit()
    {
        await Preamble();
        await HighlightProbe.Run(this);
        await CaptureFrame();
        Quit(HighlightProbe.Passed ? 0 : 1);
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

    /// <summary>
    /// Log in, wait for the second screen to come up, report what it did
    /// and what it cost, photograph both screens; see DualProbe. Quits on a
    /// desktop, stays up on a device so the tooling can photograph the panels.
    /// </summary>
    private async void DualProbeThenMaybeQuit()
    {
        await DualProbe.Run(this);

        // --dual-probe-held: the photographs are taken with an item held from
        // the shelved backpack and the pointer on the second screen.
        bool held = _options.DualProbeHeld && DualProbe.Passed && await DualProbe.HoldOnShelf(this);

        SaveSecondFrame();
        await CaptureFrame();

        if (held)
        {
            await DualProbe.DropBack(this);
        }

        if (!OS.HasFeature("mobile"))
        {
            Quit(DualProbe.Passed ? 0 : 1);
        }
    }

    /// <summary>
    /// What the second screen last showed, beside the main screenshot as
    /// <c>&lt;name&gt;_second.png</c>; nothing without a second screen.
    /// </summary>
    private void SaveSecondFrame()
    {
        if (!GUO.Platform.Android.DualScreen.HasSecondaryDisplay)
        {
            return;
        }

        string dir = string.IsNullOrWhiteSpace(_options.ScreenshotDir)
            ? "user://screenshots"
            : _options.ScreenshotDir;

        DirAccess.MakeDirRecursiveAbsolute(dir);

        string second = dir.PathJoin(
            string.IsNullOrWhiteSpace(_options.ScreenshotName)
                ? "guo_second.png"
                : $"{_options.ScreenshotName}_second.png"
        );

        if (GUO.Platform.Android.DualScreen.SaveFrame(second))
        {
            GD.Print($"[GUO] screenshot -> {ProjectSettings.GlobalizePath(second)}");
        }
    }

    /// Say on the log when the login gump has been drawn; see LoginProbe.
    /// </summary>
    private async void LoginProbeThenMaybeQuit()
    {
        await LoginProbe.Run(this);

        if (_options.LoginProbeQuits)
        {
            // The second screen (dual from launch, ADR-0009) shows the
            // welcome panel at the login screen; a few frames so it has
            // been pushed at least once.
            if (GUO.Platform.Android.DualScreen.HasSecondaryDisplay)
            {
                await InputProbe.Wait(this, 12);
            }

            SaveSecondFrame();
            await CaptureFrame();
            Quit(LoginProbe.Passed ? 0 : 1);
        }
    }

    /// <summary>
    /// Get into the world, open the backpack, log the profile's platform
    /// defaults, photograph it and quit; see UiProbe. On a device something
    /// else logs in, and the frame is taken with adb, so it stays up.
    /// </summary>
    private async void UiProbeThenQuit()
    {
        bool device = OS.HasFeature("mobile");

        await UiProbe.Run(this, logInHere: !device);

        if (!device)
        {
            await CaptureFrame();
            Quit(UiProbe.Passed ? 0 : 1);
        }
    }

    /// <summary>
    /// Drive the touch layer with synthetic fingers, photograph the result,
    /// and exit with the verdict; see TouchProbe.
    /// </summary>
    private async void TouchProbeThenQuit()
    {
        await TouchProbe.Run(this);
        await CaptureFrame();
        Quit(TouchProbe.Passed ? 0 : 1);
    }

    /// <summary>Check gump presentation is absent on a default desktop, and exit with the verdict.</summary>
    private async void PresentationParityThenQuit()
    {
        await PresentationParityProbe.Run(this);
        Quit(PresentationParityProbe.Passed ? 0 : 1);
    }

    /// <summary>Picture each of GUO's own mobile UIs and exit; see GalleryProbe.</summary>
    private async void UiGalleryThenQuit()
    {
        await GalleryProbe.Run(this, _options.ScreenshotDir, _options.ScreenshotName);
        Quit(GalleryProbe.Passed ? 0 : 1);
    }

    /// <summary>Tap the six macros against fixtures and exit with the verdict; see MacroProbe.</summary>
    private async void MacroProbeThenQuit()
    {
        await MacroProbe.Run(this);
        await CaptureFrame();
        Quit(MacroProbe.Passed ? 0 : 1);
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

        // A name when one is asked for, a timestamp otherwise. The sweep asks:
        // two sweeps are only comparable if the same place lands on the same
        // filename both times, and a timestamp makes every run a new set of
        // pictures with nothing to hold them against.
        string path = dir.PathJoin(
            string.IsNullOrWhiteSpace(_options.ScreenshotName)
                ? $"guo_{Time.GetUnixTimeFromSystem():F0}.png"
                : $"{_options.ScreenshotName}.png"
        );

        Error err = frame.SavePng(path);
        if (err != Error.Ok)
        {
            GD.PrintErr($"[GUO] FATAL: could not write screenshot to {path}: {err}");

            return false;
        }

        GD.Print($"[GUO] screenshot -> {ProjectSettings.GlobalizePath(path)}");

        return true;
    }

    /// <summary>
    /// Resolves the client data by ADR-0021 (see DataSources) and applies it:
    /// the install becomes ClientData, and a layered custom folder becomes the
    /// files override unless --files-override was given.
    /// </summary>
    private DataSources.Result ResolveData()
    {
        string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var inputs = new DataSources.Inputs
        {
            CustomFlag = _options.CustomData,
            CustomEnv = System.Environment.GetEnvironmentVariable("UO_CUSTOM_DATA") ?? "",
            ShippedFolder = exeDir.Length > 0 ? System.IO.Path.Combine(exeDir, "guo_data") : "",
            InstallConfigured = _options.ClientData,
            InstallConfiguredOrigin = _options.ClientDataFromFlag ? "flag" : "environment",
            SettingsFile = System.IO.Path.Combine(GuoDataDirectory(), Configuration.Settings.SETTINGS_FILENAME),
            Defaults = DataSources.PlatformDefaults(),
        };

        DataSources.Result r = DataSources.Resolve(inputs);
        foreach (string note in r.Notes)
        {
            GD.Print($"[GUO] data passed over: {note}");
        }

        if (!r.Ok)
        {
            return r;
        }

        string filesOverride = _options.FilesOverride;
        if (r.Overrides.Count > 0 && string.IsNullOrWhiteSpace(filesOverride))
        {
            filesOverride = DataSources.WriteOverride(r, System.IO.Path.Combine(GuoDataDirectory(), "guo_data_override.txt"));
        }

        _options.UseData(r.ClientData, filesOverride);
        GD.Print($"[GUO] data source   : {r.Source} ({r.Origin})" + (r.Custom != null ? $", custom {r.Custom}" : "")
                 + (r.LocalOnly ? ", EA-derived: local only" : ""));
        GD.Print($"[GUO] client data   : {_options.ClientData}");
        return r;
    }

    /// <summary>
    /// The one place the client lands when there is no valid data (ADR-0021):
    /// the first-run screen, which asks for the UO install, saves it as
    /// settings.json ultimaonlinedirectory and continues to login in this run.
    /// A run with no window (headless) or a mode other than play cannot ask,
    /// so it logs the reason and exits as before.
    /// </summary>
    private void OnNoValidData(DataSources.Result data)
    {
        GD.Print($"[GUO] data source   : wizard needed: {data.Reason}");
        if (_options.Mode == RunMode.Play && DisplayServer.GetName() != "headless")
        {
            string configured = string.IsNullOrWhiteSpace(_options.ClientData)
                ? null
                : $"{(_options.ClientDataFromFlag ? "--client-data" : "UO_CLIENT_DATA")} = {_options.ClientData}";
            FirstRunScreen.Open(this, data, configured, _options.FirstRunProbe, _options.ScreenshotDir, OnInstallChosen);
            return;
        }

        Fail(
            "No valid UO client data: the first-run wizard is needed.\n"
            + $"  {data.Reason}\n"
            + "Set UO_CLIENT_DATA in launchers\\_shared\\config.local.bat, or pass\n"
            + "  --client-data \"<path to your UO install>\""
        );
    }

    /// <summary>The first-run screen's Continue: save the choice and go on to login.</summary>
    private void OnInstallChosen(string folder)
    {
        string settings = System.IO.Path.Combine(GuoDataDirectory(), Configuration.Settings.SETTINGS_FILENAME);
        DataSources.SaveSetting(settings, folder);
        GD.Print($"[GUO] data source   : install (chosen) {folder}; saved to {settings}");
        _options.UseData(folder, _options.FilesOverride);
        RunWithData();
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

        /// <summary>True when ClientData came from --client-data rather than UO_CLIENT_DATA.</summary>
        public bool ClientDataFromFlag { get; private set; }

        /// <summary>A custom data folder (ADR-0021): --custom-data PATH.</summary>
        public string CustomData { get; private set; } = "";

        /// <summary>
        /// Scripted first run: --first-run-probe FOLDER shows the first-run
        /// screen, photographs it, "picks" FOLDER without a dialog, photographs
        /// the check, and presses Continue.
        /// </summary>
        public string FirstRunProbe { get; private set; } = "";

        /// <summary>What ResolveData decided: the install to open, and the files override.</summary>
        internal void UseData(string clientData, string filesOverride)
        {
            ClientData = clientData;
            FilesOverride = filesOverride;
        }

        public string CacheDir { get; private set; } = "";

        public string ShardHost { get; private set; } = "127.0.0.1";

        public int ShardPort { get; private set; } = 2593;

        public string ScreenshotDir { get; private set; } = "";
        public string ScreenshotName { get; private set; } = "";

        /// <summary>
        /// Frames to let run before capturing, then quit. Zero means never.
        /// Screenshot mode draws one fixed frame and can capture immediately;
        /// the client cannot, because its first frames are spent loading and
        /// building the login scene, and a shot taken then is a black window.
        /// </summary>
        public int ShotAfter { get; private set; }

        /// <summary>Drive the running client with synthesised input.</summary>
        public bool InputProbe { get; private set; }

        /// <summary>Let a scripted run be heard. Off by default; a person playing is never muted.</summary>
        public bool Sound { get; private set; }

        /// <summary>
        /// Whether something other than a person is driving this run: any
        /// probe, a shard-command run, a timed screenshot, an endurance run,
        /// or a mode that is not Play at all. Such a run shares the desktop
        /// with whoever started it and must not take their keyboard.
        /// --stay hands the client to a person, so it is not scripted.
        /// </summary>
        public bool Scripted =>
            !Stay
            && (Mode != RunMode.Play
                || InputProbe
                || TradePartner
                || HighlightProbe
                || ZoomProbe
                || PortraitProbe
                || PerfProbe
                || !string.IsNullOrEmpty(PostFxSheet)
                || DoorProbe
                || GamepadProbe
                || EffectsProbe > 0
                || EndureSeconds > 0
                || TouchProbe
                || MacroProbe
                || UiGallery
                || PresentationParity
                || LoginProbe
                || UiProbe
                || DualProbe
                || ShardCommands.Count > 0
                || ShotAfter > 0);

        /// <summary>
        /// Whether the window is kept from ever taking focus. <c>--no-focus</c>
        /// and <c>--focus</c> decide it outright; with neither, a scripted run
        /// is unfocusable and an interactive one is not. Mirrors how --sound
        /// settles the audio.
        /// </summary>
        public bool NoFocus => _noFocus ?? Scripted;

        private bool? _noFocus;

        /// <summary>
        /// Account, password and character for every scripted mode; empty
        /// means the probe's own. The password defaults to the account name,
        /// which is what auto account creation on the dev shard makes of it.
        /// </summary>
        public string Account { get; private set; } = "";
        public string Password { get; private set; } = "";
        public string Character { get; private set; } = "";

        /// <summary>
        /// Log straight in as <see cref="Account"/>: login, first server and
        /// last (or first) character with no input, through upstream's own
        /// -username/-password/-autologin/-skiploginscreen
        /// switches. For device and scripted
        /// runs, where typing through a soft keyboard is the slow part. Like
        /// upstream, the login scene saves settings.json, so autologin stays
        /// on in that profile until it is unticked.
        /// </summary>
        public bool AutoLogin { get; private set; }

        /// <summary>
        /// Where to put the window, overriding the saved position and never
        /// saved back. Null leaves the client to place itself. This is what
        /// lets several scripted clients tile a screen instead of stacking.
        /// </summary>
        public Vector2I? WindowPosition { get; private set; }

        /// <summary>
        /// The window's size, overriding the saved size and the saved
        /// maximised state, and never saved back. Four clients share one
        /// settings file, and each one writes its window state on exit, so
        /// without this a lane comes up at whatever size the last lane to
        /// quit had.
        /// </summary>
        public Vector2I? WindowSize { get; private set; }

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
        /// Type the --shard-command lines and then hand the client to a person,
        /// rather than photograph the result and quit.
        /// </summary>
        public bool Stay { get; private set; }

        /// <summary>Run the mesh highlight check rather than play.</summary>
        public bool HighlightProbe { get; private set; }

        /// <summary>Time the world at each zoom level rather than play.</summary>
        public bool ZoomProbe { get; private set; }
        public bool PortraitProbe { get; private set; }

        /// <summary>Frame time in five fixed scenes rather than play (PerfProbe): --perf-probe [--perf-out DIR] [--perf-label NAME].</summary>
        public bool PerfProbe { get; private set; }

        public string PerfOut { get; private set; } = "";

        public string PerfLabel { get; private set; } = "";

        public float PerfZoom { get; private set; }
        /// <summary>Photograph and time every post-processing look into this folder (ADR-0023).</summary>
        public string PostFxSheet { get; private set; }

        /// <summary>--perf-parity: per scene, compare --batched-world with the plain path pixel for pixel.</summary>
        public bool PerfParity { get; private set; }

        public bool DoorProbe { get; private set; }

        /// <summary>File the world objects near the player are written to after the shard commands; see ObjectsDump.</summary>
        public string ObjectsDump { get; private set; } = "";

        /// <summary>Folder the client watches for dump requests after the shard commands; see ObjectsDump.Watch.</summary>
        public string ObjectsWatch { get; private set; } = "";

        /// <summary>Check the gamepad layer by injected joypad events (--gamepad-probe).</summary>
        public bool GamepadProbe { get; private set; }

        /// <summary>Folder the asset probe writes to; empty means no probe (see AssetProbe).</summary>
        public string AssetProbe { get; private set; } = "";

        /// <summary>What the asset probe decodes, e.g. land:0x0244,static:0x0E75,gump:0x0064,hue:33.</summary>
        public string AssetProbeIds { get; private set; } = "";

        /// <summary>How many effects the effects probe spawns; zero means no probe.</summary>
        public int EffectsProbe { get; private set; }

        /// <summary>The effects probe draws its effects with no blend.</summary>
        public bool EffectsPlain { get; private set; }

        /// <summary>
        /// Put the touch layer in front of the mouse path. On a phone it is
        /// on regardless; on a desktop this is how it is tried out and tested.
        /// </summary>
        public bool Touch { get; private set; }

        /// <summary>Drive the touch layer with synthetic fingers and check the client reacted.</summary>
        public bool TouchProbe { get; private set; }

        /// <summary>Tap each of the touch bar's six macros against spawned fixtures; see MacroProbe.</summary>
        public bool MacroProbe { get; private set; }

        /// <summary>Picture each of GUO's own mobile UIs; see GalleryProbe.</summary>
        public bool UiGallery { get; private set; }

        /// <summary>Desktop defaults leave gump drawing and hit testing as ClassicUO's; see PresentationParityProbe.</summary>
        public bool PresentationParity { get; private set; }

        /// <summary>Echo every gesture the touch layer resolves to the log, for a device run read over logcat.</summary>
        public bool TouchTrace { get; private set; }

        /// <summary>Draw every finger the touch layer sees; see TouchOverlay.</summary>
        public bool ShowTouches { get; private set; }

        /// <summary>The second screen as companion tabs; see CompanionTabs.</summary>
        public bool CompanionTabs { get; private set; }

        /// <summary>Mute the Master bus for the whole run; the Android tool bakes this in unless told --sound.</summary>
        public bool Silent { get; private set; }

        /// <summary>
        /// Whether to play the boot splash (SplashIntro): <c>--splash</c> and
        /// <c>--no-splash</c> decide it outright; with neither, an interactive
        /// run follows the player's Options choice and a scripted run skips it.
        /// </summary>
        public bool? Splash { get; private set; }

        /// <summary>
        /// Wait for the login gump to be drawn, say so on the log, and either
        /// quit (desktop) or keep running (a device, where the line is what
        /// the smoke reads back through logcat).
        /// </summary>
        public bool LoginProbe { get; private set; }
        public bool UiProbe { get; private set; }

        /// <summary>Whether the login probe quits once it has reported. Default true.</summary>
        public bool LoginProbeQuits { get; private set; } = true;

        /// <summary>Log in, use the second screen, report and photograph it; see DualProbe.</summary>
        public bool DualProbe { get; private set; }

        /// <summary>The dual probe's photographs with an item held; see DualProbe.HoldOnShelf.</summary>
        public bool DualProbeHeld { get; private set; }

        /// <summary>"WxH": stand a desktop window in for a second display of that size.</summary>
        public string DualSimulate { get; private set; } = "";

        /// <summary>Leave a second display alone this run.</summary>
        public bool DualOff { get; private set; }

        /// <summary>
        /// Integer screen scale for the touch layer; zero picks one from the
        /// window height so the 640x480 login screen fits.
        /// </summary>
        public int ScreenScale { get; private set; }

        /// <summary>
        /// Lines to type into the game window once the character is in the
        /// world, in order. Used to administer the local dev shard, which
        /// takes its commands in game.
        /// </summary>
        public List<string> ShardCommands { get; } = new();

        /// <summary>
        /// The canvas background for this run, <c>mode[:path]</c>, applied
        /// over whatever the profile says and never saved. Null when the
        /// profile decides.
        /// </summary>
        public string Background { get; private set; }

        /// <summary>
        /// A store pack id to install at startup through the in-client
        /// installer (the configured UO_STORE_URL, this build's profile
        /// version), for scripted and device runs: --store-install ID.
        /// </summary>
        public string StoreInstall { get; private set; }

        /// <summary>
        /// Upstream's files_override for this run: a file of name=path lines
        /// that replace single client files, e.g. a staged data set
        /// (ADR-0022) on a device, where settings.json cannot be reached:
        /// --files-override PATH.
        /// </summary>
        public string FilesOverride { get; private set; }

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
                    case "--stay":
                        o.Stay = true;
                        break;
                    case "--highlight-probe":
                        o.HighlightProbe = true;
                        break;
                    case "--zoom-probe":
                        o.ZoomProbe = true;
                        break;
                    case "--portrait-probe":
                        o.PortraitProbe = true;
                        break;
                    case "--merged-land=array":
                        GUO.Renderer.MergedLand.Array = true;
                        goto case "--merged-land=ordered";
                    case "--merged-land=ordered":
                        GUO.Renderer.MergedLand.Ordered = true;
                        goto case "--merged-land";
                    case "--merged-land":
                        GUO.Renderer.MergedLand.Enabled = true;
                        GUO.Host.PerfProbe.ParityToggle = on => GUO.Renderer.MergedLand.Enabled = on;
                        GUO.Host.PerfProbe.ParityState = () => GUO.Renderer.MergedLand.Enabled;
                        break;
                    case "--cover-cull":
                        // Epic B, B4 fix 2b: covering land only where it overlaps its object.
                        GUO.Game.Scenes.RenderLists.CoverCull = true;
                        GUO.Host.PerfProbe.ParityToggle = on => GUO.Game.Scenes.RenderLists.CoverCull = on;
                        GUO.Host.PerfProbe.ParityState = () => GUO.Game.Scenes.RenderLists.CoverCull;
                        break;
                    case "--gamepad-probe":
                        o.GamepadProbe = true;
                        break;
                    case "--merged-cover":
                        // Epic B, B4 fix 2c: each run of covering land one mesh over the land array.
                        // The parity toggle is this flag alone; give --merged-land=array before it.
                        GUO.Renderer.UltimaBatcher2D.MergedCover = true;
                        GUO.Host.PerfProbe.ParityToggle = on => GUO.Renderer.UltimaBatcher2D.MergedCover = on;
                        GUO.Host.PerfProbe.ParityState = () => GUO.Renderer.UltimaBatcher2D.MergedCover;
                        break;
                    case "--batched-world":
                        GUO.Renderer.UltimaBatcher2D.BatchedWorld = true;
                        break;
                    case "--perf-probe":
                        o.PerfProbe = true;
                        break;
                    case "--perf-out":
                        o.PerfOut = Next();
                        break;
                    case "--perf-label":
                        o.PerfLabel = Next();
                        break;
                    case "--perf-parity":
                        o.PerfParity = true;
                        break;
                    case "--perf-zoom":
                        o.PerfZoom = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--postfx-sheet":
                        o.PostFxSheet = Next();
                        break;
                    case "--door-probe":
                        o.DoorProbe = true;
                        break;
                    case "--objects-watch":
                        o.ObjectsWatch = Next();
                        break;
                    case "--objects-dump":
                        o.ObjectsDump = Next();
                        break;
                    case "--asset-probe":
                        o.AssetProbe = Next();
                        break;
                    case "--asset-probe-ids":
                        o.AssetProbeIds = Next();
                        break;
                    case "--effects-plain":
                        o.EffectsPlain = true;
                        break;
                    case "--touch":
                        o.Touch = true;
                        break;
                    case "--touch-probe":
                        o.Touch = true;
                        o.TouchProbe = true;
                        break;
                    case "--presentation-parity":
                        o.PresentationParity = true;
                        break;
                    case "--macro-probe":
                        o.Touch = true;
                        o.MacroProbe = true;
                        break;
                    case "--ui-gallery":
                        o.Touch = true;
                        o.UiGallery = true;
                        break;
                    case "--touch-trace":
                        o.TouchTrace = true;
                        break;
                    case "--show-touches":
                        o.ShowTouches = true;
                        break;
                    case "--companion-tabs":
                        o.CompanionTabs = true;
                        break;
                    case "--gamepad-trace":
                        GUO.Input.Gamepad.GamepadInput.Trace = true;
                        break;
                    case "--silent":
                        o.Silent = true;
                        break;
                    case "--splash":
                        o.Splash = true;
                        break;
                    case "--no-splash":
                        o.Splash = false;
                        break;
                    case "--no-focus":
                        o._noFocus = true;
                        break;
                    case "--focus":
                        o._noFocus = false;
                        break;
                    case "--login-probe":
                        o.LoginProbe = true;
                        break;
                    case "--ui-probe":
                        o.UiProbe = true;
                        break;
                    case "--login-probe-stay":
                        o.LoginProbe = true;
                        o.LoginProbeQuits = false;
                        break;
                    case "--dual-probe":
                        o.DualProbe = true;
                        break;
                    case "--dual-probe-held":
                        o.DualProbe = true;
                        o.DualProbeHeld = true;
                        break;
                    case "--dual-screen":
                        o.DualSimulate = Next() ?? "";
                        break;
                    case "--dual-off":
                        o.DualOff = true;
                        break;
                    case "--screen-scale":
                        if (int.TryParse(Next(), out int screenScale))
                        {
                            o.ScreenScale = screenScale;
                        }

                        break;
                    case "--effects-probe":
                        if (int.TryParse(Next(), out int effects))
                        {
                            o.EffectsProbe = effects;
                        }

                        break;
                    case "--sound":
                        o.Sound = true;
                        break;
                    case "--account":
                        o.Account = Next();
                        break;
                    case "--password":
                        o.Password = Next();
                        break;
                    case "--autologin":
                        o.AutoLogin = true;
                        break;
                    case "--character":
                        o.Character = Next();
                        break;
                    case "--window-position":
                        {
                            string[] xy = Next().Split(',');

                            if (xy.Length == 2 && int.TryParse(xy[0], out int wx) && int.TryParse(xy[1], out int wy))
                            {
                                o.WindowPosition = new Vector2I(wx, wy);
                            }
                        }

                        break;
                    case "--window-size":
                        {
                            string[] wh = Next().Split(',');

                            if (wh.Length == 2 && int.TryParse(wh[0], out int ww) && int.TryParse(wh[1], out int wh2))
                            {
                                o.WindowSize = new Vector2I(ww, wh2);
                            }
                        }

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
                        o.ClientDataFromFlag = true;
                        break;
                    case "--custom-data":
                        o.CustomData = Next();
                        break;
                    case "--first-run-probe":
                        o.FirstRunProbe = Next();
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
                    case "--screenshot-name":
                        o.ScreenshotName = Next();
                        break;
                    case "--host":
                        o.ShardHost = Next();
                        break;
                    case "--store-install":
                        o.StoreInstall = Next();
                        break;
                    case "--files-override":
                        o.FilesOverride = Next();
                        break;
                    case "--background":
                        o.Background = Next();
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
