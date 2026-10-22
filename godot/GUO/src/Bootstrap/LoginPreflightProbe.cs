// SPDX-License-Identifier: BSD-2-Clause
// The preflight rules inside the client, headless, no shard: the same
// negative cases tools/probe_rules_tests runs, plus the strict login path's
// own invariants (no typing, no creation, one Connect).
using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Host;

internal static class LoginPreflightProbe
{
    private static int _failed, _checks;

    private static void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok) _failed++;
        GD.Print($"[GUO] login preflight probe: {(ok ? "ok" : "FAIL")} {name}{(detail.Length > 0 ? " -- " + detail : "")}");
    }

    public static bool Run()
    {
        _failed = _checks = 0;
        try
        {
            var env = new Dictionary<string, string>
            {
                ["UO_SHARD_OWNER"] = "owneracct", ["UO_SHARD_OWNER_PASSWORD"] = "s3cret-owner",
                ["UO_SHARD_GM_ACCOUNTS"] = "gmone,gmtwo", ["UO_SHARD_GM_PASSWORD"] = "s3cret-gm",
            };
            string Env(string key) => env.GetValueOrDefault(key);

            Check("missing allocation refused", !LoginPreflight.ResolveCredential(null, "owneracct", Env, out _, out _));
            Check("other configured account refused", !LoginPreflight.ResolveCredential("owneracct", "guoprobe", Env, out _, out _));
            Check("owner secret resolves", LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out string s, out _) && s == "s3cret-owner");
            Check("unlisted account has no credential", !LoginPreflight.ResolveCredential("someone", "someone", Env, out s, out _) && s == null);
            env["UO_SHARD_OWNER_PASSWORD"] = "";
            Check("empty secret refused, no account-text fallback", !LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out s, out _) && s == null);
            Check("prefilled field doubles when typed into", LoginPreflight.TypedInto("guoprobe", "guoprobe") == "guoprobeguoprobe");
            Check("right character, wrong account refused", !LoginPreflight.IdentityMatches("other", "Guoprobe", "owneracct", "Guoprobe", out _));
            Check("crypt roof is the wrong floor", !LoginPreflight.ArrivedAt(1010, 760, 0, 0, "[go 1010 760 -60", 0, out _));
            Check("pier land is the wrong floor", !LoginPreflight.ArrivedAt(1499, 1761, 0, 0, "[go 1499 1761 -3", 0, out _));
            Check("crypt floor arrives", LoginPreflight.ArrivedAt(1011, 761, -61, 0, "[go 1010 760 -60", 0, out _));

            // The strict path's invariants, as the perf probe sets them up.
            InputProbe.Strict = true;
            InputProbe.ProbeAccount = "guoprobe";
            Check("strict mode never creates", InputProbe.NeverCreate);
            Check("strict mode with no allocation refuses before connecting",
                !InputProbe.StrictCredential(null, Env, out _, out string why) && why.Contains("no account"));
            Check("strict mode with the wrong configured account refuses",
                !InputProbe.StrictCredential("owneracct", Env, out _, out _));
            InputProbe.ProbeAccount = "owneracct";
            env["UO_SHARD_OWNER_PASSWORD"] = "s3cret-owner";
            Check("strict mode resolves the allocated account's secret", InputProbe.StrictCredential("owneracct", Env, out s, out _) && s == "s3cret-owner");
            Check("no Connect was issued by the rules", InputProbe.ConnectCalls == 0);
            InputProbe.Strict = false;
        }
        catch (Exception e)
        {
            Check($"unexpected exception {e}", false);
        }

        GD.Print($"[GUO] login preflight probe: {(_failed == 0 ? "PASS" : "FAIL")} ({_checks - _failed}/{_checks})");
        return _failed == 0;
    }
}
