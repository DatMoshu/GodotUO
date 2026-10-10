// GUO addition, not a port. ADR-0034 / AX2: every id the loaders can decode, with and without the extracted set.
//
//   godot-console --headless --path godot/GUO res://src/Assets/Extracted/ArtSetParityProbe.tscn -- --art-set
//
// UO_ART_PARITY_OUT names the JSON report; UO_ART_PARITY_MODE=load measures one start (startup time and memory)
// instead, with --art-set on or off. Exit code 0 only when no id differs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.Assets;
using Environment = System.Environment;

namespace GUO.Assets.Extracted;

public partial class ArtSetParityProbe : Node
{
    private sealed class Tally
    {
        public int Compared, FromSet, Different, Empty;
        public List<string> First = new();
    }

    public override void _Ready()
    {
        int code = 1;
        try { code = Run(); }
        catch (Exception ex) { GD.PrintErr("[artparity] " + ex); }
        GetTree().Quit(code);
    }

    private static UOFileManager Open(bool content, out long ms)
    {
        var files = new UOFileManager(GUO.Host.UoDataProbe.ParseVersion(Environment.GetEnvironmentVariable("UO_CLIENT_VERSION") ?? "7.0.107.76"), Environment.GetEnvironmentVariable("UO_CLIENT_DATA"));
        var sw = Stopwatch.StartNew();
        files.Load(false, "enu", "", content);
        ms = sw.ElapsedMilliseconds;
        return files;
    }

