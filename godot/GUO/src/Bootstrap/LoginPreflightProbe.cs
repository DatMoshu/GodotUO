// SPDX-License-Identifier: BSD-2-Clause
// The measuring run's login and capture rules inside the client, headless,
// no shard: the real argument parser and the real startup decision, the
// strict login's connect count with an injected connect, and the capture
// marker across the UI's own list draws.
using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;
using GUO.Renderer;

namespace GUO.Host;

internal static class LoginPreflightProbe
{
    /// <summary>How long one awaited frame may take before the check fails instead of hanging.</summary>
    public const int FrameTimeoutMs = 10_000;

    /// <summary>How long the whole probe may run before Main quits it with a failure.</summary>
    public const int RunTimeoutMs = 120_000;

    private static int _failed, _checks;

    private static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) _failed++;
        GD.Print($"[GUO] login preflight probe: {(ok ? "ok" : "FAIL")} {name}{(detail.Length > 0 ? " -- " + detail : "")}");
    }

    public static async System.Threading.Tasks.Task<bool> Run(Node host)
    {
        _failed = _checks = 0;
        string account = InputProbe.ProbeAccount;
        bool strict = InputProbe.Strict, autoLogin = InputProbe.AutoLogin, neverCreate = InputProbe.NeverCreate;
        string expect = PerfProbe.ExpectAccount;
        try
        {
            Rules();
            Startup();
            ConnectCount();
            await Marker(host);
        }
        catch (Exception e)
        {
            Check($"unexpected exception {e}", false);
        }
        finally
        {
            InputProbe.ProbeAccount = account;
            InputProbe.Strict = strict;
            InputProbe.AutoLogin = autoLogin;
            InputProbe.NeverCreate = neverCreate;
            PerfProbe.ExpectAccount = expect;
        }

        GD.Print($"[GUO] login preflight probe: {(_failed == 0 ? "PASS" : "FAIL")} ({_checks - _failed}/{_checks})");
        return _failed == 0;
    }

    private static Func<string, string> Env(string owner = "owneracct", string ownerSecret = "s3cret-owner")
    {
        var env = new Dictionary<string, string>
        {
            ["UO_SHARD_OWNER"] = owner, ["UO_SHARD_OWNER_PASSWORD"] = ownerSecret,
            ["UO_SHARD_GM_ACCOUNTS"] = "gmone,gmtwo", ["UO_SHARD_GM_PASSWORD"] = "s3cret-gm",
        };
        return key => env.GetValueOrDefault(key);
    }

    /// <summary>The engine-free rules, as tools/probe_rules_tests runs them.</summary>
    private static void Rules()
    {
        Func<string, string> env = Env();
        Check("missing allocation refused", !LoginPreflight.ResolveCredential(null, "owneracct", env, out _, out _));
        Check("other configured account refused", !LoginPreflight.ResolveCredential("owneracct", "guoprobe", env, out _, out _));
        Check("owner secret resolves", LoginPreflight.ResolveCredential("owneracct", "owneracct", env, out string s, out _) && s == "s3cret-owner");
        Check("unlisted account has no credential", !LoginPreflight.ResolveCredential("someone", "someone", env, out s, out _) && s == null);
        Check("empty secret refused, no account-text fallback", !LoginPreflight.ResolveCredential("owneracct", "owneracct", Env(ownerSecret: ""), out s, out _) && s == null);
        Check("prefilled field doubles when typed into", LoginPreflight.TypedInto("guoprobe", "guoprobe") == "guoprobeguoprobe");
        Check("right character, wrong account refused", !LoginPreflight.IdentityMatches("other", "Guoprobe", "owneracct", "Guoprobe", out _));
        Check("crypt roof is the wrong floor", !LoginPreflight.ArrivedAt(1010, 760, 0, 0, "[go 1010 760 -60", 0, out _));
        Check("pier land is the wrong floor", !LoginPreflight.ArrivedAt(1499, 1761, 0, 0, "[go 1499 1761 -3", 0, out _));
        Check("crypt floor arrives", LoginPreflight.ArrivedAt(1011, 761, -61, 0, "[go 1010 760 -60", 0, out _));
        Check("strict with autologin refused", !LoginPreflight.Decide("owneracct", "owneracct", true, env, out _, out _));
    }

    /// <summary>The real parser and the real startup decision (Main.StrictStartup), both flag orders.</summary>
    private static void Startup()
    {
        Func<string, string> env = Env();

        foreach (string[] args in new[]
        {
            new[] { "--account", "guoprobe", "--perf-expect-account", "owneracct" },
            new[] { "--perf-expect-account", "owneracct", "--account", "guoprobe" },
        })
        {
            Main.Options o = Main.Options.Parse(args);
            Check($"parse keeps the two accounts apart ({string.Join(" ", args)})",
                o.Account == "guoprobe" && o.ExpectAccount == "owneracct" && InputProbe.Strict);
            Check("startup refuses a configured account other than the allocated one",
                !Main.StrictStartup(o, env, out _, out string why) && why.Contains("another account"));
        }

        {
            Main.Options o = Main.Options.Parse(new[] { "--perf-expect-account", "owneracct", "--account", "owneracct", "--autologin" });
            Check("startup refuses strict with autologin before any connect",
                !Main.StrictStartup(o, env, out _, out string why) && why.Contains("autologin"));
        }

        {
            Main.Options o = Main.Options.Parse(new[] { "--perf-expect-account", "owneracct", "--account", "owneracct" });
            Check("startup allows the allocated account with its configured secret",
                Main.StrictStartup(o, env, out string secret, out _) && secret == "s3cret-owner");
        }

        {
            Main.Options o = Main.Options.Parse(new[] { "--perf-expect-account", "owneracct" });
            Check("without --account the configured account is the probe's default, and it is not the allocated one",
                !Main.StrictStartup(o, env, out _, out string why) && why.Contains("another account"));
            Main.Options d = Main.Options.Parse(new[] { "--perf-expect-account", InputProbe.DefaultAccountName });
            Check("the default configured account can be the allocated one",
                Main.StrictStartup(d, Env(owner: InputProbe.DefaultAccountName), out _, out _));
        }

        {
            Main.Options o = Main.Options.Parse(new[] { "--perf-expect-account", "nobody", "--account", "nobody" });
            Check("startup refuses an account with no configured credential",
                !Main.StrictStartup(o, env, out string secret, out string why) && secret == null && why.Contains("not used as one"));
        }
    }

    /// <summary>The strict login's connect count: zero when refused, one when allowed, never a field input.</summary>
    private static void ConnectCount()
    {
        int connects = 0;
        void Connect(string a, string p) => connects++;
        int fieldInputs = InputProbe.FieldInputs;
        int before = InputProbe.ConnectCalls;

        InputProbe.Strict = true;
        Check("strict implies never create", InputProbe.NeverCreate);

        InputProbe.ProbeAccount = "guoprobe";
        Check("refused: the configured account is not the allocated one, zero connects",
            !InputProbe.StrictLogin("owneracct", false, Env(), Connect, out _) && connects == 0);
        Check("refused: no allocation, zero connects", !InputProbe.StrictLogin(null, false, Env(), Connect, out _) && connects == 0);

        InputProbe.ProbeAccount = "owneracct";
        Check("refused: strict with autologin, zero connects", !InputProbe.StrictLogin("owneracct", true, Env(), Connect, out _) && connects == 0);
        Check("refused: empty configured secret, zero connects", !InputProbe.StrictLogin("owneracct", false, Env(ownerSecret: ""), Connect, out _) && connects == 0);
        Check("no connect was counted by the refusals", InputProbe.ConnectCalls == before);

        Check("allowed: exactly one connect with the allocated account",
            InputProbe.StrictLogin("owneracct", false, Env(), Connect, out _) && connects == 1 && InputProbe.ConnectCalls == before + 1);
        Check("no field input across the strict path", InputProbe.FieldInputs == fieldInputs);
    }

    /// <summary>
    /// The capture marker survives the UI's own list draws, which go
    /// through RenderLists.DrawRenderLists(batcher, maxGroundZ) after the
    /// world every frame: a world frame drawn on the pass stays marked at
    /// capture time. Headless, the client never draws (GameController's
    /// DrawFrame returns on the zero-size window before UIManager.Draw), and
    /// the rendering server never emits a frame signal, so the probe waits
    /// on process frames and calls the UI's overload itself, on empty
    /// lists, the way UIManager and the child lists do. The live proof is
    /// gpu_test_on_pass per capture in the parity run.
    /// </summary>
    private static async System.Threading.Tasks.Task Marker(Node host)
    {
        // Every wait is finite: a signal that never comes (the render frame
        // signal, headless, stranded a lease once) fails the check within
        // FrameTimeoutMs instead of hanging the client.
        async System.Threading.Tasks.Task Frame()
        {
            async System.Threading.Tasks.Task Signal() => await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            System.Threading.Tasks.Task signal = Signal();
            System.Threading.Tasks.Task first = await System.Threading.Tasks.Task.WhenAny(signal, System.Threading.Tasks.Task.Delay(FrameTimeoutMs));
            Check($"a process frame arrived within {FrameTimeoutMs} ms", first == signal);
        }

        var uiLists = new RenderLists();
        int draws = RenderLists.ListDraws;
        GpuWorld.LastFrameOnPass = true;
        await Frame();
        uiLists.DrawRenderLists(null, sbyte.MaxValue);
        await Frame();
        uiLists.DrawRenderLists(null, sbyte.MaxValue);
        await Frame();
        Check("the UI's list draws ran in between", RenderLists.ListDraws >= draws + 2, $"{RenderLists.ListDraws - draws} list draws");
        Check("a world frame marked as drawn on the pass stays marked across the UI's list draws", GpuWorld.LastFrameOnPass);

        GpuWorld.LastFrameOnPass = false;
        await Frame();
        uiLists.DrawRenderLists(null, sbyte.MaxValue);
        Check("and an unmarked one stays unmarked", !GpuWorld.LastFrameOnPass);
    }
}
