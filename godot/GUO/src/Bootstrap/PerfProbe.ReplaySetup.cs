// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

namespace GUO.Host;

internal static partial class PerfProbe
{
    // Preparation only, before warm-up/timing. A real turn is allowed, but a
    // moved origin, adjusted heading or denial is never rescued by a teleport.
    private static async Task PrepareReplayOrigin(Node host, PerfWalkRoute route)
    {
        using var setup = new PerfWalkSetup(route, new PerfWalkReplay.PlayerSource(),
            () => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
        async Task NextFrame() { await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); }
        while (!setup.Done)
        {
            setup.Step();
            if (!setup.Done) await setup.WaitForSignal(NextFrame());
        }
        setup.Dispose(); // Validate terminal state and restore observer before accepting setup.
        if (setup.Failed) throw new InvalidOperationException("Replay setup failed: " + setup.Failure);
    }
}
