// SPDX-License-Identifier: BSD-2-Clause
// FROZEN CONTRACT (task 1155): the competition benchmark scene and time tables.
// Only the lead may change a coordinate here, after looking at fresh stills.
#if GUO_LABS && DEBUG
using System;
using System.Collections.Generic;

namespace GUO.Labs.CompetitionBench;

/// <summary>One benchmark location: Britain facet (map 0) tile, optional player Z override.</summary>
internal readonly record struct BenchScene(string Name, int X, int Y, int? Z, string Expect);

/// <summary>A pinned clock: shard hour:minute plus the classic light level used by the Classic variant.</summary>
internal readonly record struct BenchTime(string Name, int Hour, int Minute, int ClassicOverall);

/// <summary>
/// The single source of the scene and time tables. Python (tools/competition_bench)
/// carries only the names, never coordinates.
/// </summary>
internal static class CompetitionBenchScenes
{
    internal static readonly BenchScene[] Scenes =
    {
        new("town-roofs", 1417, 1734, null, "a town with roofs drawn and visible"),
        new("sloped-terrain", 1216, 1968, null, "visibly sloped terrain"),
        new("forest-edge", 1856, 1472, null, "trees on one side, open land on the other"),
    };

    internal static readonly BenchTime[] Times =
    {
        new("noon", 12, 0, 12),
        new("dusk", 17, 15, 20),
    };

    internal static BenchScene Scene(string name)
    {
        foreach (var scene in Scenes) if (scene.Name == name) return scene;
        throw new ArgumentException("unknown GUO_BENCH_SCENE '" + name + "'");
    }

    internal static BenchTime Time(string name)
    {
        foreach (var time in Times) if (time.Name == name) return time;
        throw new ArgumentException("unknown GUO_BENCH_TIME '" + name + "'");
    }

    internal static IEnumerable<string> SceneNames { get { foreach (var s in Scenes) yield return s.Name; } }
}
#endif
