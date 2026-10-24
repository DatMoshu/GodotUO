// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Network;

namespace GUO.Host;

internal readonly record struct PerfWalkPoint(int Map, int X, int Y, int Z);

internal readonly record struct PerfWalkConditions(bool Mounted, bool Flying, CharacterSpeedType SpeedMode,
    bool AlwaysRun, bool AlwaysRunUnlessHidden, bool Hidden, bool Dead, int Stamina)
{
    // Mirror PlayerMobile.Walk's existing run gate; never change the profile.
    internal bool EffectiveRun(bool requested) => (requested || AlwaysRun)
        && !(SpeedMode >= CharacterSpeedType.CantRun || Stamina <= 1 && !Dead || Hidden && AlwaysRunUnlessHidden);
}

// Diagnostic artifact only. Load once outside all timed/allocation baselines.
// {"version":1,"run":true,"initial_direction":6,"points":[[map,x,y,z],...47]}
internal sealed class PerfWalkRoute
{
    private readonly PerfWalkPoint[] _points;
    internal PerfWalkPoint Origin => _points[0];
    internal PerfWalkPoint this[int index] => _points[index];
    internal bool Run { get; }
    internal Direction InitialDirection { get; }
    internal string Hash { get; }
    // Map selection is deliberately left to the caller, outside measurement.
    internal string Go => $"[go {Origin.X} {Origin.Y} {Origin.Z}";

    private PerfWalkRoute(PerfWalkPoint[] points, bool run, Direction direction, string hash)
    {
        _points = points; Run = run; InitialDirection = direction; Hash = hash;
    }

    internal static PerfWalkRoute Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 65536) throw new InvalidDataException("Walk route exceeds 64 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    internal static PerfWalkRoute Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 65536) throw new InvalidDataException("Walk route exceeds 64 KiB.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported walk route version.");
        bool run = root.GetProperty("run").GetBoolean();
        int facing = root.GetProperty("initial_direction").GetInt32();
        if (facing < 0 || facing > 7) throw new InvalidDataException("Invalid initial direction.");
        JsonElement points = root.GetProperty("points");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() != 47)
            throw new InvalidDataException("Walk route requires exactly 47 points (46 tiles).");
        var kept = new PerfWalkPoint[47];
        for (int i = 0; i < kept.Length; i++)
        {
            JsonElement p = points[i];
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 4)
                throw new InvalidDataException("Walk point must contain map/X/Y/Z.");
            int map = p[0].GetInt32(), x = p[1].GetInt32(), y = p[2].GetInt32(), z = p[3].GetInt32();
            if (map < 0 || map > 255 || x < 0 || x > ushort.MaxValue || y < 0 || y > ushort.MaxValue
                || z < sbyte.MinValue || z > sbyte.MaxValue)
                throw new InvalidDataException("Walk point is outside protocol coordinate bounds.");
            kept[i] = new PerfWalkPoint(map, x, y, z);
            if (i != 0 && (map != kept[0].Map
                || Math.Max(Math.Abs(x - kept[i - 1].X), Math.Abs(y - kept[i - 1].Y)) != 1))
                throw new InvalidDataException("Walk route contains a map change, duplicate or nonadjacent tile.");
        }
        return new PerfWalkRoute(kept, run, (Direction)facing, Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
    }
}

// Main-thread-only transport seam; tests need no client, server, clock or Godot.
internal interface IPerfWalkSource
{
    PerfWalkPoint Position { get; }
    Direction Direction { get; }
    bool Healthy { get; }
    bool Drained { get; }
    bool Crowded { get; }
    PerfWalkConditions Conditions { get; }
    Action<WalkDiagnosticEvent, byte> Observer { get; set; }
    bool TryWalk(Direction direction, bool run, out StepInfo request);
}

