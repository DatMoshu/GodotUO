// SPDX-License-Identifier: BSD-2-Clause
// The perf probe's preflight rules (src/Bootstrap/LoginPreflight.cs), run
// without the engine: the negative cases a measuring run must refuse.
using GUO.Host;

int checks = 0;
void Require(bool ok, string what)
{
    checks++;
    if (!ok)
    {
        Console.WriteLine($"FAIL {what}");
        Environment.Exit(1);
    }
}

// Credentials: the allocated account's configured secret, nothing else.
{
    var env = new Dictionary<string, string>
    {
        ["UO_SHARD_OWNER"] = "owneracct",
        ["UO_SHARD_OWNER_PASSWORD"] = "s3cret-owner",
        ["UO_SHARD_GM_ACCOUNTS"] = "gmone, gmtwo",
        ["UO_SHARD_GM_PASSWORD"] = "s3cret-gm",
    };
    string Env(string key) => env.GetValueOrDefault(key);

    Require(!LoginPreflight.ResolveCredential(null, "owneracct", Env, out _, out string why) && why.Contains("no account was allocated"),
        "missing allocation is refused");
    Require(!LoginPreflight.ResolveCredential("", "owneracct", Env, out _, out _), "blank allocation is refused");
    Require(!LoginPreflight.ResolveCredential("owneracct", "guoprobe", Env, out _, out why) && why.Contains("another account"),
        "a client set up for another account is refused");
    Require(LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out string secret, out _) && secret == "s3cret-owner",
        "the owner's configured secret resolves");
    Require(LoginPreflight.ResolveCredential("gmtwo", "gmtwo", Env, out secret, out _) && secret == "s3cret-gm",
        "a listed GM's configured secret resolves");
    Require(!LoginPreflight.ResolveCredential("someone", "someone", Env, out secret, out why) && secret == null && why.Contains("not used as one"),
        "an unlisted account has no credential; the account name is never one");

    env["UO_SHARD_OWNER_PASSWORD"] = "";
    Require(!LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out secret, out _) && secret == null,
        "an empty configured secret is refused, not replaced by the account text");
    env["UO_SHARD_OWNER_PASSWORD"] = "OWNERACCT";
    Require(!LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out secret, out why) && why.Contains("account name itself"),
        "a credential equal to the account name is refused");
    env.Remove("UO_SHARD_OWNER");
    Require(!LoginPreflight.ResolveCredential("owneracct", "owneracct", Env, out _, out _), "no owner configured, no owner credential");
}

// Fields: typing into a prefilled field doubles it; the strict path never types.
{
    Require(LoginPreflight.TypedInto("guoprobe", "guoprobe") == "guoprobeguoprobe", "a prefilled field plus the typed account is a doubled account");
    Require(LoginPreflight.TypedInto("", "guoprobe") == "guoprobe", "an empty field plus the typed account is the account");
    Require(LoginPreflight.TypedInto("other", "guoprobe") != "guoprobe", "a field holding another account never becomes the typed one");
}

// Identity: the loaded profile's account and character, both.
{
    Require(LoginPreflight.IdentityMatches("owneracct", "Guoprobe", "owneracct", "Guoprobe", out _), "matching account and character pass");
    Require(!LoginPreflight.IdentityMatches("other", "Guoprobe", "owneracct", "Guoprobe", out string why) && why.Contains("another account"),
        "the right character on the wrong account is refused");
    Require(!LoginPreflight.IdentityMatches("owneracct", "Someone", "owneracct", "Guoprobe", out _), "the wrong character is refused");
    Require(!LoginPreflight.IdentityMatches("OwnerAcct", "Guoprobe", "owneracct", "Guoprobe", out _), "the account comparison is exact");
    Require(LoginPreflight.IdentityMatches("owneracct", "Guoprobe", null, "Guoprobe", out _), "no expected account checks the character only");
}

// Arrival: x, y within 3, z within 2 when the [go names one, the map when expected.
{
    Require(LoginPreflight.ArrivedAt(1010, 760, -60, 0, "[go 1010 760 -60", 0, out _), "the crypt floor at -60");
    Require(LoginPreflight.ArrivedAt(1011, 761, -61, 0, "[go 1010 760 -60", 0, out _), "within tolerance on every axis");
    Require(!LoginPreflight.ArrivedAt(1010, 760, 0, 0, "[go 1010 760 -60", 0, out string where) && where == "1010,760,0 map 0",
        "the roof at z 0 is the wrong floor for the crypt");
    Require(!LoginPreflight.ArrivedAt(1499, 1761, 0, 0, "[go 1499 1761 -3", 0, out _), "the land at z 0 is not the pier at -3");
    Require(LoginPreflight.ArrivedAt(1499, 1761, -1, 0, "[go 1499 1761 -3", 0, out _), "the pier within two of its z");
    Require(!LoginPreflight.ArrivedAt(1014, 760, -60, 0, "[go 1010 760 -60", 0, out _), "four tiles off in x is not there");
    Require(!LoginPreflight.ArrivedAt(1010, 760, -60, 1, "[go 1010 760 -60", 0, out _), "the right place on the wrong map");
    Require(LoginPreflight.ArrivedAt(1010, 760, -60, 1, "[go 1010 760 -60", -1, out _), "no expected map checks the place only");
    Require(LoginPreflight.ArrivedAt(1164, 1668, 5, 0, "[go 1164 1668", 0, out _), "no z in the [go: z unchecked");
    Require(LoginPreflight.ArrivedAt(1, 2, 3, 0, "say hello", 0, out _), "not a [go: nothing to check");
}

// The startup decision: autologin is its own connect route and is refused;
// otherwise the credential rules, with the two accounts as given.
{
    var env = new Dictionary<string, string> { ["UO_SHARD_OWNER"] = "owneracct", ["UO_SHARD_OWNER_PASSWORD"] = "s3cret-owner" };
    string Env(string key) => env.GetValueOrDefault(key);
    Require(!LoginPreflight.Decide("owneracct", "owneracct", true, Env, out string secret, out string why) && secret == null && why.Contains("autologin"),
        "strict with autologin is refused before the credential is even resolved");
    Require(LoginPreflight.Decide("owneracct", "owneracct", false, Env, out secret, out _) && secret == "s3cret-owner",
        "strict without autologin resolves the allocated account's secret");
    Require(!LoginPreflight.Decide("owneracct", "guoprobe", false, Env, out _, out _), "the configured account must be the allocated one");
    Require(!LoginPreflight.Decide("guoprobe", "owneracct", false, Env, out _, out _), "and the other way round");
    Require(!LoginPreflight.Decide(null, "owneracct", false, Env, out _, out _), "no allocation, no connect");
}

Console.WriteLine($"PASS probe rules: {checks} checks; CPU only, no login");
