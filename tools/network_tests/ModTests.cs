// Packet mod checks (MOD-02, ADR-0038). Run with the length guard checks:
// dotnet run --project tools/network_tests
// The parse loop follows PacketHandlers.ParsePackets with the two mod seams in
// place: GetPacketLength asks the registry first, AnalyzePacket offers the
// packet to the registry before the client's handler table.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using GUO.ModApi;
using GUO.Network;
using GUO.Network.Mods;
using GuoMods.Announce;

static class ModTests
{
    static int _checks;
    static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception("FAIL: mods: " + message); }

    static void Throws(Action action, string contains, string message)
    {
        try { action(); }
        catch (ModException ex) { Check(ex.Message.Contains(contains), $"{message} (said: {ex.Message})"); return; }
        Check(false, message + " (no refusal)");
    }

    // A slice of the client's tables: 0x1B and 0x73 are handled, 0xF9 is not.
    static readonly Dictionary<byte, short> BaseLengths = new() { [0x1B] = 0x25, [0x73] = 2, [0xF9] = 3 };
    static bool BaseHandled(byte id) => id is 0x1B or 0x73 or 0xBF;
    static short BaseLength(byte id) => BaseLengths.TryGetValue(id, out short l) ? l : (short)-1;

    sealed class Context : IModContext
    {
        public readonly List<(string Text, ushort Hue)> Messages = new();
        public uint ClientVersion => Version;
        public void SystemMessage(string text, ushort hue = 946) => Messages.Add((text, hue));
        public void Send(ReadOnlySpan<byte> packet) { }
        public void Log(string message) { }
    }

    const uint Version = (7u << 24) | (0u << 16) | (15u << 8) | 1u;

    static ModManifest Manifest(string id, string packets, string shards = "[\"127.0.0.1\"]") => ModManifest.Parse(
        "{\"schema\":\"guo/mod@1\",\"id\":\"" + id + "\",\"version\":\"1.0.0\",\"title\":\"t\",\"author\":\"a\",\"abi\":1," +
        "\"assembly\":\"M.dll\",\"entry\":\"M\",\"shards\":" + shards + ",\"packets\":[" + packets + "]}", null);

    // Mirrors ParsePackets + GetPacketInfo + AnalyzePacket with the seams.
    static List<byte[]> Parse(CircularBuffer s, PacketModRegistry mods, out List<byte[]> toMods)
    {
        var client = new List<byte[]>();
        toMods = new List<byte[]>();
        var buf = new byte[4096];
        for (int guard = 0; s.Length > 0; guard++)
        {
            if (guard > 1000) throw new Exception("FAIL: mods: parse loop spins");
            byte id = s[0];
            short len = mods != null && mods.TryGetLength(id, out short m) ? m : BaseLength(id);
            int offset = 1;
            if (len == -1)
            {
                if (s.Length < 3) break;
                len = (short)((s[1] << 8) | s[2]);
                offset = 3;
            }
            if (PacketLengthGuard.RejectShortDynamic(s, offset, len)) continue;
            if (s.Length < len) break;
            s.Dequeue(buf, 0, len);
            byte[] packet = buf.AsSpan(0, len).ToArray();
            if (mods != null && mods.TryDispatch(packet)) toMods.Add(packet);
            else client.Add(packet);
        }
        return client;
    }

    static CircularBuffer Of(params byte[][] packets)
    {
        var s = new CircularBuffer();
        foreach (byte[] p in packets) s.Enqueue(p);
        return s;
    }

    public static int Run()
    {
        byte[] ping = { 0x73, 0x01 };
        byte[] announce = AnnounceMod.Build("Server restart in 5 minutes", 0x35);

        // Off: no registry, so 0xF9 is framed by the client's table (3 bytes) and the stream
        // after it is misread. That is upstream's behaviour and the reason mods exist.
        {
            var got = Parse(Of(announce, ping), null, out var toMods);
            Check(toMods.Count == 0, "off: nothing reaches a mod");
            Check(got.Count > 0 && got[0].Length == 3 && got[0][0] == 0xF9, "off: 0xF9 framed by the client's length, as upstream");
        }

        // The sample mod, loaded from its DLL the way the client loads it.
        string root = Path.Combine(Path.GetTempPath(), "guo-mods-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string folder = Path.Combine(root, "announce");
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Announce.dll"), Path.Combine(folder, "Announce.dll"));
            File.Copy(Path.Combine(SourceDir(), "..", "..", "docs", "modding", "samples", "announce", "mod.json"), Path.Combine(folder, "mod.json"));

            string host = "127.0.0.1";
            var context = new Context();
            var registry = new PacketModRegistry(Version, BaseHandled, e => throw new Exception("FAIL: mods: " + e)) { ShardHost = () => host };
            var results = ModLoader.LoadAll(root, registry, _ => context);
            Check(results.Count == 1 && results[0].Loaded && results[0].Packets == 1, "sample loads from its DLL: " + string.Join("; ", results.Select(r => r.Message)));

            var got = Parse(Of(announce, ping), registry, out var toMods);
            Check(toMods.Count == 1 && toMods[0].SequenceEqual(announce), "on: 0xF9 framed by the mod's length and handed to it");
            Check(got.Count == 1 && got[0].SequenceEqual(ping), "on: the packet after it still reaches the client");
            Check(context.Messages.Count == 1 && context.Messages[0] == ("Server restart in 5 minutes", 0x35), "on: the mod printed the announcement");

            // Another shard: the mod is inactive and parsing is the client's again.
            host = "play.example.org";
            Parse(Of(announce), registry, out toMods);
            Check(toMods.Count == 0 && !registry.TryGetLength(0xF9, out _), "other shard: the mod's packet is not active");
            host = "127.0.0.1";

            // A second copy of the same mod is refused whole.
            string twin = Path.Combine(root, "announce2");
            Directory.CreateDirectory(twin);
            File.Copy(Path.Combine(folder, "Announce.dll"), Path.Combine(twin, "Announce.dll"));
            File.WriteAllText(Path.Combine(twin, "mod.json"), File.ReadAllText(Path.Combine(folder, "mod.json")).Replace("\"announce\"", "\"announce-two\""));
            var again = ModLoader.LoadAll(root, new PacketModRegistry(Version, BaseHandled, null), _ => context);
            Check(again.Count == 2 && again[0].Loaded && !again[1].Loaded && again[1].Message.Contains("already registered by announce"), "two mods claiming 0xF9: the second is refused");

            // ABI and assembly checks.
            File.WriteAllText(Path.Combine(twin, "mod.json"), File.ReadAllText(Path.Combine(folder, "mod.json")).Replace("\"announce\"", "\"announce-two\"").Replace("\"abi\": 1", "\"abi\": 2"));
            again = ModLoader.LoadAll(root, new PacketModRegistry(Version, BaseHandled, null), _ => context);
            Check(!again[1].Loaded && again[1].Message.Contains("ABI 2"), "a mod for another ABI is refused");
            File.Delete(Path.Combine(twin, "Announce.dll"));
            File.WriteAllText(Path.Combine(twin, "mod.json"), File.ReadAllText(Path.Combine(folder, "mod.json")).Replace("\"announce\"", "\"announce-two\""));
            again = ModLoader.LoadAll(root, new PacketModRegistry(Version, BaseHandled, null), _ => context);
            Check(!again[1].Loaded && again[1].Message.Contains("missing"), "a mod without its assembly is refused");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // Registration rules, without a DLL.
        {
            var registry = new PacketModRegistry(Version, BaseHandled, null) { ShardHost = () => "127.0.0.1" };
            PacketHandler nothing = (_, _) => { };

            var m = Manifest("rules", "{\"id\":\"0xF8\",\"length\":7}");
            Throws(() => registry.For(m, new Context()).AddPacket(new PacketRegistration { Id = 0xF7, Length = 7, Handler = nothing }), "not declared", "undeclared id refused");
            Throws(() => registry.For(m, new Context()).AddPacket(new PacketRegistration { Id = 0xF8, Length = 9, Handler = nothing }), "does not match", "length differing from mod.json refused");
            Throws(() => registry.For(m, new Context()).AddPacket(new PacketRegistration { Id = 0xF8, Length = 7 }), "no handler", "missing handler refused");

            var takeover = Manifest("takeover", "{\"id\":\"0x73\",\"length\":2}");
            Throws(() => registry.For(takeover, new Context()).AddPacket(new PacketRegistration { Id = 0x73, Length = 2, Handler = nothing }), "override", "replacing a client handler without override refused");
            var fake = Manifest("fake", "{\"id\":\"0xF8\",\"length\":7,\"override\":true}");
            Throws(() => registry.For(fake, new Context()).AddPacket(new PacketRegistration { Id = 0xF8, Length = 7, Override = true, Handler = nothing }), "does not handle", "override of an unhandled id refused");

            var over = Manifest("over", "{\"id\":\"0x73\",\"length\":4,\"override\":true}");
            var r = registry.For(over, new Context());
            r.AddPacket(new PacketRegistration { Id = 0x73, Length = 4, Override = true, Handler = nothing });
            Check(r.Commit() == 1 && registry.TryGetLength(0x73, out short l) && l == 4, "a declared override takes the id and its length");

            var sub = Manifest("sub", "{\"id\":\"0xBF\",\"subcommand\":\"0x99\"},{\"id\":\"0xBF\",\"subcommand\":\"0x14\"}");
            r = registry.For(sub, new Context());
            var hits = new List<int>();
            r.AddPacket(new PacketRegistration { Id = 0xBF, Subcommand = 0x99, Handler = (_, p) => hits.Add(p.Length) });
            Throws(() => r.AddPacket(new PacketRegistration { Id = 0xBF, Subcommand = 0x14, Handler = nothing }), "already handles", "a client subcommand needs override");
            r.Commit();
            Check(!registry.TryGetLength(0xBF, out _), "a subcommand leaves 0xBF's framing alone");
            Check(registry.TryDispatch(new byte[] { 0xBF, 0, 6, 0, 0x99, 1 }) && hits.SequenceEqual(new[] { 6 }), "a registered 0xBF subcommand reaches the mod");
            Check(!registry.TryDispatch(new byte[] { 0xBF, 0, 5, 0, 0x14 }), "other subcommands stay with the client");
            Throws(() => registry.For(Manifest("badsub", "{\"id\":\"0xF0\",\"subcommand\":1}"), new Context())
                .AddPacket(new PacketRegistration { Id = 0xF0, Subcommand = 1, Handler = nothing }), "needs id 0xBF", "a subcommand on another id refused");

            var old = Manifest("old", "{\"id\":\"0xF6\",\"length\":5}");
            r = registry.For(old, new Context());
            r.AddPacket(new PacketRegistration { Id = 0xF6, Length = 5, MaxClientVersion = (6u << 24), Handler = nothing });
            Check(r.Commit() == 0 && r.Skipped == 1 && !registry.TryGetLength(0xF6, out _), "client-version guard: a registration for older clients is skipped");

            var boom = Manifest("boom", "{\"id\":\"0xF5\",\"length\":3}");
            var errors = new List<string>();
            var loud = new PacketModRegistry(Version, BaseHandled, errors.Add) { ShardHost = () => "127.0.0.1" };
            r = loud.For(boom, new Context());
            r.AddPacket(new PacketRegistration { Id = 0xF5, Length = 3, Handler = (_, _) => throw new FormatException("bad") });
            r.Commit();
            Check(loud.TryDispatch(new byte[] { 0xF5, 0, 0 }) && errors.Count == 1 && errors[0].Contains("boom") && errors[0].Contains("bad"), "a throwing handler drops its packet and is logged");

            var anywhere = Manifest("anywhere", "{\"id\":\"0xFC\",\"length\":3}", "[\"*\"]");
            var everywhere = new PacketModRegistry(Version, BaseHandled, null) { ShardHost = () => "any.host" };
            r = everywhere.For(anywhere, new Context());
            r.AddPacket(new PacketRegistration { Id = 0xFC, Length = 3, Handler = nothing });
            r.Commit();
            Check(everywhere.TryGetLength(0xFC, out _), "shards [\"*\"] applies everywhere");
        }

        // mod.json is strict.
        Throws(() => ModManifest.Parse("{\"schema\":\"guo/mod@1\",\"id\":\"x\",\"typo\":1}", null), "unknown field", "unknown top-level field refused");
        Throws(() => Manifest("x", "{\"id\":\"0xF9\",\"lenght\":3}"), "unknown field", "unknown packet field refused");
        Throws(() => Manifest("x", "{\"id\":\"0x1F9\"}"), "0..0xFF", "id over 0xFF refused");
        Throws(() => Manifest("x", "{\"id\":\"0xF9\"},{\"id\":249}"), "declared twice", "duplicate declaration refused");
        Throws(() => Manifest("x", "{\"id\":\"0xF9\"}", "[]"), "shards", "an empty shards list refused");
        Throws(() => Manifest("Bad Id", "{\"id\":\"0xF9\"}"), "id must be", "a bad mod id refused");
        Throws(() => ModManifest.Parse(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(
            "{\"schema\":\"guo/mod@1\",\"id\":\"x\",\"version\":\"1.0.0\",\"title\":\"t\",\"author\":\"a\",\"abi\":1,\"assembly\":\"..\\\\M.dll\",\"entry\":\"M\",\"shards\":[\"*\"],\"packets\":[{\"id\":1}]}")), null),
            "assembly must be", "an assembly path outside the folder refused");

        // The subcommands the registry treats as the client's match ExtendedCommand's switch.
        {
            string handlers = File.ReadAllText(Path.Combine(SourceDir(), "..", "..", "godot", "GUO", "src", "Network", "PacketHandlers.cs")).Replace("\r\n", "\n");
            int start = handlers.IndexOf("private static void ExtendedCommand(", StringComparison.Ordinal);
            int end = handlers.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            if (end < 0) end = handlers.IndexOf("\n        }\r\n", start, StringComparison.Ordinal);
            var cases = Regex.Matches(handlers.Substring(start, end - start), @"^ {16}case (0x[0-9A-Fa-f]+|\d+):", RegexOptions.Multiline)
                .Select(x => (ushort)(x.Groups[1].Value.StartsWith("0x") ? Convert.ToInt32(x.Groups[1].Value, 16) : int.Parse(x.Groups[1].Value)))
                .ToHashSet();
            Check(cases.Count > 10 && cases.SetEquals(PacketModRegistry.ClientSubcommands),
                "ClientSubcommands matches ExtendedCommand: " + string.Join(",", cases.Except(PacketModRegistry.ClientSubcommands).Concat(PacketModRegistry.ClientSubcommands.Except(cases)).Select(c => $"0x{c:X}")));
        }

        return _checks;
    }

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
}
