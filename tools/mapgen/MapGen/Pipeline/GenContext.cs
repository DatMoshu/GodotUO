using CentrED.MapGen.IR;

namespace CentrED.MapGen.Pipeline;

// Mutable context passed to every pass. Deterministic: each pass gets its own RNG derived
// from (IR.Seed, pass index) so toggling pass 5 on/off doesn't reshuffle pass 9's randomness.
public sealed class GenContext
{
    public required GenIR IR { get; init; }
    public required Random Rng { get; init; }       // pass-scoped, deterministic
    public required PassReport Report { get; init; }
    public CancellationToken Cancellation { get; init; }
}
