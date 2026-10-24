// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

/// <summary>
/// The quiet perf fixture (PERF-LANE-01): weather frozen off and every
/// incoming message dropped, so two launches photograph the same world.
/// </summary>
/// <remarks>
/// Cross-launch captures of an unchanged build differed by 5,000-12,000
/// pixels, nearly all of it rain and server text (the bottom-left system
/// lines, the overhead echo of the probe's own [go). Set only while a perf
/// probe (<c>--perf-quiet</c>) or PerfDump (<c>GUO_PERF_QUIET=1</c>) runs and
/// cleared when it ends; the flag is refused without <c>--perf-probe</c>
/// (<see cref="PerfProbeFlags.QuietOnlyError"/>), so ordinary play never
/// loses a message. Read at two seams: PacketHandlers' weather packet and
/// MessageManager.HandleMessage. No engine types here.
/// </remarks>
public static class PerfFixture
{
    public static bool Quiet { get; internal set; }
}
