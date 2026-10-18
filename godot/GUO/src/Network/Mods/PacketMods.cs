// SPDX-License-Identifier: BSD-2-Clause
// Packet mods (ADR-0038): what the client consults, when "Load mods" is on,
// before its own length table and handler table. Engine-free on purpose:
// tools/network_tests compiles this file with the parse loop and the sample
// mod, without Godot. The client's side is ClientMods.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using GUO.ModApi;

namespace GUO.Network.Mods
{
    /// <summary>The installed mods' packets. <see cref="Current"/> is null unless the user turned
    /// "Load mods" on and at least one mod loaded; every seam checks it first, so with mods off the
    /// client parses exactly as upstream does.</summary>
    internal sealed class PacketModRegistry
    {
        public static PacketModRegistry Current { get; set; }

        /// <summary>The 0xBF subcommands PacketHandlers.ExtendedCommand handles itself. A mod
        /// taking one must declare an override. tools/network_tests keeps this list in step with
        /// the handler's switch.</summary>
        internal static readonly HashSet<ushort> ClientSubcommands = new()
        {
            0x00, 0x01, 0x02, 0x04, 0x06, 0x08, 0x0C, 0x10, 0x11, 0x14, 0x16, 0x18, 0x19, 0x1B,
            0x1D, 0x20, 0x21, 0x22, 0x25, 0x26, 0x2A, 0x2B, 0xBEEF
        };

        /// <summary>0xBF subcommands GUO itself reserves; no mod may declare one. 0x4755 is the
        /// shard offer (guo/shard-offer@1, STORE-02).</summary>
        internal static readonly HashSet<ushort> ReservedSubcommands = new() { 0x4755 };

        private sealed class Entry
        {
            public ModManifest Mod;
            public PacketRegistration Registration;
            public IModContext Context;
        }

        private readonly Entry[] _byId = new Entry[0x100];
        private readonly Dictionary<ushort, Entry> _bySubcommand = new();
        private readonly Func<byte, bool> _baseHandled;
        private readonly Action<string> _error;
        private string _shardHost;
        private string _shardName;
        private bool[] _active = Array.Empty<bool>();

        public PacketModRegistry(uint clientVersion, Func<byte, bool> baseHandled, Action<string> error)
        {
            ClientVersion = clientVersion;
            _baseHandled = baseHandled;
            _error = error ?? (_ => { });
        }

        public uint ClientVersion { get; }

        /// <summary>The host of the login server the user chose. A mod's packets apply only when
        /// it, or <see cref="ShardName"/>, is one of the mod's <c>shards</c> (or the mod lists "*").</summary>
        public Func<string> ShardHost { get; set; } = () => string.Empty;

        /// <summary>The shard's name, when the client knows it; matched like the host.</summary>
        public Func<string> ShardName { get; set; } = () => string.Empty;

        public List<ModManifest> Mods { get; } = new();

        public int Count => _byId.Count(e => e != null) + _bySubcommand.Count;

        public bool TryGetLength(byte id, out short length)
        {
            Entry e = _byId[id];
            if (e != null && IsActive(e.Mod))
            {
                length = e.Registration.Length;
                return true;
            }
            length = 0;
            return false;
        }

        /// <summary>Runs the mod handler for a framed packet, if one is registered and active.
        /// True means the packet was the mod's, handled or dropped on an exception; the client's
        /// own handler must not run.</summary>
        public bool TryDispatch(ReadOnlySpan<byte> packet)
        {
            if (packet.IsEmpty)
                return false;

            Entry e = null;
            if (packet[0] == 0xBF && packet.Length >= 5)
                _bySubcommand.TryGetValue((ushort)((packet[3] << 8) | packet[4]), out e);
            e ??= _byId[packet[0]];

            if (e == null || !IsActive(e.Mod))
                return false;

            try
            {
                e.Registration.Handler(e.Context, packet);
            }
            catch (Exception ex)
            {
                _error($"[mods] {e.Mod.Id}: dropped packet 0x{packet[0]:X2} ({packet.Length} bytes): {ex.GetType().Name}: {ex.Message}");
            }
            return true;
        }

        private bool IsActive(ModManifest mod)
        {
            string host = ShardHost() ?? string.Empty;
            string shard = ShardName() ?? string.Empty;
            if (!ReferenceEquals(host, _shardHost) || !ReferenceEquals(shard, _shardName) || _active.Length != Mods.Count)
            {
                _shardHost = host;
                _shardName = shard;
                _active = Mods.Select(m => m.AppliesTo(host) || (shard.Length > 0 && m.AppliesTo(shard))).ToArray();
            }
            int i = Mods.IndexOf(mod);
            return i >= 0 && _active[i];
        }

