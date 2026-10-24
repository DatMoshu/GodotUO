// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Threading.Tasks;

namespace GUO.Host;

// A renderer post-draw signal can arrive while client pacing skips DrawFrame.
// Require two actual batcher frames after switching, not two engine signals.
internal readonly struct ParityDrawFence(long startDraws, ulong deadline)
{
    internal long DrawsSince(long draws) => draws - startDraws;

    internal Task WaitForSignal(Task signal, ulong now)
    {
        RequireBeforeDeadline(now);
        return signal.WaitAsync(TimeSpan.FromMilliseconds(deadline - now));
    }

    private void RequireBeforeDeadline(ulong now)
    {
        if (now >= deadline)
            throw new TimeoutException("Parity capture did not receive two actual client draws before its deadline.");
    }

    internal bool Observe(long draws, ulong now, bool selectedState, bool expectedState)
    {
        if (selectedState != expectedState || draws < startDraws)
            throw new InvalidOperationException("Parity capture state or draw counter changed unexpectedly.");
        RequireBeforeDeadline(now);
        return DrawsSince(draws) >= 2;
    }
}
