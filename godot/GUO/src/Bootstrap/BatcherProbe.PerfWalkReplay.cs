// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text;
using GUO.Game.Data;
using GUO.Game.Managers;

namespace GUO.Host;

public partial class BatcherProbe
{
    private static byte[] WalkRouteJson(int count = 47, int stride = 1, int finalMap = 0, int direction = 0)
    {
        var text = new StringBuilder($"{{\"version\":1,\"run\":true,\"initial_direction\":{direction},\"points\":[");
        for (int i = 0; i < count; i++)
        {
            if (i != 0) text.Append(',');
            text.Append($"[{(i == count - 1 ? finalMap : 0)},{100 + i * stride},200,0]");
        }
        return Encoding.UTF8.GetBytes(text.Append("]}").ToString());
    }

    private sealed class WalkReplaySource : IPerfWalkSource
    {
        public PerfWalkPoint Position { get; set; } = new(0, 100, 200, 0);
        public Direction Direction { get; set; } = Direction.North;
        public bool Healthy { get; set; } = true;
        public bool Crowded { get; set; }
        public PerfWalkConditions Conditions { get; set; } = new(false, false, CharacterSpeedType.Normal, false, false, false, false, 100);
        public Action<WalkDiagnosticEvent, byte> Observer { get; set; }
        public bool Drained => !_active;
        internal bool Refuse, WrongDestination, WrongDirection, WrongRun;
        internal int Calls;
        internal byte NextSequence = 255;
        internal StepInfo Request;
        private bool _active, _ack, _animation;
        internal void ClearQueues() { _active = false; }

        public bool TryWalk(Direction direction, bool run, out StepInfo request)
        {
            Calls++;
            request = default;
            if (Refuse) return false;
            var target = Position;
            if ((Direction & Direction.Mask) == direction)
            {
                int dx = direction is Direction.Right or Direction.East or Direction.Down ? 1
                    : direction is Direction.Left or Direction.West or Direction.Up ? -1 : 0;
                int dy = direction is Direction.Down or Direction.South or Direction.Left ? 1
                    : direction is Direction.North or Direction.Right or Direction.Up ? -1 : 0;
                target = target with { X = target.X + dx, Y = target.Y + dy };
            }
            Request = new StepInfo
            {
                X = (ushort)(target.X + (WrongDestination ? 1 : 0)), Y = (ushort)target.Y, Z = (sbyte)target.Z,
                Direction = (byte)(WrongDirection ? Direction.West : direction), OldDirection = (byte)(Direction & Direction.Mask),
                Running = WrongRun ? !run : run, Sequence = NextSequence,
            };
            NextSequence = NextSequence == 255 ? (byte)1 : (byte)(NextSequence + 1);
            _active = true; _ack = _animation = false;
            request = Request;
            return true;
        }

        internal void Confirm()
        {
            _ack = true;
            Observer?.Invoke(WalkDiagnosticEvent.Confirmed, Request.Sequence);
            if (_animation) _active = false;
        }
        internal void Animate()
        {
            Position = new PerfWalkPoint(Position.Map, Request.X, Request.Y, Request.Z);
            Direction = (Direction)Request.Direction;
            _animation = true;
            if (_ack) _active = false;
        }
    }

