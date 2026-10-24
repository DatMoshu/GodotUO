// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

/// <summary>
/// What the perf probe's flags may be combined with. No engine types, so
/// tools/arg_trace_tests can compile this file alone and check it.
/// </summary>
public static class PerfProbeFlags
{
    /// <summary>
    /// Why <c>--perf-static-world</c> cannot be accepted with these options,
    /// or null when it can. The flag thins the world the render lists see;
    /// outside a probe that would silently hide items and effects from a
    /// player, so without <c>--perf-probe</c> the client refuses to start.
    /// </summary>
    public static string ProbeOnlyError(bool perfProbe, bool staticWorld)
        => staticWorld && !perfProbe
            ? "--perf-static-world is a perf probe flag and does nothing outside one; add --perf-probe"
            : null;

    /// <summary>
    /// Why <c>--perf-quiet</c> cannot be accepted, or null when it can. It
    /// drops every incoming message and the weather; outside a probe that
    /// would hide the server from a player.
    /// </summary>
    public static string QuietOnlyError(bool perfProbe, bool quiet)
        => quiet && !perfProbe
            ? "--perf-quiet is a perf probe flag and does nothing outside one; add --perf-probe"
            : null;
}
