// SPDX-License-Identifier: BSD-2-Clause
// The rules a measuring run checks before it logs in, once it is in the
// world and before each capture. No engine type here: tools/probe_rules_tests
// compiles this file on its own and runs the negative cases with dotnet.
using System;

namespace GUO.Host;

/// <summary>
/// PORT DEVIATION (GUO): the perf probe's preflight (--perf-expect-account,
/// --perf-expect-character, --perf-expect-map). A measuring run must log in
/// as the allocated account with its configured credential and nothing
/// else, never type into prefilled fields, never create an account or a
/// character, and capture a scene only where it actually stands.
/// </summary>
internal static class LoginPreflight
{
    /// <summary>How far from a scene's [go target the player may stand, in tiles.</summary>
    public const int ArriveXyTolerance = 3;

    /// <summary>How far from the target's z, when the [go names one.</summary>
    public const int ArriveZTolerance = 2;

    /// <summary>
    /// The credential for the allocated account: the secret configured for
    /// that account (the dev shard's owner or a listed GM), read from the
    /// environment the launchers export. The account text itself is never
    /// a credential, a missing allocation is refused, and a client set up
    /// for another account is refused before anything connects.
    /// </summary>
    public static bool ResolveCredential(string expectedAccount, string configuredAccount, Func<string, string> env,
        out string secret, out string why)
    {
        secret = null;
        if (string.IsNullOrWhiteSpace(expectedAccount))
        {
            why = "no account was allocated (--perf-expect-account)";
            return false;
        }

        if (!string.Equals(expectedAccount, configuredAccount, StringComparison.Ordinal))
        {
            why = "the client is set up for another account than the allocated one";
            return false;
        }

        string candidate = null;
        string owner = env("UO_SHARD_OWNER");
        if (!string.IsNullOrEmpty(owner) && string.Equals(owner, expectedAccount, StringComparison.OrdinalIgnoreCase))
        {
            candidate = env("UO_SHARD_OWNER_PASSWORD");
        }
        else
        {
            foreach (string gm in (env("UO_SHARD_GM_ACCOUNTS") ?? "").Split(',', ';', ' '))
            {
                if (gm.Trim().Length > 0 && string.Equals(gm.Trim(), expectedAccount, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = env("UO_SHARD_GM_PASSWORD");
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(candidate))
        {
            why = "no credential is configured for the allocated account (UO_SHARD_OWNER with UO_SHARD_OWNER_PASSWORD,"
                + " or UO_SHARD_GM_ACCOUNTS with UO_SHARD_GM_PASSWORD); the account name is not used as one";
            return false;
        }

        if (string.Equals(candidate, expectedAccount, StringComparison.OrdinalIgnoreCase))
        {
            why = "the configured credential is the account name itself; refused";
            return false;
        }

        secret = candidate;
        why = null;
        return true;
    }

    /// <summary>
    /// The startup decision of a strict (measuring) run: whether anything
    /// may connect at all. Upstream's autologin (-autologin, -skiploginscreen)
    /// is its own connect route and is refused outright; otherwise the
    /// allocated account and the configured one are compared as they were
    /// given (the parser never copies one into the other) and the credential
    /// is resolved. False means zero connects, whatever the route.
    /// </summary>
    public static bool Decide(string expectedAccount, string configuredAccount, bool autoLoginRequested,
        Func<string, string> env, out string secret, out string why)
    {
        if (autoLoginRequested)
        {
            secret = null;
            why = "a strict run cannot use autologin (its own connect route); drop --autologin";
            return false;
        }

        return ResolveCredential(expectedAccount, configuredAccount, env, out secret, out why);
    }

    /// <summary>
    /// What typing leaves in a field that already holds text: the saved
    /// login plus the typed one. This is the hazard the strict path avoids
    /// by not typing at all (one guarded Connect).
    /// </summary>
    public static string TypedInto(string prefilled, string typed) => (prefilled ?? "") + (typed ?? "");

    /// <summary>
    /// The identity the world was entered with, as the loaded profile
    /// records it: the account the client connected with and the shard let
    /// in, and the character. A character name alone proves nothing about
    /// the account, so both are required when both are expected.
    /// </summary>
    public static bool IdentityMatches(string profileAccount, string profileCharacter, string expectedAccount,
        string expectedCharacter, out string why)
    {
        if (expectedAccount != null && !string.Equals(profileAccount, expectedAccount, StringComparison.Ordinal))
        {
            why = "the profile is for another account than the allocated one";
            return false;
        }

        if (expectedCharacter != null && !string.Equals(profileCharacter, expectedCharacter, StringComparison.Ordinal))
        {
            why = $"the profile is for character '{profileCharacter}', not '{expectedCharacter}'";
            return false;
        }

        why = null;
        return true;
    }

    /// <summary>
    /// The player stands at the scene's target: on the expected map, within
    /// <see cref="ArriveXyTolerance"/> tiles, and within
    /// <see cref="ArriveZTolerance"/> of the target z when the [go names one
    /// (a crypt at -60 is not its roof at 0; a pier at -3 is not the land).
    /// </summary>
    public static bool ArrivedAt(int x, int y, int z, int map, string go, int expectMap, out string where)
    {
        where = $"{x},{y},{z} map {map}";
        if (expectMap >= 0 && map != expectMap)
        {
            return false;
        }

        string[] parts = (go ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[1], out int tx) || !int.TryParse(parts[2], out int ty))
        {
            return true; // not a [go X Y: nothing to check against
        }

        if (Math.Abs(x - tx) > ArriveXyTolerance || Math.Abs(y - ty) > ArriveXyTolerance)
        {
            return false;
        }

        if (parts.Length >= 4 && int.TryParse(parts[3], out int tz) && Math.Abs(z - tz) > ArriveZTolerance)
        {
            return false;
        }

        return true;
    }
}
