using System.Diagnostics;
using CentrED.MapGen.IR;

namespace CentrED.MapGen.Pipeline;

/// <summary>Thrown by <see cref="PipelineRunner.Validate"/> when a pass reads a field no prior enabled pass writes.</summary>
public sealed class PipelineValidationException : Exception
{
    public PipelineValidationException(string message) : base(message) { }
}

/// <summary>
/// Executes a list of pipeline steps against an IR. Validates Reads/Writes dependencies before
/// running so a pipeline that, e.g., enables BiomeAssign while NoiseHeight is off fails fast
/// with a clear error instead of producing garbage. RNG per step is deterministic and
/// toggle-stable: stream seed = ir.Seed XOR (passIndex * golden ratio prime).
/// </summary>
public sealed class PipelineRunner
{
    /// <summary>Runs each enabled step in order. Pre-allocates each pass's Writes fields, builds a deterministic per-step RNG, and records timing into <see cref="GenIR.Reports"/>.</summary>
    public void Run(GenIR ir, IReadOnlyList<PipelineStep> steps, CancellationToken cancel = default)
        => RunRange(ir, steps, 0, steps.Count, cancel);

    /// <summary>
    /// Runs steps <c>[from, to)</c> against <paramref name="ir"/>. The IR must already hold the state
    /// after step <c>from - 1</c> (a fresh IR when <paramref name="from"/> is 0). Validation covers the
    /// prefix <c>[0, to)</c> so a slice that reads fields written by earlier steps is accepted. The
    /// per-step RNG depends only on the step index, so running 0..n in one call or in slices gives
    /// identical results. <paramref name="onStep"/> is called before each enabled step runs.
    /// </summary>
    public void RunRange(GenIR ir, IReadOnlyList<PipelineStep> steps, int from, int to,
        CancellationToken cancel = default, Action<int, PipelineStep>? onStep = null)
    {
        from = Math.Clamp(from, 0, steps.Count);
        to = Math.Clamp(to, from, steps.Count);
        Validate(steps.Take(to).ToList());

        for (int i = from; i < to; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var step = steps[i];
            if (!step.Enabled) continue;
            onStep?.Invoke(i, step);
            RunStep(ir, step, i, cancel);
        }

        ir.PassVersion++;
    }

    /// <summary>Deterministic per-step RNG: seed = ir.Seed XOR ((index + 1) * golden ratio prime). Toggling step 5 doesn't reshuffle step 9.</summary>
    public static Random CreateStepRng(ulong irSeed, int stepIndex)
    {
        ulong streamSeed = irSeed ^ ((ulong)(stepIndex + 1) * 0x9E3779B97F4A7C15UL);
        return new Random(unchecked((int)(streamSeed ^ (streamSeed >> 32))));
    }

    private static void RunStep(GenIR ir, PipelineStep step, int index, CancellationToken cancel)
    {
        // Pre-allocate any dense fields the pass writes so passes can address them
        // without each one repeating EnsureX().
        ir.EnsureField(step.Pass.Writes);

        var report = new PassReport { PassName = step.Pass.Name };
        ir.Reports[step.Pass.Name] = report;

        var ctx = new GenContext
        {
            IR = ir,
            Rng = CreateStepRng(ir.Seed, index),
            Report = report,
            Cancellation = cancel,
        };

        var sw = Stopwatch.StartNew();
        try
        {
            step.Pass.Run(ctx, step.Parameters);
        }
        finally
        {
            sw.Stop();
            report.Elapsed = sw.Elapsed;
        }
    }

    /// <summary>
    /// Walks the steps in order and ensures that for every enabled step, every flag in Reads
    /// has been written by some prior enabled step. Sparse layers (StaticOps/Rivers/Roads/Pois)
    /// start empty, which is a valid "no input" state and is exempt from the check.
    /// </summary>
    /// <exception cref="PipelineValidationException">A dense Reads field has no prior writer.</exception>
    public static void Validate(IReadOnlyList<PipelineStep> steps)
    {
        IrFields written = IrFields.None;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!step.Enabled) continue;

            var missing = step.Pass.Reads & ~written;
            // Sparse layers (StaticOps/Rivers/Roads/Pois) start empty; missing is fine.
            missing &= ~(IrFields.StaticOps | IrFields.Rivers | IrFields.Roads | IrFields.Pois);
            if (missing != IrFields.None)
            {
                throw new PipelineValidationException(
                    $"Pass '{step.Pass.Name}' (step {i}) reads {missing} but no enabled prior pass writes those fields.");
            }
            written |= step.Pass.Writes;
        }
    }
}
