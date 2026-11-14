// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GUO.Renderer.SpeedMode.Weather;

internal sealed record CloudLayer(string Algorithm, string Mask, string Sprite,
    float Density, float Coverage, float Scale, float WindDegrees, float WindSpeed,
    float Opacity, float[] DayColor, float[] NightColor, float DaynightResponse,
    float Height, float Parallax);

// Same strict data-only pattern as StoreWater; no Godot dependency.
internal sealed record CloudPreset(string Name, float ZoomStart, float ZoomEnd, CloudLayer[] Layers)
{
    internal const int MaxBytes = 16384;
    internal static CloudPreset Parse(byte[] bytes)
    {
        Require(bytes.Length <= MaxBytes, "Cloud preset exceeds 16 KiB");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            Fields(root, "schema name zoom_start zoom_end layers");
            Require(Text(root, "schema") == "guo/cloud@1", "Unsupported cloud schema");
            string name = Text(root, "name");
            Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 48 && !name.Any(char.IsControl), "Invalid cloud name");
            float start = Number(root, "zoom_start", 4, 5), end = Number(root, "zoom_end", 4, 6);
            Require(end > start, "Cloud zoom_end must exceed zoom_start");
            var rows = root.GetProperty("layers");
            Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() is >= 1 and <= 2, "Cloud layers must contain one or two entries");
            var layers = new List<CloudLayer>();
            foreach (var row in rows.EnumerateArray())
            {
                Fields(row, "algorithm mask sprite density coverage scale wind_degrees wind_speed opacity day_color night_color daynight_response height parallax");
                string algorithm = Text(row, "algorithm"), mask = Text(row, "mask"), sprite = Text(row, "sprite");
                Require(algorithm is "procedural" or "sprite-layered" or "hybrid", "Unknown cloud algorithm");
                Require(mask is "billow" or "haze" or "broken", "Unknown cloud mask");
                Require(algorithm == "procedural" ? sprite == "" : sprite != "", "Cloud sprite/algorithm mismatch");
                if (sprite.Length > 0 && sprite != "builtin:billow") SafeSprite(sprite);
                layers.Add(new CloudLayer(algorithm, mask, sprite,
                    Number(row,"density",0,2), Number(row,"coverage",0,1), Number(row,"scale",.01f,.25f),
                    Number(row,"wind_degrees",0,360), Number(row,"wind_speed",0,2), Number(row,"opacity",0,1),
                    Color(row,"day_color"), Color(row,"night_color"), Number(row,"daynight_response",0,1),
                    Number(row,"height",0,600), Number(row,"parallax",0,.03f)));
            }
            return new CloudPreset(name, start, end, layers.ToArray());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException or FormatException)
        { throw new InvalidDataException("Invalid cloud JSON or value type", e); }
    }

    internal static CloudPreset Load(string path)
    {
        // Bounded read even if the file grows between stat and read.
        using var stream = File.OpenRead(path);
        Require(stream.Length <= MaxBytes, "Cloud preset exceeds 16 KiB");
        byte[] bytes = new byte[MaxBytes + 1];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream:false);
        Require(count <= MaxBytes, "Cloud preset exceeds 16 KiB");
        return Parse(bytes.AsSpan(0,count).ToArray());
    }

    internal static string ResolveSprite(string directory, string reference)
    {
        SafeSprite(reference);
        string root = Path.GetFullPath(directory);
        string current = root;
        // Reject links in the entire ancestry, including the selected root.
        for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
            Require((ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "Cloud sprite directory is a link");
        foreach (string part in reference.Split('/'))
        {
            current = Path.Combine(current, part);
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Cloud sprite path is a link");
        }
        Require(new FileInfo(current).Length <= 4 * 1024 * 1024, "Cloud sprite exceeds 4 MiB");
        return current;
    }

    // Decode only after verifying the dimensions from the bounded file header.
    // WebP VP8X may contain animation; reject it rather than run a hidden timeline.
    internal static byte[] ReadSprite(string path)
    {
        using var stream = File.OpenRead(path);
        Require(stream.Length <= 4*1024*1024, "Cloud sprite exceeds 4 MiB");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        int width=0,height=0;
        if (bytes.Length>=24 && bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
        {
            uint w=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));
            uint h=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
            Require(w is >0 and <=1024 && h is >0 and <=1024,"Cloud sprite dimensions exceed 1024");
            width=(int)w;height=(int)h;
        }
        else if(bytes.Length>=30 && Encoding.ASCII.GetString(bytes,0,4)=="RIFF" && Encoding.ASCII.GetString(bytes,8,4)=="WEBP")
        {
            string chunk=Encoding.ASCII.GetString(bytes,12,4);
            if(chunk=="VP8X") {
                Require((bytes[20]&2)==0,"Animated cloud sprites are unsupported");
                width=1+bytes[24]+(bytes[25]<<8)+(bytes[26]<<16);
                height=1+bytes[27]+(bytes[28]<<8)+(bytes[29]<<16);
            } else if(chunk=="VP8L" && bytes[20]==0x2f) {
                width=1+bytes[21]+((bytes[22]&0x3f)<<8);
                height=1+(bytes[22]>>6)+(bytes[23]<<2)+((bytes[24]&0xf)<<10);
            } else if(chunk=="VP8 " && bytes[23]==0x9d && bytes[24]==1 && bytes[25]==0x2a) {
                width=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26,2))&0x3fff;
                height=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28,2))&0x3fff;
            }
        }
        Require(width is >0 and <=1024 && height is >0 and <=1024,"Cloud sprite header/dimensions invalid");
        return bytes;
    }

    private static void SafeSprite(string reference)
    {
        Require(reference.Length is > 0 and <= 200 && !reference.Any(c => char.IsControl(c) || "\\:<>\"|?*".Contains(c)), "Unsafe cloud sprite path");
        Require(reference.Split('/').All(p => p.Length > 0 && !p.StartsWith('.') && !p.EndsWith('.') && !p.EndsWith(' ')), "Unsafe cloud sprite component");
        foreach(string part in reference.Split('/')) {
            string stem=part.Split('.')[0].ToUpperInvariant();
            Require(stem is not ("CON" or "PRN" or "AUX" or "NUL")
                && !(stem.Length==4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])),"Reserved cloud sprite name");
        }
        Require(Path.GetExtension(reference) is ".png" or ".webp", "Cloud sprite must be PNG or WebP");
    }

    private static void Fields(JsonElement value, string fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Cloud value must be an object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) Require(names.Add(property.Name), "Duplicate cloud field");
        Require(names.SetEquals(fields.Split(' ')), "Missing or unknown cloud fields");
    }
    private static string Text(JsonElement row, string key)
    {
        var value = row.GetProperty(key);
        Require(value.ValueKind == JsonValueKind.String, "Cloud " + key + " must be text");
        return value.GetString();
    }
    private static float Number(JsonElement row, string key, float min, float max) => Scalar(row.GetProperty(key), key, min, max);
    private static float Scalar(JsonElement value, string key, float min, float max)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float n) && float.IsFinite(n) && n >= min && n <= max, "Cloud " + key + " out of range");
        return value.GetSingle();
    }
    private static float[] Color(JsonElement row, string key)
    {
        var color = row.GetProperty(key);
        Require(color.ValueKind == JsonValueKind.Array && color.GetArrayLength() == 3, "Cloud " + key + " needs three channels");
        return color.EnumerateArray().Select(v => Scalar(v,key,0,1)).ToArray();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