    private int Run()
    {
        string outPath = Environment.GetEnvironmentVariable("UO_ART_PARITY_OUT");
        if (Environment.GetEnvironmentVariable("UO_ART_PARITY_MODE") == "load") return Load(outPath);
        if (Environment.GetEnvironmentVariable("UO_ART_PARITY_MODE") == "synthetic") return Synthetic();

        using var plain = Open(false, out long plainMs);
        using var withSet = Open(true, out long setMs);
        if (withSet.Content == null) { GD.PrintErr("[artparity] the set did not mount (pass --art-set; see the warning above)"); return 2; }

        var report = new Dictionary<string, object> { ["load_ms_plain"] = plainMs, ["load_ms_set"] = setMs };
        int bad = 0;
        var classes = new (string Name, int Max, Func<UOFileManager, int, (uint[] P, int W, int H)> Get)[]
        {
            ("land", ArtLoader.MAX_LAND_DATA_INDEX_COUNT, (f, i) => Art(f, (uint)i)),
            ("static", ArtLoader.MAX_STATIC_DATA_INDEX_COUNT - ArtLoader.MAX_LAND_DATA_INDEX_COUNT, (f, i) => Art(f, (uint)i + ArtLoader.MAX_LAND_DATA_INDEX_COUNT)),
            ("gump", GumpsLoader.MAX_GUMP_DATA_INDEX_COUNT, (f, i) => Gump(f, (uint)i)),
            ("texmap", TexmapsLoader.MAX_LAND_TEXTURES_DATA_INDEX_COUNT, (f, i) => Texmap(f, (uint)i)),
            ("light", LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT, (f, i) => Light(f, (uint)i)),
        };
        bool onlyAnim = Environment.GetEnvironmentVariable("UO_ART_PARITY_ONLY") == "anim";
        foreach (var c in classes)
        {
            if (onlyAnim) break;
            var t = new Tally();
            for (int id = 0; id < c.Max; id++)
            {
                var a = c.Get(plain, id);
                var b = c.Get(withSet, id);
                t.Compared++;
                if (a.P == null && b.P == null) { t.Empty++; continue; }
                if (withSet.Content.TryImage(c.Name, id, out _)) t.FromSet++;
                if (a.W != b.W || a.H != b.H || a.P == null || b.P == null || !a.P.AsSpan().SequenceEqual(b.P))
                {
                    t.Different++;
                    if (t.First.Count < 10) t.First.Add($"{c.Name} {id}: {a.W}x{a.H} vs {b.W}x{b.H}");
                }
            }
            bad += t.Different;
            report[c.Name] = new { t.Compared, empty = t.Empty, answered_by_set = t.FromSet, different = t.Different, first = t.First };
            GD.Print($"[artparity] {c.Name}: compared {t.Compared}, empty {t.Empty}, answered by the set {t.FromSet}, different {t.Different}");
        }
        bad += AnimationPass(plain, withSet, report);
        report["different_total"] = bad;
        if (!string.IsNullOrEmpty(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        GD.Print($"[artparity] {(bad == 0 ? "PASS" : "FAIL")}: {bad} different");
        return bad == 0 ? 0 : 1;
    }

    // Animations: every block the loader can reach through its own body resolution (Body.def, Bodyconv.def, mobtypes,
    // the UOP replacement tables), read both ways. The set is keyed below that resolution, so this is the proof the
    // keys line up with what the loader reads.
    private static int AnimationPass(UOFileManager plain, UOFileManager withSet, Dictionary<string, object> report)
    {
        var loader = plain.Animations;
        var seen = new HashSet<string>();
        var first = new List<string>();
        var firstMissing = new List<string>();
        int reached = 0, fromSet = 0, missing = 0, different = 0, emptyBoth = 0;
        const int MaxBodies = 2048;

        void Compare(string key, Func<FrameCopy[]> read, Func<(bool Ok, FrameCopy[] Frames)> fromContent)
        {
            if (!seen.Add(key)) return;
            reached++;
            var a = read();
            var (ok, b) = fromContent();
            if (!ok)
            {
                if (a.Length == 0) emptyBoth++; else { missing++; if (firstMissing.Count < 15 || missing % 3000 == 0) firstMissing.Add($"{key} frames={a.Length} drawn={a.Count(x => x.Pixels != null)}"); }
                return;
            }
            fromSet++;
            if (!SameFrames(a, b))
            {
                different++;
                if (first.Count < 15 || different % 150 == 0) first.Add(key + " " + WhyDifferent(a, b));
            }
        }

        for (int body = 0; body < MaxBodies; body++)
        {
            ushort hue = 0;
            var flags = AnimationFlags.None;
            AnimationsLoader.AnimationDirection[] indices;
            int fileIndex;
            AnimationGroupsType type;
            try
            {
                indices = loader.GetIndices(plain.Version, (ushort)body, ref hue, ref flags, out fileIndex, out type).ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            bool uop = (flags & AnimationFlags.UseUopAnimation) != 0;
            for (int i = 0; i < indices.Length; i++)
            {
                var ff = indices[i];
                if (ff.Position == 0 && ff.Size == 0) continue;
                if (uop)
                {
                    foreach (var kind in new[] { type, AnimationGroupsType.Equipment })
                    {
                        bool equip = kind == AnimationGroupsType.Equipment;
                        for (byte dir = 0; dir < AnimationsLoader.MAX_DIRECTIONS; dir++)
                        {
                            byte d = dir;
                            Compare($"u{fileIndex}.{ff.Position}.{d}" + (equip ? ".e" : ""),
                                () => Copy(loader.ReadUOPAnimationFrames((ushort)body, (byte)i, d, kind, fileIndex, ff)),
                                () => withSet.Content.TryAnimationUop(fileIndex, ff.Position, d, equip, out var f) ? (true, Copy(f)) : (false, null));
                        }
                    }
                }
                else
                {
                    Compare($"m{fileIndex}.{ff.Position}.{ff.Size}",
                        () => Copy(loader.ReadMULAnimationFrames(fileIndex, ff)),
                        () => withSet.Content.TryAnimationMul(fileIndex, ff.Position, ff.Size, out var f) ? (true, Copy(f)) : (false, null));
                }
            }
        }
        report["anim"] = new { reached, answered_by_set = fromSet, not_in_set = missing, empty_in_both = emptyBoth, different, first, firstMissing };
        GD.Print($"[artparity] anim: blocks reached {reached}, answered by the set {fromSet}, not in the set {missing}, empty in both {emptyBoth}, different {different}");
        return different;
    }

    private readonly record struct FrameCopy(int Num, short Cx, short Cy, short W, short H, uint[] Pixels);

    private static FrameCopy[] Copy(ReadOnlySpan<AnimationsLoader.FrameInfo> frames)
    {
        var r = new FrameCopy[frames.Length];
        for (int i = 0; i < r.Length; i++)
        {
            var f = frames[i];
            int n = f.Width > 0 && f.Height > 0 ? f.Width * f.Height : 0;
            // a frame with a zero side has no pixels; the loader keeps the other side, the set does not, and nothing reads it
            r[i] = new FrameCopy(f.Num, f.CenterX, f.CenterY, (short)(n == 0 ? 0 : f.Width), (short)(n == 0 ? 0 : f.Height), n == 0 ? null : f.Pixels.AsSpan(0, n).ToArray());
        }
        return r;
    }

    private static string WhyDifferent(FrameCopy[] a, FrameCopy[] b)
    {
        if (a.Length != b.Length) return $"count {a.Length} vs {b.Length}";
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Num != b[i].Num) return $"frame {i} num {a[i].Num} vs {b[i].Num}";
            if (a[i].Cx != b[i].Cx || a[i].Cy != b[i].Cy) return $"frame {i} centre {a[i].Cx},{a[i].Cy} vs {b[i].Cx},{b[i].Cy}";
            if (a[i].W != b[i].W || a[i].H != b[i].H) return $"frame {i} size {a[i].W}x{a[i].H} vs {b[i].W}x{b[i].H}";
            if ((a[i].Pixels == null) != (b[i].Pixels == null)) return $"frame {i} pixels null mismatch";
            if (a[i].Pixels != null && !a[i].Pixels.AsSpan().SequenceEqual(b[i].Pixels))
            {
                int n = 0; for (int k = 0; k < a[i].Pixels.Length; k++) if (a[i].Pixels[k] != b[i].Pixels[k]) n++;
                return $"frame {i} pixels differ in {n} of {a[i].Pixels.Length}";
            }
        }
        return "?";
    }

    private static bool SameFrames(FrameCopy[] a, FrameCopy[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Num != b[i].Num || a[i].Cx != b[i].Cx || a[i].Cy != b[i].Cy || a[i].W != b[i].W || a[i].H != b[i].H) return false;
            if ((a[i].Pixels == null) != (b[i].Pixels == null)) return false;
            if (a[i].Pixels != null && !a[i].Pixels.AsSpan().SequenceEqual(b[i].Pixels)) return false;
        }
        return true;
    }