    // Parent adds this call to its existing central probe entrypoint.
    private void VerifyPerfWalkReplay()
    {
        byte[] json = WalkRouteJson();
        PerfWalkRoute route = PerfWalkRoute.Parse(json);
        string hash = route.Hash;
        Array.Fill(json, (byte)0);
        CheckThat("walk route owns immutable validated points", route.Origin == new PerfWalkPoint(0, 100, 200, 0)
            && route[46].X == 146 && route.Hash == hash && route.Hash.Length == 64 && route.Go == "[go 100 200 0",
            "input buffer mutation cannot change route");
        bool Reject(byte[] bytes)
        {
            try { PerfWalkRoute.Parse(bytes); return false; }
            catch (InvalidDataException) { return true; }
        }
        CheckThat("walk route rejects wrong lengths", Reject(WalkRouteJson(46)) && Reject(WalkRouteJson(48)), "exactly 46 transitions");
        CheckThat("walk route rejects duplicates and jumps", Reject(WalkRouteJson(stride: 0)) && Reject(WalkRouteJson(stride: 2)), "adjacent tiles only");
        CheckThat("walk route rejects map and facing errors", Reject(WalkRouteJson(finalMap: 1)) && Reject(WalkRouteJson(direction: 8)), "single map and 0..7 facing");

        long now = 0;
        var source = new WalkReplaySource();
        int previousCalls = 0;
        Action<WalkDiagnosticEvent, byte> previous = (_, _) => previousCalls++;
        source.Observer = previous;
        using (var replay = new PerfWalkReplay(route, source, () => now))
        {
            replay.Step(); // Initial facing north: first request must only turn east.
            source.Animate(); replay.Step();
            CheckThat("turn animation without ACK does not advance", replay.Tiles == 0 && source.Calls == 1 && !replay.Failed,
                "one request in flight");
            source.Confirm(); replay.Step();
            CheckThat("turn ACK remains separate from tile completion", replay.Tiles == 0 && !replay.Done, "sequence 255");
            now = PerfWalkReplay.ReleaseCadenceMs - 1;
            replay.Step();
            CheckThat("walk release waits fixed cadence", source.Calls == 1, "no catch-up requests");
            now++;
            replay.Step(); // First tile, sequence wraps to 1.
            source.Confirm(); replay.Step();
            CheckThat("move ACK waits for animation", replay.Tiles == 0 && source.Calls == 2 && source.Request.Sequence == 1,
                "ACK is not local completion");
            source.Animate(); replay.Step();
            CheckThat("move completes after ACK and animation", replay.Tiles == 1, "exact XYZ");
            for (int tile = 1; tile < 46; tile++)
            {
                now += PerfWalkReplay.ReleaseCadenceMs;
                replay.Step();
                source.Animate(); source.Confirm(); replay.Step();
            }
            int calls = source.Calls;
            replay.Step();
            CheckThat("replay completes exactly 46 tiles", replay.Done && !replay.Failed && replay.Tiles == 46
                && replay.Route().Length == 47 && replay.Route()[46][1] == 146 && source.Calls == calls,
                "one turn plus 46 moves, no further request");
            replay.Dispose();
            var report = replay.Report();
            CheckThat("replay reports actual request and ACK coverage", (int)report["requests"] == 47
                && (int)report["acks"] == 47 && (int)report["turns"] == 1 && (bool)report["success"], "includes timings");
            source.Conditions = source.Conditions with { Mounted = true, Stamina = 0 };
            var conditions = (System.Collections.Generic.Dictionary<string, object>)replay.Report()["movement_preconditions"];
            CheckThat("replay reports captured movement preconditions", !(bool)conditions["mounted"]
                && (int)conditions["speed_mode"] == (int)CharacterSpeedType.Normal && (bool)conditions["effective_run"]
                && (int)conditions["initial_stamina"] == 100, "immutable constructor snapshot, not end-of-leg values");
        }
        CheckThat("replay restores previous observer", source.Observer == previous && previousCalls == 47,
            "previous witness chained and restored");

        bool RejectOrigin(WalkReplaySource candidate)
        {
            try { using var replay = new PerfWalkReplay(route, candidate, () => now); return false; }
            catch (InvalidOperationException) { return true; }
        }
        CheckThat("constructor rejects inexact origin or facing", RejectOrigin(new WalkReplaySource { Position = new(0, 101, 200, 0) })
            && RejectOrigin(new WalkReplaySource { Direction = Direction.East }), "strict start");
        var queued = new WalkReplaySource(); queued.TryWalk(Direction.East, true, out _);
        CheckThat("constructor rejects pending queues", RejectOrigin(queued), "no borrowed walk requests");
        CheckThat("constructor rejects incompatible run preconditions", RejectOrigin(new WalkReplaySource
            { Conditions = new(false, false, CharacterSpeedType.Normal, false, false, false, false, 1) }),
            "no request before failure");

        void EventFailure(WalkDiagnosticEvent kind, string failure)
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step();
            fake.Observer(kind, fake.Request.Sequence);
            int sent = fake.Calls; replay.Step();
            CheckThat("replay fails on " + kind, replay.Failed && replay.Done && replay.Failure == failure
                && replay.Tiles == 0 && fake.Calls == sent, "no rescue/replan");
        }
        EventFailure(WalkDiagnosticEvent.Denied, "walk-denied");
        EventFailure(WalkDiagnosticEvent.Reset, "walk-reset");
        EventFailure(WalkDiagnosticEvent.BadAck, "bad-ack");