        /// <summary>A registrar for one mod. Nothing is committed until <see cref="Registrar.Commit"/>,
        /// so a mod whose Register throws leaves no packet behind.</summary>
        public Registrar For(ModManifest mod, IModContext context) => new Registrar(this, mod, context);

        internal sealed class Registrar : IModRegistrar
        {
            private readonly PacketModRegistry _owner;
            private readonly ModManifest _mod;
            private readonly IModContext _context;
            private readonly List<Entry> _pending = new();

            public Registrar(PacketModRegistry owner, ModManifest mod, IModContext context)
            {
                _owner = owner;
                _mod = mod;
                _context = context;
            }

            public string ModId => _mod.Id;
            public uint ClientVersion => _owner.ClientVersion;
            public int Skipped { get; private set; }

            public void AddPacket(PacketRegistration r)
            {
                if (r == null || r.Handler == null)
                    throw new ModException($"{_mod.Id}: a packet registration has no handler");

                string name = r.Subcommand is ushort s ? $"0xBF subcommand 0x{s:X2}" : $"packet 0x{r.Id:X2}";

                if (r.Subcommand != null && r.Id != 0xBF)
                    throw new ModException($"{_mod.Id}: {name}: a subcommand needs id 0xBF");
                if (r.Subcommand == null && r.Length != -1 && r.Length < 1)
                    throw new ModException($"{_mod.Id}: {name}: length {r.Length} is neither -1 nor 1..32767");

                ModPacket declared = _mod.Packets.FirstOrDefault(p => p.Id == r.Id && p.Subcommand == r.Subcommand);
                if (declared == null)
                    throw new ModException($"{_mod.Id}: {name} is not declared in mod.json");
                if (declared.Override != r.Override || (r.Subcommand == null && declared.Length != r.Length))
                    throw new ModException($"{_mod.Id}: {name} does not match its mod.json declaration");

                uint v = _owner.ClientVersion;
                if ((r.MinClientVersion != 0 && v < r.MinClientVersion) || (r.MaxClientVersion != 0 && v > r.MaxClientVersion))
                {
                    Skipped++;
                    return;
                }

                bool clientHas = r.Subcommand is ushort sub ? ClientSubcommands.Contains(sub) : _owner._baseHandled(r.Id);
                if (clientHas && !r.Override)
                    throw new ModException($"{_mod.Id}: the client already handles {name}; declare \"override\": true to replace it");
                if (!clientHas && r.Override)
                    throw new ModException($"{_mod.Id}: {name} is declared as an override, but the client does not handle it");

                Entry taken = r.Subcommand is ushort k ? (_owner._bySubcommand.TryGetValue(k, out var t) ? t : null) : _owner._byId[r.Id];
                taken ??= _pending.FirstOrDefault(p => p.Registration.Id == r.Id && p.Registration.Subcommand == r.Subcommand);
                if (taken != null)
                    throw new ModException($"{_mod.Id}: {name} is already registered by {taken.Mod.Id}");

                _pending.Add(new Entry { Mod = _mod, Registration = r, Context = _context });
            }

            public int Commit()
            {
                foreach (Entry e in _pending)
                {
                    if (e.Registration.Subcommand is ushort s)
                        _owner._bySubcommand[s] = e;
                    else
                        _owner._byId[e.Registration.Id] = e;
                }
                _owner.Mods.Add(_mod);
                _owner._active = Array.Empty<bool>();
                return _pending.Count;
            }
        }
    }

    internal sealed class ModException : Exception
    {
        public ModException(string message) : base(message) { }
    }

    internal sealed class ModPacket
    {
        public byte Id;
        public ushort? Subcommand;
        public short Length = -1;
        public bool Override;
    }

    /// <summary><c>mod.json</c>, schema <c>guo/mod@1</c> (docs/data_formats.md section 41). Unknown
    /// fields are refused, so a typo cannot silently drop a declaration.</summary>
    internal sealed class ModManifest
    {
        public const string Schema = "guo/mod@1";
        private static readonly Regex IdPattern = new(@"\A[a-z0-9][a-z0-9-]{0,63}\z");
        private static readonly Regex VersionPattern = new(@"\A\d{1,5}\.\d{1,5}\.\d{1,5}\z");
        private static readonly Regex AssemblyPattern = new(@"\A[A-Za-z0-9_.-]{1,96}\.dll\z");
        private static readonly string[] Fields = { "schema", "id", "version", "title", "author", "licence", "abi", "assembly", "entry", "shards", "packets" };
        private static readonly string[] PacketFields = { "id", "subcommand", "length", "override" };