internal sealed class PerfWalkReplay : IDisposable
{
    internal const long ReleaseCadenceMs = 250, StepTimeoutMs = 5000, OverallTimeoutMs = 180000;
    private readonly PerfWalkRoute _route;
    private readonly IPerfWalkSource _source;
    private readonly PerfWalkConditions _conditions;
    private readonly Func<long> _clock;
    private readonly Action<WalkDiagnosticEvent, byte> _previousObserver, _observer;
    private readonly PerfWalkPoint[] _completed = new PerfWalkPoint[47];
    private readonly long[] _completedMs = new long[47];
    private readonly long[] _requestMs = new long[92], _ackMs = new long[92];
    private readonly bool[] _turnRequests = new bool[92];
    private readonly long _started;
    private long _now, _deadline, _releaseAt;
    private int _count = 1, _requests, _acks, _turns;
    private bool _pending, _acked, _turn, _disposed;
    private bool _terminalValidated;
    private byte _sequence;
    private Direction _direction, _facing;
    private PerfWalkPoint _destination;
    private string _failure;

    internal PerfWalkReplay(PerfWalkRoute route) : this(route, new PlayerSource(), MonotonicMs) { }

    internal PerfWalkReplay(PerfWalkRoute route, IPerfWalkSource source, Func<long> clock)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (!source.Healthy || !source.Drained || source.Crowded || source.Position != route.Origin
            || (source.Direction & Direction.Mask) != route.InitialDirection)
            throw new InvalidOperationException("Replay requires exact origin/facing, healthy world and drained walk queues.");
        _conditions = source.Conditions;
        if (_conditions.EffectiveRun(route.Run) != route.Run)
            throw new InvalidOperationException("Replay requested run mode conflicts with player/profile preconditions.");
        _started = _now = _releaseAt = clock();
        _deadline = _now + StepTimeoutMs;
        _completed[0] = source.Position;
        _facing = route.InitialDirection;
        _previousObserver = source.Observer;
        _observer = OnWalk;
        source.Observer = _observer;
    }

    private static long MonotonicMs() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
    internal bool Failed => _failure != null;
    // Terminal, including failure: lets the caller stop its measurement loop.
    internal bool Done => Failed || _count == 47;
    internal string Failure => _failure;
    internal bool Valid => _disposed && _terminalValidated && !Failed && _count == 47;
    internal int Tiles => _count - 1;
    internal int RouteDropped => 0; // Exactly 47 completed points fit; unexpected positions fail explicitly.
    private void Fail(string reason) { _failure ??= reason; }

    private bool CheckTime(long now)
    {
        if (now < _now) { Fail("clock-reset"); return false; }
        _now = now;
        if (now - _started >= OverallTimeoutMs) { Fail("overall-timeout"); return false; }
        if (now >= _deadline) { Fail("step-timeout"); return false; }
        return true;
    }

    private void OnWalk(WalkDiagnosticEvent kind, byte sequence)
    {
        if (!_disposed && !Failed && CheckTime(_clock()))
        {
            if (kind != WalkDiagnosticEvent.Confirmed)
                Fail(kind == WalkDiagnosticEvent.Denied ? "walk-denied" : kind == WalkDiagnosticEvent.Reset ? "walk-reset" : "bad-ack");
            else if (!_pending || sequence != _sequence || _acked)
                Fail("unexpected-or-duplicate-ack");
            else
            {
                _acked = true;
                _ackMs[_requests - 1] = _now - _started;
                _acks++;
            }
        }
        _previousObserver?.Invoke(kind, sequence);
    }

    // No arrays, delegates, report objects or strings are allocated per frame.
    // Calls the real Walk path; its existing side effects/allocations are unchanged.
    internal void Step()
    {
        if (_disposed || Done) return;
        if (!CheckTime(_clock())) return;
        if (_source.Observer != _observer) { Fail("observer-replaced"); return; }
        if (!_source.Healthy || _source.Crowded) { Fail("world-or-queue-diverged"); return; }
        PerfWalkPoint current = _source.Position;
        if (_pending)
        {
            // Local animation may reach the predicted destination before ACK.
            if (current != _completed[_count - 1] && current != _destination)
            { Fail("position-diverged"); return; }
            Direction facing = _source.Direction & Direction.Mask;
            if (facing != _facing && facing != _direction) { Fail("direction-diverged"); return; }
            if (!_acked || !_source.Drained) return;
            if (current != _destination || (_source.Direction & Direction.Mask) != _direction)
            { Fail("completion-diverged"); return; }
            _pending = false;
            _facing = _direction;
            if (_turn) _turns++;
            else
            {
                _completed[_count] = current;
                _completedMs[_count++] = _now - _started;
            }
            // Cadence is a minimum idle interval after completion, never a
            // wall-clock catch-up schedule. One turn/move maximum per call.
            _releaseAt = _now + ReleaseCadenceMs;
            _deadline = _releaseAt + StepTimeoutMs;
            return;
        }
        if (current != _completed[_count - 1] || !_source.Drained || (_source.Direction & Direction.Mask) != _facing)
        { Fail("idle-position-or-queue-diverged"); return; }
        if (_now < _releaseAt) return;
        PerfWalkPoint next = _route[_count];
        _direction = DirectionHelper.CalculateDirection(current.X, current.Y, next.X, next.Y);
        _turn = (_source.Direction & Direction.Mask) != _direction;
        _destination = _turn ? current : next;
        Direction oldDirection = _source.Direction & Direction.Mask;
        if (!_source.TryWalk(_direction, _route.Run, out StepInfo request))
        {
            if (!_source.Drained) Fail("unreported-walk-request");
            return; // Rate-limit or blockage retries only until the same deadline.
        }
        if (Failed) return;
        if (_source.Crowded || _requests == _requestMs.Length) { Fail("request-queue-overflow"); return; }
        _turnRequests[_requests] = _turn;
        _requestMs[_requests++] = _now - _started;
        if (request.X != _destination.X || request.Y != _destination.Y || request.Z != _destination.Z
            || request.Direction != (byte)_direction || request.Running != _route.Run
            || request.OldDirection != (byte)oldDirection)
        { Fail("request-diverged"); return; }
        _sequence = request.Sequence;
        _acked = false;
        _pending = true;
        _deadline = _now + StepTimeoutMs;
    }

    // Allocation is intentional only for final serialization, outside measurement.
    internal int[][] Route()
    {
        var result = new int[_count][];
        for (int i = 0; i < _count; i++)
        {
            PerfWalkPoint p = _completed[i];
            result[i] = new[] { p.Map, p.X, p.Y, p.Z };
        }
        return result;
    }

    internal Dictionary<string, object> Report()
    {
        if (!_disposed) throw new InvalidOperationException("Dispose replay after the final draw drain before snapshotting its report.");
        var timing = new Dictionary<string, object>[_requests];
        for (int i = 0; i < _requests; i++) timing[i] = new()
        {
            ["turn"] = _turnRequests[i], ["request_ms"] = _requestMs[i],
            ["ack_ms"] = i < _acks ? _ackMs[i] : null,
        };
        var completedTimes = new long[_count];
        Array.Copy(_completedMs, completedTimes, _count);
        PerfWalkPoint position = _source.Position;
        return new Dictionary<string, object>
        {
            ["route_hash"] = _route.Hash, ["success"] = Valid,
            ["requested_run"] = _route.Run, ["initial_direction"] = (int)_route.InitialDirection,
            ["movement_preconditions"] = new Dictionary<string, object>
            {
                ["mounted"] = _conditions.Mounted, ["flying"] = _conditions.Flying,
                ["speed_mode"] = (int)_conditions.SpeedMode, ["always_run"] = _conditions.AlwaysRun,
                ["always_run_unless_hidden"] = _conditions.AlwaysRunUnlessHidden,
                ["hidden"] = _conditions.Hidden, ["dead"] = _conditions.Dead,
                ["initial_stamina"] = _conditions.Stamina, ["effective_run"] = _conditions.EffectiveRun(_route.Run),
            },
            ["failed"] = Failed, ["failure"] = Failure, ["tiles"] = Tiles,
            ["next_point_index"] = _count, ["request_pending"] = _pending,
            ["completed_points"] = Route(), ["completed_ms"] = completedTimes,
            ["last_position"] = new[] { position.Map, position.X, position.Y, position.Z },
            ["route_dropped"] = RouteDropped, ["requests"] = _requests, ["acks"] = _acks,
            ["turns"] = _turns, ["request_timings"] = timing,
            ["elapsed_ms"] = _now - _started, ["release_cadence_ms"] = ReleaseCadenceMs,
            ["step_timeout_ms"] = StepTimeoutMs, ["overall_timeout_ms"] = OverallTimeoutMs,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Finalize after the draw drain. Done only means the route loop ended;
        // it is not acceptance until terminal state and cleanup are checked.
        if (!Failed && _count == 47)
        {
            if (!_source.Healthy || !_source.Drained || _source.Crowded
                || _source.Position != _route[46] || (_source.Direction & Direction.Mask) != _facing)
                Fail("terminal-state-diverged");
            else if ((_source.Conditions with { Stamina = _conditions.Stamina }) != _conditions)
                Fail("terminal-movement-mode-diverged");
            else _terminalValidated = true;
        }
        if (_source.Observer == _observer) _source.Observer = _previousObserver;
        else Fail("observer-replaced"); // Do not overwrite a later owner's observer.
        if (!Done) Fail("disposed-before-completion");
        _disposed = true;
        // Do not clear queues, teleport or modify live movement on disposal.
    }

    internal sealed class PlayerSource : IPerfWalkSource
    {
        private readonly PlayerMobile _player;
        private readonly bool _mounted;
        private readonly bool _flying;
        private readonly CharacterSpeedType _speed;
        internal PlayerSource()
        {
            _player = Client.Game?.UO.World?.Player ?? throw new InvalidOperationException("No world player for walk replay.");
            _mounted = _player.IsMounted;
            _flying = _player.IsFlying;
            _speed = _player.SpeedMode;
        }
        public PerfWalkPoint Position => new(_player.World.MapIndex, _player.X, _player.Y, _player.Z);
        public Direction Direction => _player.Direction;
        public PerfWalkConditions Conditions => new(_mounted, _flying, _speed,
            ProfileManager.CurrentProfile.AlwaysRun, ProfileManager.CurrentProfile.AlwaysRunUnlessHidden,
            _player.IsHidden, _player.IsDead, _player.Stamina);
        public bool Healthy => Client.Game?.UO.World == _player.World && _player.World.Player == _player
            && _player.World.InGame && NetClient.Socket.IsConnected && !_player.Pathfinder.AutoWalking
            && !_player.Walker.WalkingFailed && !_player.Walker.ResendPacketResync
            && _player.IsMounted == _mounted && _player.IsFlying == _flying && _player.SpeedMode == _speed;
        public bool Drained => _player.Steps.Count == 0 && _player.Walker.StepsCount == 0
            && _player.Walker.UnacceptedPacketsCount == 0 && _player.Walker.CurrentWalkSequence == 0;
        public bool Crowded => _player.Walker.StepsCount > 1 || _player.Walker.UnacceptedPacketsCount > 1 || _player.Steps.Count > 1;
        public Action<WalkDiagnosticEvent, byte> Observer
        {
            get => _player.Walker.DiagnosticObserver;
            set => _player.Walker.DiagnosticObserver = value;
        }
        public bool TryWalk(Direction direction, bool run, out StepInfo request)
        {
            request = default;
            if (!_player.Walk(direction, run)) return false;
            if (_player.Walker.StepsCount != 1) return false;
            request = _player.Walker.StepInfos[0];
            return true;
        }
    }
}
