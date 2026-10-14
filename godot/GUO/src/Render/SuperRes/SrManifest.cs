// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x manifest (provenance for the disk
// cache). One JSON dict at overrides/SR4x/manifest.json keyed by
// "Art/Land/<id>", "Art/Statics/<id>" and "Gumps/<id>". A PNG without a
// row still loads (the Art/Gump resolvers do not require it). Atomic
// write via tmp+move. No Godot objects.

using System;
using System.Collections.Generic;
using System.IO;
using GUO.Assets;
using GUO.Utility.Logging;

namespace GUO.Renderer.SuperRes
{
    internal static class SrManifest
    {
        public sealed class Row
        {
            [System.Text.Json.Serialization.JsonPropertyName("native_w")]
            public int NativeW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("native_h")]
            public int NativeH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sr_w")]
            public int SrW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sr_h")]
            public int SrH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("scale")]
            public double Scale { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("backend")]
            public string Backend { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("denoise")]
            public double Denoise { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha256")]
            public string Sha256 { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("created_utc")]
            public string CreatedUtc { get; set; } = "";
            /// <summary>True when the worker restored exact gray over
            /// native-gray blocks (SrGray), so the PNG hues in-shader.
            /// Missing (old rows) reads false: the resolvers pixel-check.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("gray_safe")]
            public bool GraySafe { get; set; } = false;
        }

        private static readonly object Sync = new object();

        public static string ManifestPath()
        {
            try
            {
                return Path.Combine(ArtLoader.OverrideDir(), "SR4x", "manifest.json");
            }
            catch
            {
                return null;
            }
        }

        public static string KeyFor(SrKind kind, int graphic)
        {
            return kind switch
            {
                SrKind.Land => $"Art/Land/{graphic}",
                SrKind.Static => $"Art/Statics/{graphic}",
                _ => $"Gumps/{graphic}",
            };
        }

        private static Dictionary<string, Row> ReadLocked()
        {
            try
            {
                string path = ManifestPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return new Dictionary<string, Row>();
                }

                string json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Row>>(json)
                    ?? new Dictionary<string, Row>();
            }
            catch (Exception e)
            {
                try { Log.Warn($"SR4x manifest unreadable: {e.Message}"); } catch { }
                return new Dictionary<string, Row>();
            }
        }

        public static void WriteRow(SrKind kind, int graphic, Row row)
        {
            try
            {
                lock (Sync)
                {
                    var rows = ReadLocked();
                    rows[KeyFor(kind, graphic)] = row;
                    string path = ManifestPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string json = System.Text.Json.JsonSerializer.Serialize(rows,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    try
                    {
                        File.Move(tmp, path, true);
                    }
                    catch
                    {
                        try
                        {
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(tmp, path);
                        }
                        catch (Exception e)
                        {
                            try { Log.Warn($"SR4x manifest write failed: {e.Message}"); } catch { }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                try { Log.Warn($"SR4x manifest row write failed: {e.Message}"); } catch { }
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            try
            {
                return ArtLoader.Sha256Hex(bytes);
            }
            catch
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] h = sha.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
