namespace CentrED.MapGen.IR;

public enum StaticOpKind : byte
{
    Add = 0,
    Remove = 1,
}

// One mutation against the static layer. World-absolute X/Y (not block-local) — the committer
// converts to block coordinates when emitting CentrED packets.
public readonly record struct StaticOp(
    StaticOpKind Kind,
    ushort X,
    ushort Y,
    sbyte Z,
    ushort Id,
    ushort Hue);
