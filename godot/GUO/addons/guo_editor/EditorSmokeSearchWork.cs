#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

public partial class EditorSmoke
{
    private sealed class CountingSearchProvider : SearchProvider
    {
        private readonly string _name;
        private readonly List<SearchEntry> _entries = new();
        public bool Enabled;
        public int Calls;
        public int Remaining = 3;
        public bool PauseAfterFirst;
        public CountingSearchProvider(string name, bool enabled) { _name = name; Enabled = enabled; }
        public override string Name => _name;
        public override bool Done => Remaining == 0;
        public override bool CanStep => Enabled;
        // Deliberately inherits constant Progress: it is not a readiness signal.
        public override IEnumerable<SearchEntry> Entries => _entries;
        public override void Step(double ms)
        {
            Calls++;
            if (!Enabled && Calls > 1000) throw new InvalidOperationException("blocked provider was repeatedly stepped");
            if (!Enabled || Done) return;
            _entries.Add(new SearchEntry { Kind = "Synthetic", Title = _name + Remaining, Key = _name + Remaining }.Prepare());
            Remaining--;
            if (PauseAfterFirst && Remaining == 2) Enabled = false;
        }
    }

    private void CheckSearchIndexWork()
    {
        var cases = new Dictionary<string, object>();
        _report["search_index_work"] = cases;
        foreach (string name in new[] { "blocked_first", "all_blocked", "ready", "completed", "zero", "negative", "resume" })
        {
            var first = new CountingSearchProvider("first", name is "ready" or "completed");
            var later = new CountingSearchProvider("later", name != "all_blocked");
            var index = new SearchIndex();
            index.Add(first);
            index.Add(later);
            if (name == "completed") { first.Remaining = 0; later.Remaining = 0; }
            double budget = name == "zero" ? 0 : name == "negative" ? -1 : 3;
            long start = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 8; i++) index.Step(budget);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            int blockedCalls = first.Calls, laterCalls = later.Calls;
            bool beforeReady = index.Ready;
            if (name == "resume")
            {
                first.Enabled = true;
                for (int i = 0; i < 8; i++) index.Step(3);
            }
            bool wantReady = name is "ready" or "completed" or "resume";
            string[] keys = index.Query("first").SelectMany(g => g.Items).Select(i => i.Entry.Key).ToArray();
            bool ok = index.Ready == wantReady;
            if (name is "blocked_first" or "all_blocked" or "resume") ok &= blockedCalls == 0;
            if (name is "blocked_first" or "resume") ok &= laterCalls == 3;
            if (name == "all_blocked") ok &= laterCalls == 0;
            if (name is "zero" or "negative" or "completed") ok &= first.Calls == 0 && later.Calls == 0;
            if (name is "ready" or "resume") ok &= keys.SequenceEqual(new[] { "first1", "first2", "first3" });
            cases[name] = new Dictionary<string, object>
            {
                ["ok"] = ok, ["initial_first_calls"] = blockedCalls, ["initial_later_calls"] = laterCalls,
                ["first_calls"] = first.Calls, ["later_calls"] = later.Calls,
                ["first_remaining"] = first.Remaining, ["later_remaining"] = later.Remaining,
                ["initial_ready"] = beforeReady, ["ready"] = index.Ready, ["pending"] = index.Pending,
                ["progress"] = index.Progress, ["query_keys"] = keys, ["ms"] = elapsed, ["managed_bytes"] = bytes,
            };
            if (!ok) _failures.Add("Search scheduler " + name + ": completion or query contract changed");
        }

        // Real UO providers with no install attached must stay pending, without loader access.
        var context = new SearchContext();
        var real = new SearchIndex();
        real.Add(new UoArtProvider(context));
        real.Add(new UoHueProvider(context));
        real.Add(new UoSoundProvider(context));
        real.Add(new UoClilocProvider(context));
        long tick = Stopwatch.GetTimestamp();
        for (int i = 0; i < 8; i++) real.Step(3);
        cases["missing_data"] = new Dictionary<string, object>
        {
            ["ok"] = !real.Ready && real.Providers.All(p => !p.CanStep && !p.Entries.Any()),
            ["ready"] = real.Ready, ["pending"] = real.Pending, ["progress"] = real.Progress,
            ["ms"] = Stopwatch.GetElapsedTime(tick).TotalMilliseconds,
        };
        if (real.Ready || real.Providers.Any(p => p.CanStep || p.Entries.Any())) _failures.Add("Search scheduler missing-data state changed");

        var finish = new SearchIndex();
        var waiting = new CountingSearchProvider("waiting", false);
        var available = new CountingSearchProvider("available", true);
        finish.Add(waiting);
        finish.Add(available);
        finish.FinishNow();
        bool finishPending = !finish.Ready && waiting.Calls == 0 && available.Calls == 3;
        waiting.Enabled = true;
        finish.FinishNow();
        bool finishOk = finishPending && finish.Ready && waiting.Calls == 3 && available.Calls == 3;
        cases["finish_resume"] = new Dictionary<string, object> { ["ok"] = finishOk, ["waiting_calls"] = waiting.Calls, ["available_calls"] = available.Calls };
        if (!finishOk) _failures.Add("Search scheduler FinishNow waiting/resume contract changed");

        var pauses = new SearchIndex();
        var pausing = new CountingSearchProvider("pausing", true) { PauseAfterFirst = true };
        var follower = new CountingSearchProvider("follower", true);
        pauses.Add(pausing);
        pauses.Add(follower);
        pauses.Step(3);
        bool pausedOk = pausing.Calls == 1 && pausing.Remaining == 2 && follower.Done && !pauses.Ready;
        pausing.Enabled = true;
        pauses.Step(3);
        pausedOk &= pauses.Ready && pausing.Calls == 3;
        bool placesAvailable = new PlacesProvider(context).CanStep;
        cases["pause_mid_slice"] = new Dictionary<string, object> { ["ok"] = pausedOk, ["calls"] = pausing.Calls };
        cases["places_without_data"] = new Dictionary<string, object> { ["ok"] = placesAvailable };
        if (!pausedOk || !placesAvailable) _failures.Add("Search scheduler mid-slice readiness or places prerequisite changed");
    }
}
#endif
