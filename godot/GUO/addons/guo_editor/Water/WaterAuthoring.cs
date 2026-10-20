// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using GUO.Store;
using Godot;

/// <summary>Manual, data-only water authoring. Uses the same validator as installed packs.</summary>
internal static class WaterAuthoring
{
    internal static string DraftDirectory
    {
        get { EditorWorkspace.Ensure(); return Path.Combine(GUO.Workspace.Workspace.Root, "editor", "water", "drafts"); }
    }

    internal static readonly StoreWater[] Presets =
    {
        new("UO+", "classicplus", new[] { .12f, .24f, .42f }, .55f, .035f, .09f, .12f, .18f, 0, 0, 1, .8f, .98f),
        new("Pixel water", "pixel", new[] { .1f, .22f, .45f }, .8f, .035f, .3f, .9f, .25f, .6f, 3, 2, 1, .98f),
        new("Cel water", "cel", new[] { .08f, .28f, .42f }, .7f, .03f, .22f, .7f, .2f, .08f, 3, 1, 1, .98f),
        new("Realistic water", "realistic", new[] { .06f, .2f, .32f }, 1, .035f, .24f, .6f, .24f, .3f, 0, 1, 1, .985f),
        new("Calm harbor", "classicplus", new[] { .1f, .22f, .34f }, .35f, .02f, .05f, .08f, .15f, 0, 0, 1, .65f, .97f),
        new("Windy coast", "realistic", new[] { .06f, .24f, .32f }, 1.5f, .04f, .42f, .8f, .32f, .25f, 0, 1, 1.2f, .985f),
        new("Dark swamp", "pixel", new[] { .1f, .19f, .12f }, .25f, .018f, .12f, .05f, .12f, 0, 3, 2, .8f, .97f),
        new("Gentle river", "uo", new[] { .12f, .26f, .35f }, .6f, .025f, .15f, .3f, .18f, .12f, 6, 1, 1, .98f),
        new("Stormy sea", "realistic", new[] { .045f, .09f, .16f }, 2.2f, .05f, .7f, .95f, .4f, .15f, 0, 1, 1.6f, .99f),
    };

    internal static byte[] Serialize(StoreWater style)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = StoreWater.Schema, name = style.Name, model = style.Model, tint = style.Tint,
            speed = style.Speed, scale = style.Scale, wave = style.Wave, foam = style.Foam,
            foam_width = style.FoamWidth, glint = style.Glint, levels = style.Levels, grain = style.Grain,
            ripples = new { strength = style.RippleStrength, damping = style.RippleDamping }
        }, new JsonSerializerOptions { WriteIndented = true });
        StoreWater.Parse(bytes);
        return bytes;
    }

    internal static StoreWater Read(string path)
    {
        if (new FileInfo(path).Length > StoreWater.MaxBytes) throw new InvalidDataException("Water preset exceeds 16 KB.");
        return StoreWater.Parse(File.ReadAllBytes(path));
    }

    internal static StoreWater ParseText(string text)
    {
        var utf8 = new System.Text.UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > StoreWater.MaxBytes)
            throw new InvalidDataException("Water preset exceeds 16 KB of UTF-8 text.");
        return StoreWater.Parse(utf8.GetBytes(text));
    }

    // Authoring destinations must never touch the install or become imported game resources.
    internal static void CheckDestination(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string root in new[] { EditorData.Setting("UO_CLIENT_DATA", ""), ProjectSettings.GlobalizePath("res://") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string folder = Path.GetFullPath(root).TrimEnd('/', '\\');
            if (full.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a folder outside the client install and Godot project.");
        }
    }

    internal static void Save(StoreWater style, string path)
    {
        CheckDestination(path);
        WriteAtomic(path, Serialize(style));
    }

    internal static void CheckPreviewDestination(string path)
    {
        CheckDestination(path);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .png file for the synthetic preview.");
    }

    internal static void SavePreview(Image image, string path)
    {
        CheckPreviewDestination(path);
        if (image == null || image.IsEmpty())
            throw new InvalidDataException("The synthetic preview has not rendered yet. Wait for it to draw, then try again.");
        byte[] bytes = image.SavePngToBuffer();
        if (bytes.Length == 0) throw new InvalidDataException("Could not encode the synthetic preview. Try saving it again.");
        WriteAtomic(path, bytes);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        string pending = full + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(pending, bytes);
            File.Move(pending, full, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    /// <summary>A portable v2 water component, with an original palette swatch and no game artwork.</summary>
    internal static void Export(StoreWater style, string path, string id, string version, string author, string licence)
    {
        CheckDestination(path);
        StorePack.Id(id); StorePack.Version(version);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["water.json"] = Serialize(style) };
        using (var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8))
        {
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float value = .5f + y / 63f;
                    if (style.Model is "pixel" or "cel") value = MathF.Floor(value * 4) / 4;
                    image.SetPixel(x, y, new Color(style.Tint[0] * value, style.Tint[1] * value, style.Tint[2] * value));
                }
            files["preview.png"] = image.SavePngToBuffer();
        }
        if (licence != "CC0-1.0")
            files["LICENSE.txt"] = System.Text.Encoding.UTF8.GetBytes($"{style.Name}\nAuthor: {author}\nSPDX-License-Identifier: {licence}\nOriginal numeric water preset and palette swatch.\n");
        var manifest = new StoreManifest
        {
            Schema = "guo/store-pack@2", Id = id, Version = version, Kind = "content", Target = "client",
            Title = style.Name, Author = author, Licence = licence, MinProfileVersion = 12,
            Preview = "preview.png", Dependencies = new(),
            Components = new() { new StoreComponent { Id = "sea", Type = "water", Target = "client", Entry = "water.json" } },
            Files = files.ToDictionary(p => p.Key, p => StorePack.Hash(new MemoryStream(p.Value)))
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        StorePack.Parse(manifestBytes);
        files["manifest.json"] = manifestBytes;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0x81a4 << 16;
                using var stream = entry.Open(); stream.Write(data);
            }
        WriteAtomic(path, buffer.ToArray());
    }
}
#endif
