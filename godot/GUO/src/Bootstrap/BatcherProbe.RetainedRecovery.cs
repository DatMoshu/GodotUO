// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Renderer;
using Command = GUO.Renderer.RetainedCanvasCommands.Command;
using Kind = GUO.Renderer.RetainedCanvasCommands.Kind;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyRetainedRecovery()
    {
        var sink = new CanvasOracle();
        var budget = new RetainedCanvasCommands.Budget();
        var cache = new RetainedCanvasCommands(budget, sink, default);
        var expected = new Command[8193];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = new Command((Kind)(i % 3), default, default,
                new Rect2(i, 2, 3, 4), new Rect2(5, i, 7, 8), Colors.White,
                new Transform2D(0, new Vector2(i, 0)));

        void Frame(int length)
        {
            // Mirror BeginFrame's uncached/empty owner path, not just Begin.
            if (!cache.HasCommands) { sink.Clear(default); cache.Reset(); }
            cache.Begin();
            for (int i = 0; i < length; i++) cache.Add(expected[i]);
            cache.End();
            bool exact = sink.Stream.Count == length;
            for (int i = 0; exact && i < length; i++) exact &= sink.Stream[i].Same(expected[i]);
            CheckThat("retained recovery exact native order", exact, $"{length} unique commands, owner Clear/Reset");
            CheckThat("retained recovery capacity stays bounded", budget.Capacity == 8192,
                "same backing capacity survives overflow and shrink");
        }

        Frame(8192);
        CheckThat("8192 commands are retainable", cache.HasCommands, "at per-item limit");
        Frame(8193);
        CheckThat("8193 commands fall back", !cache.HasCommands, "complete prefix and suffix remain native");
        int clears = sink.Clears;
        Frame(8193);
        CheckThat("repeated overflow uses owner clear", sink.Clears == clears + 1 && !cache.HasCommands,
            "no accumulation across uncached frames");
        clears = sink.Clears;
        Frame(3);
        CheckThat("shrinking stream resumes retention", cache.HasCommands && sink.Clears == clears + 1,
            "first small frame records after owner reset");
        int emits = sink.Emits;
        clears = sink.Clears;
        Frame(3);
        CheckThat("recovered identical stream makes no native calls",
            cache.HasCommands && sink.Emits == emits && sink.Clears == clears,
            "all recovered commands reused");
        Frame(0);
        CheckThat("empty recovered stream removes old suffix", !cache.HasCommands && sink.Clears == clears + 1,
            "native item is empty");
        clears = sink.Clears;
        Frame(0);
        CheckThat("empty owner reset stays empty", sink.Stream.Count == 0 && sink.Clears == clears + 1,
            "no stale commands after repeated empty frame");
    }
}
