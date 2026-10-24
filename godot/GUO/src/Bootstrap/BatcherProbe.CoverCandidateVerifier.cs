// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.Scenes;

namespace GUO.Host;

public partial class BatcherProbe
{
    private sealed class CoverVerifierSource(Chunk chunk) : IChunkSource<Chunk>
    {
        public Chunk ChunkAt(int x, int y) => x == 0 && y == 0 ? chunk : null;
    }

    private void VerifyCoverCandidateVerifier()
    {
        var fence = new ParityDrawFence(100, 1000);
        CheckThat("parity engine signals cannot substitute for client draws",
            !fence.Observe(100, 1, true, true) && !fence.Observe(100, 2, true, true)
            && !fence.Observe(101, 3, true, true) && fence.Observe(102, 4, true, true),
            "suppressed, first and second actual frame");
        bool timedOut = false, wrongState = false, resetCounter = false;
        try { fence.Observe(101, 1000, true, true); } catch (TimeoutException) { timedOut = true; }
        try { fence.Observe(102, 4, false, true); } catch (InvalidOperationException) { wrongState = true; }
        try { fence.Observe(99, 4, true, true); } catch (InvalidOperationException) { resetCounter = true; }
        CheckThat("parity draw fence fails closed", timedOut && wrongState && resetCounter,
            "deadline, state drift and reset counter");
        bool lateDraw = false, noSignal = false, lateSignal = false;
        try { fence.Observe(102, 1001, true, true); } catch (TimeoutException) { lateDraw = true; }
        try { fence.WaitForSignal(System.Threading.Tasks.Task.CompletedTask, 1000).GetAwaiter().GetResult(); }
        catch (TimeoutException) { lateSignal = true; }
        var absent = new System.Threading.Tasks.TaskCompletionSource();
        try { fence.WaitForSignal(absent.Task, 999).GetAwaiter().GetResult(); }
        catch (TimeoutException) { noSignal = true; }
        CheckThat("parity capture deadline includes absent and late signals", lateDraw && noSignal && lateSignal,
            "two late draws cannot bypass deadline; missing signal cannot await forever");
        long plans = CoverCandidateVerifier.Plans, references = CoverCandidateVerifier.References,
            mismatches = CoverCandidateVerifier.Mismatches;
        var chunk = new Chunk(FirstLandFixture<World>());
        var source = new CoverVerifierSource(chunk);
        var a = FirstLandFixture<Land>();
        var b = FirstLandFixture<Land>();
        var expected = new[] { new CoverCandidates.Candidate(a, 1), new CoverCandidates.Candidate(b, -1) };
        void Verify(CoverCandidates.Candidate[] value) => CoverCandidateVerifier.Check(source, 0, 0, 1, 1, 1, -1, value);
        void Reject(string label, CoverCandidates.Candidate[] value)
        {
            long before = CoverCandidateVerifier.Mismatches;
            bool failed = false;
            try { Verify(value); }
            catch (InvalidOperationException) { failed = true; }
            CheckThat("cover verifier rejects " + label, failed && CoverCandidateVerifier.Mismatches == before + 1,
                "fresh raw linked-list oracle");
        }
        try
        {
            chunk.AddGameObject(a, 1, 0);
            chunk.AddGameObject(b, 0, 1);
            Verify(expected);
            CheckThat("cover verifier accepts exact live identities and columns",
                CoverCandidateVerifier.Plans == plans + 1 && CoverCandidateVerifier.References == references + 2
                && CoverCandidateVerifier.Mismatches == mismatches, "two ordered occurrences");
            a.AlphaHue = 0; b.Z = 100;
            Verify(expected);
            CheckThat("cover verifier leaves live visual predicates outside identity cache",
                CoverCandidateVerifier.Mismatches == mismatches, "alpha/depth do not alter identity plan");
            Reject("missing occurrence", new[] { expected[0] });
            Reject("extra occurrence", new[] { expected[0], expected[1], expected[0] });
            Reject("wrong order", new[] { expected[1], expected[0] });
            Reject("wrong column", new[] { expected[0] with { Column = -1 }, expected[1] });
            // Deliberately bypass production invalidation to prove this oracle
            // reads the raw chain instead of agreeing with a stale identity cache.
            chunk.GetFirstLand(1, 0);
            chunk.Tiles[1, 0] = b;
            try { Reject("stale first-Land cache", expected); }
            finally { chunk.Tiles[1, 0] = a; }
        }
        catch (Exception ex) { CheckThat("cover verifier unexpected exception", false, ex.ToString()); }
        finally
        {
            chunk.ClearForReload(); chunk.Mesh.Clear();
            CoverCandidateVerifier.Plans = plans; CoverCandidateVerifier.References = references;
            CoverCandidateVerifier.Mismatches = mismatches;
        }
    }
}