        public string Id, Version, Title, Author, Licence, Assembly, Entry;
        public int Abi;
        public List<string> Shards = new();
        public List<ModPacket> Packets = new();
        public string Folder;

        public bool AppliesTo(string host) =>
            Shards.Any(s => s == "*" || string.Equals(s, host?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static ModManifest Parse(string json, string folder)
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            JsonElement root = doc.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, "mod.json is not an object");
            foreach (JsonProperty p in root.EnumerateObject())
                Require(Fields.Contains(p.Name), $"mod.json has an unknown field \"{p.Name}\"");

            Require(Str(root, "schema") == Schema, $"mod.json schema must be \"{Schema}\"");
            var m = new ModManifest
            {
                Id = Str(root, "id"),
                Version = Str(root, "version"),
                Title = Str(root, "title"),
                Author = Str(root, "author"),
                Licence = Str(root, "licence"),
                Assembly = Str(root, "assembly"),
                Entry = Str(root, "entry"),
                Folder = folder
            };
            Require(m.Id != null && IdPattern.IsMatch(m.Id), "mod.json id must be 1-64 of a-z, 0-9 and -");
            Require(m.Version != null && VersionPattern.IsMatch(m.Version), $"{m.Id}: version must be three numbers, like 1.0.0");
            Require(!string.IsNullOrWhiteSpace(m.Title) && m.Title.Length <= 100, $"{m.Id}: title is required (at most 100 characters)");
            Require(!string.IsNullOrWhiteSpace(m.Author) && m.Author.Length <= 100, $"{m.Id}: author is required (at most 100 characters)");
            Require(m.Licence == null || m.Licence.Length <= 64, $"{m.Id}: licence is at most 64 characters");
            Require(m.Assembly != null && AssemblyPattern.IsMatch(m.Assembly), $"{m.Id}: assembly must be a .dll file name in the mod's folder");
            Require(!string.IsNullOrWhiteSpace(m.Entry) && m.Entry.Length <= 200, $"{m.Id}: entry must name the mod's IGuoMod type");
            Require(root.TryGetProperty("abi", out JsonElement abi) && abi.ValueKind == JsonValueKind.Number && abi.TryGetInt32(out m.Abi), $"{m.Id}: abi is required");

            Require(root.TryGetProperty("shards", out JsonElement shards) && shards.ValueKind == JsonValueKind.Array && shards.GetArrayLength() is > 0 and <= 32,
                $"{m.Id}: shards must list 1-32 login hosts, or \"*\" for every shard");
            foreach (JsonElement s in shards.EnumerateArray())
            {
                Require(s.ValueKind == JsonValueKind.String && s.GetString().Trim().Length is > 0 and <= 253, $"{m.Id}: a shard is not a host name");
                m.Shards.Add(s.GetString().Trim());
            }

            Require(root.TryGetProperty("packets", out JsonElement packets) && packets.ValueKind == JsonValueKind.Array && packets.GetArrayLength() is > 0 and <= 64,
                $"{m.Id}: packets must declare 1-64 packets");
            foreach (JsonElement e in packets.EnumerateArray())
            {
                Require(e.ValueKind == JsonValueKind.Object, $"{m.Id}: a packet is not an object");
                foreach (JsonProperty p in e.EnumerateObject())
                    Require(PacketFields.Contains(p.Name), $"{m.Id}: a packet has an unknown field \"{p.Name}\"");
                var packet = new ModPacket
                {
                    Id = (byte)Number(e, "id", 0xFF, m.Id),
                    Subcommand = e.TryGetProperty("subcommand", out _) ? (ushort)Number(e, "subcommand", 0xFFFF, m.Id) : null,
                    Length = e.TryGetProperty("length", out JsonElement len) && len.ValueKind == JsonValueKind.Number && len.TryGetInt16(out short l) ? l : (short)-1,
                    Override = e.TryGetProperty("override", out JsonElement ov) && ov.ValueKind == JsonValueKind.True
                };
                if (e.TryGetProperty("override", out ov))
                    Require(ov.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{m.Id}: override is true or false");
                if (e.TryGetProperty("length", out len))
                    Require(len.ValueKind == JsonValueKind.Number && len.TryGetInt16(out _), $"{m.Id}: length is a number");
                Require(!m.Packets.Any(p => p.Id == packet.Id && p.Subcommand == packet.Subcommand), $"{m.Id}: packet 0x{packet.Id:X2} is declared twice");
                Require(packet.Subcommand is not ushort reserved || packet.Id != 0xBF || !PacketModRegistry.ReservedSubcommands.Contains(reserved),
                    $"{m.Id}: 0xBF subcommand 0x{packet.Subcommand:X4} is reserved by GUO");
                m.Packets.Add(packet);
            }
            return m;
        }

        private static string Str(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out JsonElement v))
                return null;
            Require(v.ValueKind == JsonValueKind.String, $"mod.json {name} must be a string");
            return v.GetString();
        }

