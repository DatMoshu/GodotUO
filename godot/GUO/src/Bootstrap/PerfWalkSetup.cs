// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Threading.Tasks;
using GUO.Game.Data;
using GUO.Game.Managers;

namespace GUO.Host;

// Preparation only. One actual turn maximum; no route movement, replan or rescue.
internal sealed class PerfWalkSetup : IDisposable
{
    private readonly PerfWalkRoute _route;
    private readonly IPerfWalkSource _source;
    private readonly Func<long> _clock;
    private readonly long _deadline;
    private readonly Action<WalkDiagnosticEvent, byte> _previous, _observer;
    private bool _requested, _acked, _complete, _disposed;
    private byte _sequence;
    private string _failure;
    internal bool Done => Failed || _complete;
    internal bool Failed => _failure != null;
    internal string Failure => _failure;

    internal PerfWalkSetup(PerfWalkRoute route, IPerfWalkSource source, Func<long> clock)
    {
        _route = route; _source = source; _clock = clock;
        if (!source.Healthy || !source.Drained || source.Crowded || source.Position != route.Origin)
            throw new InvalidOperationException("Setup requires exact origin and healthy drained movement queues.");
        _deadline = clock() + 10000;
        _previous = source.Observer;
        _observer = Observe;
        source.Observer = _observer;
    }

    private void Fail(string reason) { _failure ??= reason; }
    private bool CheckDeadline()
    {
        if (_clock() < _deadline) return true;
        Fail("setup-timeout"); return false;
    }

    private void Observe(WalkDiagnosticEvent kind, byte sequence)
    {
        if (!_disposed && !Failed && CheckDeadline())
        {
            if (kind != WalkDiagnosticEvent.Confirmed)
                Fail(kind == WalkDiagnosticEvent.Denied ? "setup-denied" : kind == WalkDiagnosticEvent.Reset ? "setup-reset" : "setup-bad-ack");
            else if (!_requested || _acked || sequence != _sequence) Fail("setup-unexpected-ack");
            else _acked = true;
        }
        _previous?.Invoke(kind, sequence);
    }

    private bool CheckOrigin()
    {
        if (_source.Observer != _observer) { Fail("setup-observer-replaced"); return false; }
        if (!_source.Healthy || _source.Crowded || _source.Position != _route.Origin)
        { Fail("setup-origin-or-world-diverged"); return false; }
        return true;
    }

    internal void Step()
    {
        if (_disposed || Failed || !CheckDeadline() || !CheckOrigin()) return;
        bool desired = (_source.Direction & Direction.Mask) == _route.InitialDirection;
        if (_complete) return;
        if (_requested)
        {
            if (_source.Drained && _acked)
            {
                if (!desired) Fail("setup-turn-direction-diverged");
                else _complete = true;
            }
            return; // Never issue a second request, including after queue reset.
        }
        if (!_source.Drained) { Fail("setup-competing-request"); return; }
        if (desired) { _complete = true; return; } // Already facing correctly: no turn needed.
        Direction old = _source.Direction & Direction.Mask;
        if (!_source.TryWalk(_route.InitialDirection, _route.Run, out StepInfo request))
        {
            if (!_source.Drained) Fail("setup-unreported-request");
            return; // Refusal without a request may retry until the same deadline.
        }
        _requested = true;
        _sequence = request.Sequence;
        if (_source.Crowded || request.X != _route.Origin.X || request.Y != _route.Origin.Y || request.Z != _route.Origin.Z
            || request.Direction != (byte)_route.InitialDirection || request.OldDirection != (byte)old || request.Running != _route.Run)
            Fail("setup-request-diverged");
    }

    internal Task WaitForSignal(Task signal)
    {
        long remaining = _deadline - _clock();
        if (remaining <= 0) throw new TimeoutException("Replay setup turn did not settle before its deadline.");
        return signal.WaitAsync(TimeSpan.FromMilliseconds(remaining));
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!Failed)
        {
            if (!_complete) Fail("setup-disposed-before-completion");
            else if (CheckDeadline() && CheckOrigin()
                && (!_source.Drained || (_source.Direction & Direction.Mask) != _route.InitialDirection || (_requested && !_acked)))
                Fail("setup-terminal-state-diverged");
        }
        if (_source.Observer == _observer) _source.Observer = _previous;
        else Fail("setup-observer-replaced");
        _disposed = true;
    }
}