        foreach (int mode in new[] { 0, 1, 2 })
        {
            var fake = new WalkReplaySource { WrongDestination = mode == 0, WrongDirection = mode == 1, WrongRun = mode == 2 };
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step();
            CheckThat("replay rejects adjusted target direction or run " + mode, replay.Failure == "request-diverged", "fail after real request, never follow detour");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); fake.Observer(WalkDiagnosticEvent.Confirmed, 17);
            CheckThat("wrong sequence ACK fails", replay.Failure == "unexpected-or-duplicate-ack", "one tracked request");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); fake.Confirm(); fake.Confirm();
            CheckThat("duplicate ACK fails", replay.Failure == "unexpected-or-duplicate-ack", "do not double count");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); now += PerfWalkReplay.StepTimeoutMs; fake.Confirm();
            CheckThat("late successful ACK cannot beat deadline", replay.Failure == "step-timeout", "deadline checked inside callback");
        }
        {
            var fake = new WalkReplaySource { Refuse = true };
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); now += PerfWalkReplay.StepTimeoutMs; replay.Step();
            CheckThat("blocked route fails on deadline", replay.Failure == "step-timeout" && replay.Tiles == 0, "no heading changes");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); now += PerfWalkReplay.StepTimeoutMs; replay.Step();
            CheckThat("missing ACK fails on deadline", replay.Failure == "step-timeout", "pending request is bounded");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            now += PerfWalkReplay.OverallTimeoutMs; replay.Step();
            CheckThat("overall deadline bounds replay", replay.Failure == "overall-timeout", "no dependence on game ticks");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); fake.Position = fake.Position with { Z = 1 }; replay.Step();
            CheckThat("unexpected correction fails", replay.Failure == "position-diverged", "exact map/XYZ");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            fake.Direction = Direction.South; replay.Step();
            CheckThat("unexpected idle facing fails", replay.Failure == "idle-position-or-queue-diverged", "do not compensate for foreign turns");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); fake.Crowded = true; replay.Step();
            CheckThat("competing queue fails", replay.Failure == "world-or-queue-diverged", "no second in-flight request");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            fake.Healthy = false; replay.Step();
            CheckThat("disconnect or unhealthy world fails", replay.Failure == "world-or-queue-diverged", "no requests to a changed session");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            now--; replay.Step();
            CheckThat("clock reset fails", replay.Failure == "clock-reset", "monotonic deadlines");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step(); fake.Animate(); fake.Confirm();
            now += 1000; replay.Step(); // Complete late, still before deadline.
            int calls = fake.Calls;
            replay.Step();
            CheckThat("late completion cannot cause catch-up burst", fake.Calls == calls && !replay.Failed,
                "next release is relative to observed completion");
        }
        {
            var fake = new WalkReplaySource { Refuse = true };
            using var replay = new PerfWalkReplay(route, fake, () => now);
            for (int i = 0; i < 32; i++) replay.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) replay.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            CheckThat("replay frame controller allocates nothing", bytes == 0, $"{bytes} bytes on blocked retry path, transport excluded");
        }
        {
            var fake = new WalkReplaySource();
            using var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Step();
            for (int i = 0; i < 32; i++) replay.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) replay.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            CheckThat("pending replay frames allocate nothing", bytes == 0 && fake.Calls == 1,
                $"{bytes} bytes waiting for ACK and animation");
        }
        {
            var fake = new WalkReplaySource();
            var replay = new PerfWalkReplay(route, fake, () => now);
            Action<WalkDiagnosticEvent, byte> replacement = (_, _) => { };
            fake.Observer = replacement;
            replay.Step(); replay.Dispose();
            CheckThat("observer ownership change fails without clobber", replay.Failure == "observer-replaced" && fake.Observer == replacement,
                "later owner's observer survives disposal");
        }
        {
            var fake = new WalkReplaySource();
            var replay = new PerfWalkReplay(route, fake, () => now);
            replay.Dispose(); replay.Dispose();
            CheckThat("early Dispose is explicit and restores observer", replay.Failure == "disposed-before-completion" && fake.Observer == null,
                "idempotent, no queue mutation");
        }
        // Exercise the real observer seams that require neither player assets
        // nor network IO. BadAck's existing resync is already marked sent.
        {
            var walker = new WalkerManager(null);
            WalkDiagnosticEvent observed = WalkDiagnosticEvent.Confirmed;
            byte sequence = 0;
            walker.DiagnosticObserver = (kind, seq) => { observed = kind; sequence = seq; };
            walker.StepsCount = 1; walker.WalkSequence = 42;
            walker.Reset();
            CheckThat("real walker reset emits diagnostic event", observed == WalkDiagnosticEvent.Reset
                && walker.StepsCount == 0 && walker.WalkSequence == 0, "normal reset semantics preserved");
            walker.ResendPacketResync = true;
            walker.ConfirmWalk(14);
            CheckThat("real walker bad ACK emits diagnostic event", observed == WalkDiagnosticEvent.BadAck
                && sequence == 14 && walker.WalkingFailed, "no socket call in this fixture");
        }
        VerifyPerfWalkFinalization();
    }
}
