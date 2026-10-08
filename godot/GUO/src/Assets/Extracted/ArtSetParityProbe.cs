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
        foreach (var c in classes)
        {
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
        report["different_total"] = bad;
        if (!string.IsNullOrEmpty(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        GD.Print($"[artparity] {(bad == 0 ? "PASS" : "FAIL")}: {bad} different");
        return bad == 0 ? 0 : 1;
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
        try { Directory.Delete(root, true); } catch (IOException) { }
        GD.Print($"[artparity] synthetic: {(failed == 0 ? "PASS" : "FAIL")}");
        return failed == 0 ? 0 : 1;
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
