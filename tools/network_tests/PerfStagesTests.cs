using GUO.Renderer;

internal static class PerfStagesTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value) throw new Exception("FAIL: " + name);
        }
        var ms = new double[(int)PerfStages.Stage.Count];
        var drawn = new int[ms.Length];
        PerfStages.Enabled = false;
        PerfStages.Take(ms, drawn);
        using (new PerfStages.Scope(PerfStages.Stage.NetworkReceive)) { }
        Check(PerfStages.Now() == 0 && PerfStages.Ms.All(x => x == 0), "disabled scope records nothing");

        var disabledScope = new PerfStages.Scope(PerfStages.Stage.PluginReceive);
        PerfStages.Enabled = true;
        disabledScope.Dispose();
        Check(PerfStages.Ms.All(x => x == 0), "scope begun disabled cannot record an epoch-sized duration");
        try
        {
            using var outer = new PerfStages.Scope(PerfStages.Stage.NetworkParse);
            using var inner = new PerfStages.Scope(PerfStages.Stage.NetworkDispatch);
            throw new InvalidOperationException("fixture");
        }
        catch (InvalidOperationException) { }
        double parse = PerfStages.Ms[(int)PerfStages.Stage.NetworkParse];
        double dispatch = PerfStages.Ms[(int)PerfStages.Stage.NetworkDispatch];
        Check(dispatch > 0 && parse >= dispatch, "throwing nested scope records inclusive parent and child");
        PerfStages.Take(ms, drawn);
        Check(ms[(int)PerfStages.Stage.NetworkParse] == parse &&
            ms[(int)PerfStages.Stage.NetworkDispatch] == dispatch, "Take exports scope totals");
        Check(PerfStages.Ms.All(x => x == 0), "Take resets every stage");
        Check(drawn.All(x => x == 0), "timing does not invent object counts");

        var enabledScope = new PerfStages.Scope(PerfStages.Stage.PluginTick);
        PerfStages.Enabled = false;
        enabledScope.Dispose();
        Check(PerfStages.Ms.All(x => x == 0), "scope ending disabled records nothing");
        return checks;
    }
}
