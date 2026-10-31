// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Renderer;
using Command = GUO.Renderer.RetainedCanvasCommands.Command;
using Kind = GUO.Renderer.RetainedCanvasCommands.Kind;

namespace GUO.Host;

public partial class BatcherProbe
{
    private sealed class ReclamationOracle : RetainedCanvasCommands.ISink
    {
        internal readonly CanvasOracle Native = new();
        internal readonly RetainedCanvasCommands.Budget Budget;
        internal int CapacityAtClear = -1;
        internal ReclamationOracle(RetainedCanvasCommands.Budget budget) => Budget = budget;
        public void Clear(Rid item) { CapacityAtClear = Budget.Capacity; Native.Clear(item); }
        public void Emit(Rid item, in Command command) => Native.Emit(item, command);
    }

    private void VerifyRetainedReclamation()
    {
        var commands = new Command[8193];
        for (int i = 0; i < commands.Length; i++)
            commands[i] = new Command((Kind)(i % 3), default, default,
                new Rect2(i, 1, 2, 3), new Rect2(4, i, 6, 7), Colors.White,
                new Transform2D(0, new Vector2(i, 0)));

        void Frame(RetainedCanvasCommands cache, CanvasOracle sink, int count, bool reclaim)
        {
            if (!cache.HasCommands) { sink.Clear(default); cache.Reset(); }
            cache.Begin();
            for (int i = 0; i < count; i++) cache.Add(commands[i]);
            cache.End(reclaim);
            bool exact = sink.Stream.Count == count;
            for (int i = 0; exact && i < count; i++) exact = sink.Stream[i].Same(commands[i]);
            CheckThat("reclamation preserves complete native stream", exact, $"reclaim={reclaim}, count={count}");
        }

        foreach (bool reclaim in new[] { false, true })
        {
            var budget = new RetainedCanvasCommands.Budget();
            var owners = new RetainedCanvasCommands[8];
            var sinks = new ReclamationOracle[8];
            for (int i = 0; i < owners.Length; i++)
            {
                sinks[i] = new ReclamationOracle(budget);
                owners[i] = new RetainedCanvasCommands(budget, sinks[i], default);
                Frame(owners[i], sinks[i].Native, 8192, reclaim);
            }
            CheckThat("reclamation fixture fills real shared budget", budget.Capacity == 65536,
                "eight owners, unchanged 8192 item / 65536 batcher limits");
            var receiverSink = new CanvasOracle();
            var receiver = new RetainedCanvasCommands(budget, receiverSink, default);
            Frame(receiver, receiverSink, 8192, reclaim);
            CheckThat("full budget falls back without dropping draws", !receiver.HasCommands,
                "no free slots before finalized empty donor");

            int hiddenEmits = sinks[1].Native.Emits, hiddenClears = sinks[1].Native.Clears;
            owners[0].Begin(); owners[0].End(reclaim);
            CheckThat("empty End clears native before returning capacity",
                sinks[0].CapacityAtClear == 65536 && sinks[0].Native.Stream.Count == 0
                && budget.Capacity == (reclaim ? 57344 : 65536), $"reclaim={reclaim}");
            owners[0].End(reclaim); owners[0].Reset(); owners[0].Begin(); owners[0].End(reclaim);
            CheckThat("repeated empty End and Reset never double release",
                budget.Capacity == (reclaim ? 57344 : 65536), "capacity charged exactly once");
            Frame(receiver, receiverSink, 8192, reclaim);
            int submits = receiverSink.Emits;
            Frame(receiver, receiverSink, 8192, reclaim);
            int repeatedSubmits = receiverSink.Emits - submits;
            CheckThat("other owner reuses released slots and avoids native resubmission",
                receiver.HasCommands == reclaim && repeatedSubmits == (reclaim ? 0 : 8192)
                && budget.Capacity == 65536, $"reclaim={reclaim}, repeated native records={repeatedSubmits}");
            GD.Print($"[retained-reclaim] cross-owner reclaim={reclaim} repeated_native_records={repeatedSubmits} capacity={budget.Capacity}");

            // Owner 1 was untouched/hidden during the donor and receiver changes.
            Frame(owners[1], sinks[1].Native, 8192, reclaim);
            CheckThat("hidden nonempty owner keeps native stream and warm cache",
                sinks[1].Native.Emits == hiddenEmits && sinks[1].Native.Clears == hiddenClears,
                "no reclaim of an owner that did not finalize empty");

            // At full capacity, regrowth of the reclaimed donor safely falls back.
            Frame(owners[0], sinks[0].Native, 3, reclaim);
            CheckThat("donor regrowth respects unchanged global budget",
                owners[0].HasCommands == !reclaim && budget.Capacity == 65536, "same exact three native commands");
        }

        var bypassBudget = new RetainedCanvasCommands.Budget();
        var bypassSink = new CanvasOracle();
        var bypass = new RetainedCanvasCommands(bypassBudget, bypassSink, default);
        Frame(bypass, bypassSink, 256, true);
        bypass.Begin(); bypass.Bypass(); bypassSink.Emit(default, commands[2]);
        bypass.Add(commands[1]); bypass.End(true);
        CheckThat("uncached bypass End preserves storage and raw command order",
            bypassBudget.Capacity == 256 && !bypass.HasCommands
            && bypassSink.Matches(new[] { commands[2], commands[1] }), "no reclaim through uncached early return");
        bypassSink.Clear(default); bypass.Reset();
        CheckThat("owner recovery Reset does not reclaim warm storage", bypassBudget.Capacity == 256, "native owner already cleared");
        Frame(bypass, bypassSink, 3, true);
        Frame(bypass, bypassSink, 8193, true);
        CheckThat("per-item overflow End never reclaims uncached storage",
            bypassBudget.Capacity == 8192 && !bypass.HasCommands, "all 8193 commands remain native");
        Frame(bypass, bypassSink, 0, true);
        CheckThat("cacheable empty frame after overflow returns storage once", bypassBudget.Capacity == 0,
            "owner reset followed by successful empty End");
        Frame(bypass, bypassSink, 256, true);

        long CycleAllocations(bool reclaim, bool empty)
        {
            var sink = new CanvasOracle();
            sink.Stream.Capacity = 256;
            var cache = new RetainedCanvasCommands(new RetainedCanvasCommands.Budget(), sink, default);
            void Cycle()
            {
                cache.Begin();
                for (int i = 0; i < 256; i++) cache.Add(commands[i]);
                cache.End(reclaim);
                if (empty) { cache.Begin(); cache.End(reclaim); }
            }
            for (int i = 0; i < 4; i++) Cycle();
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) Cycle();
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        long off = CycleAllocations(false, true), on = CycleAllocations(true, true);
        long stable = CycleAllocations(true, false);
        CheckThat("empty reclamation exposes regrowth allocation tradeoff", off == 0 && on > off,
            $"32 cycles x 256 commands: off={off}, on={on} managed bytes; no FPS inference");
        CheckThat("nonempty retained reuse remains allocation free", stable == 0, $"{stable} managed bytes");
        GD.Print($"[retained-reclaim] regrowth_cycles=32 commands=256 off_bytes={off} on_bytes={on} nonempty_bytes={stable}");
    }
}
