using System.Security.Cryptography;
using CentrED.MapGen;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class PipelineDeterminismTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalIR()
    {
        var ir1 = RunFixture(seed: 12345);
        var ir2 = RunFixture(seed: 12345);

        Assert.Equal(Digest(ir1.Height_Z!), Digest(ir2.Height_Z!));
        Assert.Equal(Digest(ir1.Biome!),    Digest(ir2.Biome!));
        Assert.Equal(Digest(ir1.LandId!),   Digest(ir2.LandId!));
        Assert.Equal(ir1.StaticOps.Count,   ir2.StaticOps.Count);
        Assert.Equal(ir1.Pois.Count,        ir2.Pois.Count);
        Assert.Equal(ir1.Rivers.Count,      ir2.Rivers.Count);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentHeight()
    {
        var ir1 = RunFixture(seed: 1);
        var ir2 = RunFixture(seed: 2);
        Assert.NotEqual(Digest(ir1.Height_Z!), Digest(ir2.Height_Z!));
    }

    [Fact]
    public void Validator_RejectsBrokenPipeline_WhenUpstreamWriteIsDisabled()
    {
        var steps = DefaultPipeline.Build();
        // Disable Noise Height — Hydraulic Erosion reads Height, should fail validation.
        steps[0].Enabled = false;
        Assert.Throws<PipelineValidationException>(() => PipelineRunner.Validate(steps));
    }

    [Fact]
    public void DisablingEverythingDownstream_StillValid()
    {
        var steps = DefaultPipeline.Build();
        for (int i = 1; i < steps.Count; i++) steps[i].Enabled = false;
        // Just NoiseHeight enabled (no Reads) — must pass.
        var ex = Record.Exception(() => PipelineRunner.Validate(steps));
        Assert.Null(ex);
    }

    private static GenIR RunFixture(long seed)
    {
        const ushort dim = 256;
        var ir = new GenIR(dim, dim, new RectU16(0, 0, dim - 1, dim - 1), unchecked((ulong)seed));
        var steps = DefaultPipeline.Build();
        new PipelineRunner().Run(ir, steps);
        return ir;
    }

    private static string Digest<T>(T[] arr) where T : struct
    {
        var bytes = new byte[arr.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
