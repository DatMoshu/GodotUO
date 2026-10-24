// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Godot;
using GUO.Game.Scenes;
using GUO.Renderer;

namespace GUO.Host;

internal static partial class PerfProbe
{
    // Opt-in, once per scene, between frames and outside the measured interval.
    // Capture numeric keys only: no objects, texture pixels, account or settings.
    // Reflection stays entirely in the probe; ordinary rendering is unchanged.
    private static async System.Threading.Tasks.Task<string> CaptureWorldSortInput(Node host, string scene)
    {
        if (scene == "login" || System.Environment.GetEnvironmentVariable("GUO_PERF_SORT_INPUT") != "1")
            return null;

        // ProcessFrame signals alone can occur without a client draw. Require
        // new completed draws before reading a scene's self-consistent queue.
        var fence = new ParityDrawFence(UltimaBatcher2D.FramesBegun, Godot.Time.GetTicksMsec() + 10000);
        async System.Threading.Tasks.Task PostDraw()
            => await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        do
        {
            await fence.WaitForSignal(PostDraw(), Godot.Time.GetTicksMsec());
        }
        while (!fence.Observe(UltimaBatcher2D.FramesBegun, Godot.Time.GetTicksMsec(), false, false));

        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var owner = Client.Game?.Scene as GameScene
            ?? throw new InvalidOperationException("Sort input requires a live game scene.");
        var lists = (RenderLists)typeof(GameScene).GetField("_renderLists", fields).GetValue(owner);
        var world = (IList)typeof(RenderLists).GetField("_world", fields).GetValue(lists);
        var sorted = (ulong[])typeof(RenderLists).GetField("_keys", fields).GetValue(lists);
        int count = world.Count;
        if (count == 0 || count > 1_000_000 || sorted.Length < count)
            throw new InvalidOperationException("Sort input has no completed bounded world queue.");

        var original = new ulong[count];
        Type drawable = world[0].GetType();
        FieldInfo depth = drawable.GetField("Depth", fields), seq = drawable.GetField("Seq", fields);
        for (int i = 0; i < count; i++)
        {
            object item = world[i];
            int index = (int)seq.GetValue(item);
            if (index != i) throw new InvalidOperationException("Sort input is not in queue-index order.");
            original[i] = DepthKey.Of((float)depth.GetValue(item), index);
        }
        var expected = (ulong[])original.Clone();
        Array.Sort(expected);
        if (!expected.AsSpan().SequenceEqual(sorted.AsSpan(0, count)))
            throw new InvalidOperationException("Sort input does not match the last completed world sort.");

        string directory = ProjectSettings.GlobalizePath(_outDir);
        Directory.CreateDirectory(directory);
        string name = $"sort-input_{_label}_{scene}.json";
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new
        {
            scene, count, static_world = StaticWorld, source = "last completed world queue",
            original_sort_verified = true, timings_are_diagnostic = true, keys = original,
            actual_client_draws_after_request = fence.DrawsSince(UltimaBatcher2D.FramesBegun),
        }));
        GD.Print($"[GUO] sort input: {scene}, {count} verified keys; diagnostic run");
        return name;
    }
}