        // A number, or a string of 0x-prefixed hex ("0xF9"), which is how packet ids are written.
        private static int Number(JsonElement o, string name, int max, string mod)
        {
            Require(o.TryGetProperty(name, out JsonElement v), $"{mod}: a packet has no {name}");
            int n = -1;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
                n = i;
            else if (v.ValueKind == JsonValueKind.String && v.GetString() is string s && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int h))
                n = h;
            Require(n >= 0 && n <= max, $"{mod}: packet {name} must be 0..0x{max:X}");
            return n;
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition)
                throw new ModException(message);
        }
    }

    /// <summary>One mod's outcome, for the log, the Options status line and the probes.</summary>
    internal readonly record struct ModLoadResult(string Folder, string Id, bool Loaded, int Packets, string Message);

    /// <summary>Loads every mod under a folder (one subfolder per mod, each with a mod.json and its
    /// assembly). Files only: nothing is fetched, and a mod that fails is skipped whole.</summary>
    internal static class ModLoader
    {
        public static List<ModLoadResult> LoadAll(string modsFolder, PacketModRegistry registry, Func<ModManifest, IModContext> contextFor)
        {
            var results = new List<ModLoadResult>();
            if (string.IsNullOrEmpty(modsFolder) || !Directory.Exists(modsFolder))
                return results;

            foreach (string folder in Directory.GetDirectories(modsFolder).OrderBy(f => f, StringComparer.Ordinal))
            {
                string manifestPath = Path.Combine(folder, "mod.json");
                if (!File.Exists(manifestPath))
                    continue;
                results.Add(LoadOne(folder, manifestPath, registry, contextFor));
            }
            return results;
        }

        private static ModLoadResult LoadOne(string folder, string manifestPath, PacketModRegistry registry, Func<ModManifest, IModContext> contextFor)
        {
            string name = Path.GetFileName(folder);
            ModManifest m = null;
            try
            {
                m = ModManifest.Parse(File.ReadAllText(manifestPath), folder);
                ModManifest.Require(m.Abi == ModAbi.Version, $"{m.Id}: built for mod ABI {m.Abi}; this client has ABI {ModAbi.Version}");
                ModManifest.Require(registry.Mods.All(x => x.Id != m.Id), $"{m.Id}: another mod has the same id");
                string assemblyPath = Path.Combine(folder, m.Assembly);
                ModManifest.Require(File.Exists(assemblyPath), $"{m.Id}: {m.Assembly} is missing from the mod's folder");

                // From a stream, so the file is not held open: a user can replace a mod while the
                // client runs, and the new copy loads on the next start.
                Assembly assembly;
                using (FileStream stream = File.OpenRead(assemblyPath))
                    assembly = new ModLoadContext(m.Id, assemblyPath).LoadFromStream(stream);
                Type type = assembly.GetType(m.Entry, throwOnError: false);
                ModManifest.Require(type != null && typeof(IGuoMod).IsAssignableFrom(type) && !type.IsAbstract,
                    $"{m.Id}: {m.Entry} is not an IGuoMod type in {m.Assembly}");
                var mod = (IGuoMod)Activator.CreateInstance(type);

                var registrar = registry.For(m, contextFor(m));
                mod.Register(registrar);
                int count = registrar.Commit();
                string skipped = registrar.Skipped > 0 ? $", {registrar.Skipped} not for this client version" : "";
                return new ModLoadResult(name, m.Id, true, count, $"{m.Id} {m.Version}: {count} packet(s){skipped}");
            }
            catch (Exception ex)
            {
                string why = ex is ModException ? ex.Message : $"{m?.Id ?? name}: {ex.GetType().Name}: {ex.Message}";
                return new ModLoadResult(name, m?.Id, false, 0, why);
            }
        }

        /// <summary>One context per mod, so a mod's own dependencies resolve from its folder. The
        /// ABI assembly always resolves to the client's copy, or the types would not match.</summary>
        private sealed class ModLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public ModLoadContext(string id, string assemblyPath) : base("guo-mod-" + id)
            {
                _resolver = new AssemblyDependencyResolver(Path.GetFullPath(assemblyPath));
            }

            protected override Assembly Load(AssemblyName name)
            {
                if (name.Name == typeof(IGuoMod).Assembly.GetName().Name)
                    return typeof(IGuoMod).Assembly;
                string path = _resolver.ResolveAssemblyToPath(name);
                return path != null ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
