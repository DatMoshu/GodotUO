// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game.Scenes;
using GUO.Renderer;

namespace GUO.Host;

public partial class BatcherProbe
{
    private sealed class PerfAllocationControl : GUO.Game.UI.Controls.Control { }

    private void VerifyIncidentalAllocations()
    {
        bool wasDebug = CUOEnviroment.Debug;
        var control = new PerfAllocationControl();
        var lists = new RenderLists();
        float depth = 1;
        try
        {
            foreach (bool hidden in new[] { false, true })
            {
                CUOEnviroment.Debug = hidden;
                control.IsVisible = !hidden;
                for (int i = 0; i < 100; i++) control.AddToRenderLists(lists, 2, 3, ref depth);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) control.AddToRenderLists(lists, 2, 3, ref depth);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                CheckThat("unused debug draw allocates nothing " + hidden, bytes == 0 && lists.GumpTextsCount == 0,
                    "1000 draws, debug disabled or invisible control");
            }
            CUOEnviroment.Debug = true;
            control.IsVisible = true;
            control.AddToRenderLists(lists, 2, 3, ref depth);
            CheckThat("visible debug control still queues its deferred draw", lists.GumpTextsCount == 1
                && lists.PeekGumpText(0).Callback != null, "original debug callback retained");
        }
        finally { CUOEnviroment.Debug = wasDebug; }

        var cover = new CoverRuns();
        for (int i = 0; i < 60; i++) cover.NextFrame();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 600; i++) cover.NextFrame();
        CheckThat("idle cover expiry reuses scratch", GC.GetAllocatedBytesForCurrentThread() == allocated,
            "ten complete expiry periods after warmup");
    }
}
