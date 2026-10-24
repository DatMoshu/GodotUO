// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Threading.Tasks;
using GUO.Game.Data;
using GUO.Game.Managers;

namespace GUO.Host;

public partial class BatcherProbe
{
    // Invoked by VerifyPerfWalkReplay: no additional central entrypoint edit.
    private void VerifyPerfWalkFinalization()
    {
        PerfWalkRoute route = PerfWalkRoute.Parse(WalkRouteJson());
        PerfWalkReplay Completed(out WalkReplaySource source)
        {
            long tick = 0;
            source = new WalkReplaySource();
            var replay = new PerfWalkReplay(route, source, () => tick);
            for (int request = 0; request < 47; request++)
            {
                replay.Step(); source.Animate(); source.Confirm(); replay.Step();
                tick += PerfWalkReplay.ReleaseCadenceMs;
            }
            return replay;
        }
        {
            using var replay = Completed(out var fake);
            bool rejected = false;
            try { replay.Report(); }
            catch (InvalidOperationException) { rejected = true; }
            CheckThat("terminal report requires observer cleanup", replay.Tiles == 46 && !replay.Valid && rejected,
                "completion alone cannot snapshot acceptance");
            replay.Dispose();
            CheckThat("validated terminal cleanup permits success snapshot", replay.Valid && (bool)replay.Report()["success"]
                && fake.Observer == null, "observer restored before report");
        }
        {
            using var replay = Completed(out var fake);
            Action<WalkDiagnosticEvent, byte> replacement = (_, _) => { };
            fake.Observer = replacement;
            replay.Step(); // Previously returned Done and allowed a true report.
            replay.Dispose();
            CheckThat("late observer replacement invalidates final snapshot", replay.Failure == "observer-replaced"
                && !replay.Valid && !(bool)replay.Report()["success"] && fake.Observer == replacement,
                "finalize before serializing, do not clobber later owner");
        }
        for (int mode = 0; mode < 5; mode++)
        {
            using var replay = Completed(out var fake);
            if (mode == 0) fake.Position = fake.Position with { X = fake.Position.X + 1 };
            if (mode == 1) fake.Healthy = false;
            if (mode == 2) fake.Direction = Direction.South;
            if (mode == 3) fake.TryWalk(Direction.East, true, out _);
            if (mode == 4) fake.Conditions = fake.Conditions with { Mounted = true };
            replay.Step(); replay.Dispose();
            CheckThat("terminal drift cannot escape snapshot " + mode,
                replay.Failed && !replay.Valid && !(bool)replay.Report()["success"] && fake.Observer == null,
                "endpoint/world/facing/queue/movement conditions checked after draw drain");
        }

        bool TimedOut(Action action)
        {
            try { action(); return false; }
            catch (TimeoutException) { return true; }
        }
        // These are the SAME fence methods used by Measure's final drain.
        var fence = new ParityDrawFence(100, 1000);
        CheckThat("replay drain counts actual draws", !fence.Observe(100, 100, true, true)
            && !fence.Observe(101, 200, true, true) && fence.Observe(102, 999, true, true), "two draws before deadline");
        CheckThat("late second drain draw fails post-await", TimedOut(() =>
        {
            fence.WaitForSignal(Task.CompletedTask, 999).GetAwaiter().GetResult();
            fence.Observe(102, 1000, true, true);
        }), "deadline checked before completed draw count");
        CheckThat("already late drain signal cannot start wait", TimedOut(() =>
            fence.WaitForSignal(Task.CompletedTask, 1000).GetAwaiter().GetResult()), "pre-await deadline");
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortFence = new ParityDrawFence(0, 1);
        CheckThat("missing final drain signal has independent timeout", TimedOut(() =>
            shortFence.WaitForSignal(never.Task, 0).GetAwaiter().GetResult()), "never-completing task, bounded one-ms wait");

        long now = 0;
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            int oldCalls = 0;
            Action<WalkDiagnosticEvent, byte> previous = (_, _) => oldCalls++;
            fake.Observer = previous;
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); fake.Animate(); setup.Step();
            CheckThat("setup local turn is not an ACK", !setup.Done && fake.Calls == 1, "wait at exact origin");
            fake.Confirm(); setup.Step(); setup.Dispose();
            CheckThat("setup matching ACK and drain restores observer", setup.Done && !setup.Failed
                && fake.Observer == previous && oldCalls == 1 && fake.Position == route.Origin && fake.Calls == 1,
                "one real turn, no route movement");
        }
        foreach (WalkDiagnosticEvent kind in new[] { WalkDiagnosticEvent.Denied, WalkDiagnosticEvent.Reset, WalkDiagnosticEvent.BadAck })
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); fake.Animate();
            fake.Observer(kind, fake.Request.Sequence);
            fake.ClearQueues(); setup.Step(); setup.Dispose();
            CheckThat("setup fails after locally completed turn " + kind, setup.Failed && fake.Calls == 1
                && fake.Observer == null && fake.Position == route.Origin, "cleared queues never replace a matching ACK");
        }
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); fake.Animate(); fake.ClearQueues(); setup.Step();
            CheckThat("setup drained queues alone do not prove turn", !setup.Done && fake.Calls == 1,
                "no ACK and no second request");
            now += 10000; setup.Step(); setup.Dispose();
            CheckThat("setup missing ACK times out and restores observer", setup.Failure == "setup-timeout"
                && fake.Observer == null, "still no movement rescue");
        }
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); fake.Observer(WalkDiagnosticEvent.Confirmed, 17); setup.Dispose();
            CheckThat("setup wrong ACK fails", setup.Failure == "setup-unexpected-ack" && fake.Observer == null,
                "exact request sequence");
        }
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); fake.Animate(); now += 10000; fake.Confirm(); setup.Dispose();
            CheckThat("setup late ACK fails even at desired facing", setup.Failure == "setup-timeout" && fake.Observer == null,
                "deadline before ACK success");
        }
        {
            var fake = new WalkReplaySource();
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); setup.Dispose();
            CheckThat("already correct setup needs no turn", setup.Done && !setup.Failed && fake.Calls == 0 && fake.Observer == null,
                "only actual requests need an ACK");
        }
        {
            var fake = new WalkReplaySource();
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step();
            Action<WalkDiagnosticEvent, byte> replacement = (_, _) => { };
            fake.Observer = replacement;
            setup.Dispose();
            CheckThat("setup finalization checks observer ownership", setup.Failure == "setup-observer-replaced"
                && fake.Observer == replacement, "no accepted setup before observer cleanup");
        }
        {
            var fake = new WalkReplaySource { Direction = Direction.East, WrongDestination = true };
            using var setup = new PerfWalkSetup(route, fake, () => now);
            setup.Step(); setup.Dispose();
            CheckThat("setup rejects a real movement request", setup.Failure == "setup-request-diverged" && fake.Calls == 1,
                "never correct coordinates or retry a sent turn");
        }
        {
            var fake = new WalkReplaySource { Direction = Direction.East };
            var setup = new PerfWalkSetup(route, fake, () => now);
            try { throw new InvalidOperationException("synthetic setup exception"); }
            catch (InvalidOperationException) { }
            finally { setup.Dispose(); }
            CheckThat("setup exception cleanup restores observer", setup.Failure == "setup-disposed-before-completion"
                && fake.Observer == null, "no queue mutation during unwinding");
        }
    }
}