    // The mount rules on a made-up set (no game data): the loaders' own override files win, and every refusal falls back.
    private static int Synthetic()
    {
        string root = Path.Combine(Path.GetTempPath(), "guo_artset_" + Guid.NewGuid().ToString("N"));
        string setDir = Path.Combine(root, "set"), client = Path.Combine(root, "client");
        Directory.CreateDirectory(Path.Combine(setDir, "static"));
        Directory.CreateDirectory(Path.Combine(client, "Art", "Statics"));
        File.WriteAllBytes(Path.Combine(client, "fake.mul"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(client, "Art", "Statics", "7.art"), new byte[] { 0 });
        using var page = Image.CreateEmpty(2048, 2048, false, Image.Format.Rgba8);
        page.Fill(new Color(0, 0, 0, 0));
        page.SetPixel(0, 0, new Color(1, 0, 0, 1)); page.SetPixel(1, 0, new Color(0, 1, 0, 1));
        page.SetPixel(2, 0, new Color(0, 0, 1, 1)); page.SetPixel(3, 0, new Color(1, 1, 1, 1));
        byte[] png = page.SavePngToBuffer();
        string pngPath = Path.Combine(setDir, "static", "page_0000.png");
        File.WriteAllBytes(pngPath, png);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        string setId = new string('a', 64);
        File.WriteAllText(Path.Combine(setDir, "set.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schema"] = "guo/art_set@1", ["version"] = 1,
            ["fingerprint"] = new Dictionary<string, object> { ["files"] = new[] { new Dictionary<string, object> { ["name"] = "fake.mul", ["size"] = 3 } }, ["set_id"] = setId },
            ["classes"] = new Dictionary<string, object> { ["static"] = new { } },
        }));
        string Entry(int x) => $"{{\"page\":0,\"x\":{x},\"y\":0,\"w\":2,\"h\":1}}";
        File.WriteAllText(Path.Combine(setDir, "static", "index.json"),
            $"{{\"schema\":\"guo/art_index@1\",\"set_id\":\"{setId}\",\"page_size\":2048,\"pixel_format\":\"rgba8\"," +
            $"\"pages\":[{{\"file\":\"page_0000.png\",\"sha256\":\"{sha}\"}}],\"entries\":{{\"6\":{Entry(0)},\"7\":{Entry(2)}}}}}");

