// Packet length guard checks (SF9). Run: dotnet run --project tools/network_tests
// The parse loop below follows PacketHandlers.ParsePackets step for step, with
// the engine-bound parts (socket, plugins, handlers) replaced by a length table
// and a list of what was dispatched.
using GUO.Network;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("FAIL: " + message); }

const byte Dyn = 0xB0, Static3 = 0x73, Static5 = 0x22;
short LengthOf(byte id) => id switch { Static3 => 3, Static5 => 5, _ => -1 };

// Mirrors GetPacketInfo.
bool Info(CircularBuffer s, out byte id, out int offset, out int len)
{
    id = 0xFF; offset = 0; len = 0;
    if (s.Length <= 0) return false;
    len = LengthOf(id = s[0]);
    offset = 1;
    if (len == -1)
    {
        if (s.Length < 3) return false;
        len = (s[1] << 8) | s[2];
        offset = 3;
    }
    return true;
}

// Mirrors ParsePackets; the iteration cap turns a spin into a failure.
List<byte[]> Parse(CircularBuffer s, out int dropped)
{
    var got = new List<byte[]>();
    var buf = new byte[4096];
    dropped = 0;
    for (int guard = 0; s.Length > 0; guard++)
    {
        if (guard > 1000) throw new Exception("FAIL: parse loop spins");
        if (!Info(s, out _, out int offset, out int len)) break;
        if (PacketLengthGuard.RejectShortDynamic(s, offset, len)) { dropped++; continue; }
        if (s.Length < len) break;
        s.Dequeue(buf, 0, len);
        got.Add(buf.AsSpan(0, len).ToArray());
    }
    return got;
}

CircularBuffer Of(params byte[] bytes) { var s = new CircularBuffer(); s.Enqueue(bytes); return s; }

// Lengths 0, 1 and 2 are each dropped with their header, and the packet after is still read.
foreach (byte bad in new byte[] { 0, 1, 2 })
{
    var s = Of(Dyn, 0x00, bad, Static3, 0xAA, 0xBB);
    var got = Parse(s, out int dropped);
    Check(dropped == 1, $"len {bad}: one packet dropped");
    Check(got.Count == 1 && got[0].SequenceEqual(new byte[] { Static3, 0xAA, 0xBB }), $"len {bad}: next packet parsed");
    Check(s.Length == 0, $"len {bad}: stream drained");
}

// The guard itself: consumes exactly the 3 header bytes, and only for a short dynamic length.
{
    var s = Of(Dyn, 0x00, 0x00, 0x11, 0x22);
    Check(PacketLengthGuard.RejectShortDynamic(s, 3, 0), "dynamic len 0 rejected");
    Check(s.Length == 2 && s[0] == 0x11, "exactly the header consumed");
    Check(!PacketLengthGuard.RejectShortDynamic(s, 3, 3), "dynamic len 3 (header only) accepted");
    Check(!PacketLengthGuard.RejectShortDynamic(s, 1, 1), "static packets never rejected");
    Check(s.Length == 2, "nothing consumed when accepted");
}

// A run of bad headers back to back does not spin and does not swallow good packets.
{
    var s = Of(Dyn, 0, 0, Dyn, 0, 1, Dyn, 0, 2, Static5, 1, 2, 3, 4, Dyn, 0, 4, 0x99);
    var got = Parse(s, out int dropped);
    Check(dropped == 3, "three bad headers dropped");
    Check(got.Count == 2 && got[0][0] == Static5 && got[1].SequenceEqual(new byte[] { Dyn, 0, 4, 0x99 }), "good packets intact");
}

// Valid traffic is unchanged: a minimal dynamic packet, and a partial one waits for more data.
{
    var s = Of(Dyn, 0, 3, Dyn, 0, 8, 1, 2);
    var got = Parse(s, out int dropped);
    Check(dropped == 0 && got.Count == 1 && got[0].Length == 3, "header-only dynamic packet read");
    Check(s.Length == 5, "partial packet left in the stream");
    s.Enqueue(new byte[] { 3, 4, 5 });
    got = Parse(s, out _);
    Check(got.Count == 1 && got[0].Length == 8 && s.Length == 0, "partial packet read once complete");
}

// A short header split across reads waits instead of being judged early.
{
    var s = Of(Dyn, 0);
    var got = Parse(s, out int dropped);
    Check(dropped == 0 && got.Count == 0 && s.Length == 2, "two header bytes wait");
    s.Enqueue(new byte[] { 0, Static3, 7, 8 });
    got = Parse(s, out dropped);
    Check(dropped == 1 && got.Count == 1 && got[0][0] == Static3, "completed bad header dropped, next read");
}

// Wrap-around inside the ring buffer: the header straddles the end of the array.
{
    var s = new CircularBuffer(8);
    s.Enqueue(new byte[] { Static5, 1, 2, 3, 4, Static3, 9 });
    var got = Parse(s, out _);
    Check(got.Count == 1 && s.Length == 2, "set up wrap");
    s.Enqueue(new byte[] { 9, Dyn, 0, 1, Static3, 5, 6 });
    got = Parse(s, out int dropped);
    Check(dropped == 1 && got.Count == 2 && got[1].SequenceEqual(new byte[] { Static3, 5, 6 }), "guard across wrap-around");
}

Console.WriteLine($"PASS: {checks} packet length guard checks (short, malformed, split and wrapped headers).");