        var pieces = new Dictionary<string, byte[]>
        {
            ["set.json"] = File.ReadAllBytes(Path.Combine(setDir, "set.json")),
            ["static/index.json"] = File.ReadAllBytes(Path.Combine(setDir, "static", "index.json")),
            ["static/page_0000.png"] = png,
        };
        int failed = 0;
        void Check(bool ok, string what) { GD.Print($"[artparity] {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }

        var src = ExtractedArtSource.Mount(setDir, client);
        Check(src != null, "a set that matches the install mounts");
        if (src != null)
        {
            Check(src.TryImage("static", 6, out var px) && px.Width == 2 && px.Height == 1 && px.Data[0] == 0xFF0000FF && px.Data[1] == 0xFF00FF00,
                "an id with no override file is answered from the page, as R,G,B,A bytes");
            Check(!src.TryImage("static", 7, out _), "an id the client's Art/Statics file holds is left to the loader (the set stays behind overrides)");
            Check(!src.TryImage("static", 8, out _) && !src.TryImage("gump", 6, out _), "an id or class not in the set falls back");
        }
        File.WriteAllBytes(Path.Combine(client, "fake.mul"), new byte[] { 1, 2, 3, 4 });
        Check(ExtractedArtSource.Mount(setDir, client) == null, "a source file with another size refuses the whole set");
        File.WriteAllBytes(Path.Combine(client, "fake.mul"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3 });
        var damaged = ExtractedArtSource.Mount(setDir, client);
        Check(damaged != null && !damaged.TryImage("static", 6, out _), "a damaged page drops its ids to the original files");
        File.Delete(Path.Combine(setDir, "set.json"));
        Check(ExtractedArtSource.Mount(setDir, client) == null, "a folder with no set.json is not a set");
        Check(!ExtractedArtSource.Enabled(new[] { "--art-set", "--no-art-set" }), "--no-art-set wins over --art-set");
        failed += SyntheticContainer(root, client, pieces, setId);
        try { Directory.Delete(root, true); } catch (IOException) { }
        GD.Print($"[artparity] synthetic: {(failed == 0 ? "PASS" : "FAIL")}");
        return failed == 0 ? 0 : 1;
    }

    // The encrypted container on the same made-up set: a test-side sealer (the client only reads), no game data.
    private static byte[] SealContainer(string shard, byte[] key, string setId, Dictionary<string, byte[]> pieces)
    {
        byte[] header = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"cipher\":\"aes-256-gcm\",\"key_id\":\"{ArtContainer.KeyId(key)}\",\"schema\":\"guo/art_container@1\",\"set_id\":\"{setId}\",\"shard_id\":\"{shard}\",\"version\":1}}");
        byte[] aadHead = new byte[4 + header.Length];
        BitConverter.GetBytes(header.Length).CopyTo(aadHead, 0); header.CopyTo(aadHead, 4);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { (byte)'G', (byte)'U', (byte)'O', (byte)'A', (byte)'R', (byte)'T', 1, 0 });
        ms.Write(aadHead);
        var entries = new Dictionary<string, long[]>();
        long Chunk(string name, byte[] data)
        {
            long at = ms.Position;
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            byte[] nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            byte[] aad = new byte[aadHead.Length + nameBytes.Length];
            aadHead.CopyTo(aad, 0); nameBytes.CopyTo(aad, aadHead.Length);
            byte[] ct = new byte[data.Length], tag = new byte[16];
            using (var gcm = new System.Security.Cryptography.AesGcm(key, 16)) gcm.Encrypt(nonce, data, ct, tag, aad);
            ms.Write(BitConverter.GetBytes((ushort)nameBytes.Length)); ms.Write(nameBytes); ms.Write(nonce);
            ms.Write(BitConverter.GetBytes(ct.Length + 16)); ms.Write(ct); ms.Write(tag);
            entries[name] = new[] { at, (long)(ct.Length + 16) };
            return at;
        }
        foreach (var kv in pieces) Chunk(kv.Key, kv.Value);
        var toc = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["entries"] = entries });
        long tocAt = Chunk(ArtContainer.Toc, toc);
        ms.Write(BitConverter.GetBytes(tocAt));
        ms.Write(new byte[] { (byte)'G', (byte)'U', (byte)'O', (byte)'E', (byte)'N', (byte)'D', 1, 0 });
        return ms.ToArray();
    }

    private static int SyntheticContainer(string root, string client, Dictionary<string, byte[]> pieces, string setId)
    {
        int failed = 0;
        void Check(bool ok, string what) { GD.Print($"[artparity] {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)i;
        string shard = "demo";
        string folder = Path.Combine(root, "cset"), keys = Path.Combine(root, "keys");
        Directory.CreateDirectory(Path.Combine(folder, "containers"));
        Directory.CreateDirectory(keys);
        string path = Path.Combine(folder, "containers", shard + ".guoart");
        byte[] good = SealContainer(shard, key, setId, pieces);
        File.WriteAllBytes(path, good);

        var src = ExtractedArtSource.MountContainer(path, shard, key, client);
        Check(src != null, "a container mounts with the shard's key");
        Check(src != null && src.TryImage("static", 6, out var px) && px.Width == 2 && px.Data[0] == 0xFF0000FF && px.Data[1] == 0xFF00FF00,
            "an id is answered from a sealed page, the same R,G,B,A bytes as the plain set");
        Check(src != null && !src.TryImage("static", 7, out _), "the client's override files still win over the container");
        Check(ExtractedArtSource.MountContainer(path, shard, new byte[32], client) == null, "a wrong key falls back to the original files");
        Check(ExtractedArtSource.MountContainer(path, "other", key, client) == null, "a container for another shard id is refused");

        Check(ExtractedArtSource.MountShardContainer(shard, client, folder, keys) == null, "no key in the profile falls back");
        File.WriteAllText(Path.Combine(keys, shard + ".key"), Convert.ToHexString(key).ToLowerInvariant() + "\n");
        Check(ExtractedArtSource.MountShardContainer(shard, client, folder, keys) != null, "the key file in the profile's key folder opens it");
        Check(ExtractedArtSource.MountShardContainer("../x", client, folder, keys) == null, "a shard id that is a path is refused");

        // a changed byte inside the sealed page: the container mounts (the index is fine) and the id falls back
        byte[] bad = (byte[])good.Clone();
        string pagePath = Path.Combine(root, "tampered.guoart");
        int at = good.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("static/page_0000.png"));
        bad[at + 20 + 12 + 4 + 3] ^= 1;
        File.WriteAllBytes(pagePath, bad);
        var tampered = ExtractedArtSource.MountContainer(pagePath, shard, key, client);
        Check(tampered != null && !tampered.TryImage("static", 6, out _), "a changed byte in a sealed page is refused and the id falls back");

        // header-only change: same length and the same key id, so only the associated data catches it
        byte[] swapped = (byte[])good.Clone();
        int hat = swapped.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("\"shard_id\":\"demo\""));
        swapped[hat + 15] = (byte)'x';
        File.WriteAllBytes(Path.Combine(root, "header.guoart"), swapped);
        Check(ExtractedArtSource.MountContainer(Path.Combine(root, "header.guoart"), "demx", key, client) == null, "a header-only change is refused");
        File.WriteAllBytes(Path.Combine(root, "cut.guoart"), good.AsSpan(0, good.Length - 9).ToArray());
        Check(ExtractedArtSource.MountContainer(Path.Combine(root, "cut.guoart"), shard, key, client) == null, "a container cut short is refused");

        Check(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 1 && !Directory.Exists(Path.Combine(folder, "static")),
            "mounting wrote nothing to disk beside the container");
        Check(!System.Text.Encoding.Latin1.GetString(good).Contains(Convert.ToHexString(key), StringComparison.OrdinalIgnoreCase), "the key is not in the container");
        Check(ExtractedArtSource.ShardId(new[] { "--art-shard", "demo" }) == "demo", "--art-shard selects the container");
        return failed;
    }

    // One start as a player would have it: load, then touch what the login screen and a few seconds in Britain touch.
    private int Load(string outPath)
    {
        using var files = Open(true, out long ms);
        var sw = Stopwatch.StartNew();
        long sum = 0;
        for (int i = 0; i < 4244 && i < ArtLoader.MAX_LAND_DATA_INDEX_COUNT; i++) sum += Art(files, (uint)i).W;
        for (int i = 0; i < 4000; i++) sum += Art(files, (uint)i + ArtLoader.MAX_LAND_DATA_INDEX_COUNT).W;
        for (int i = 0; i < 600; i++) sum += Gump(files, (uint)i).W;
        long touchMs = sw.ElapsedMilliseconds;
        GC.Collect();
        var report = new Dictionary<string, object>
        {
            ["set_mounted"] = files.Content != null, ["load_ms"] = ms, ["touch_ms"] = touchMs,
            ["working_set_mb"] = Process.GetCurrentProcess().WorkingSet64 / 1048576, ["managed_mb"] = GC.GetTotalMemory(true) / 1048576, ["checksum"] = sum,
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        GD.Print("[artparity] " + json.Replace("\n", " "));
        if (!string.IsNullOrEmpty(outPath)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!); File.WriteAllText(outPath, json); }
        return 0;
    }

    private static (uint[] P, int W, int H) Art(UOFileManager f, uint id)
    {
        try { var i = f.Arts.GetArt(id); return (i.Pixels.Length == 0 ? null : i.Pixels.ToArray(), i.Width, i.Height); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, -1, -1); }
    }

    private static (uint[] P, int W, int H) Gump(UOFileManager f, uint id)
    {
        try { var i = f.Gumps.GetGump(id); return (i.Pixels.Length == 0 ? null : i.Pixels.ToArray(), i.Width, i.Height); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, -1, -1); }
    }

    private static (uint[] P, int W, int H) Texmap(UOFileManager f, uint id)
    {
        try { var i = f.Texmaps.GetTexmap(id); return (i.Pixels.Length == 0 ? null : i.Pixels.ToArray(), i.Width, i.Height); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, -1, -1); }
    }

    private static (uint[] P, int W, int H) Light(UOFileManager f, uint id)
    {
        try { var i = f.Lights.GetLight(id); return (i.Pixels.Length == 0 ? null : i.Pixels.ToArray(), i.Width, i.Height); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, -1, -1); }
    }
}
