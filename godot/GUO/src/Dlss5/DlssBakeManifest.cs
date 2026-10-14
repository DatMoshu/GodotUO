// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): DLSS5 bake manifest (provenance for the disk
// cache). One JSON dict at overrides/DLSS/manifest.json keyed by
// "Art/Land/<id>", "Art/Statics/<id>" and "Gumps/<id>" — kept apart from
// overrides/SR4x/manifest.json on purpose: DLSS bakes are same-res (scale
// 1) while SR4x rows are 4x, and neither resolver may pick up the other's
// PNGs. Atomic write via tmp+move. No Godot objects.

using System;
using System.Collections.Generic;
using System.IO;
using GUO.Assets;
using GUO.Utility.Logging;

namespace GUO.Dlss5
{
    internal static class DlssBakeManifest
    {
        public sealed class Row
        {
            [System.Text.Json.Serialization.JsonPropertyName("native_w")]
            public int NativeW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("native_h")]
            public int NativeH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("out_w")]
            public int OutW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("out_h")]
            public int OutH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("scale")]
            public double Scale { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("backend")]
            public string Backend { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("intensity")]
            public double Intensity { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("profile")]
            public int Profile { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("tile")]
            public int Tile { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("overlap")]
            public int Overlap { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha256")]
            public string Sha256 { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("created_utc")]
            public string CreatedUtc { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("gray_safe")]
            public bool GraySafe { get; set; } = false;
        }

        private static readonly object Sync = new object();

        public static string ManifestPath()
        {
            try
            {
                return Path.Combine(ArtLoader.OverrideDir(), "DLSS", "manifest.json");
            }
            catch
            {
                return null;
            }
        }

        public static string KeyFor(DlssBakeKind kind, int graphic)
        {
            return kind switch
            {
                DlssBakeKind.Land => $"Art/Land/{graphic}",
                DlssBakeKind.Static => $"Art/Statics/{graphic}",
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
                try { Log.Warn($"DLSS bake manifest unreadable: {e.Message}"); } catch { }
                return new Dictionary<string, Row>();
            }
        }

        public static void WriteRow(DlssBakeKind kind, int graphic, Row row)
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
                            try { Log.Warn($"DLSS bake manifest write failed: {e.Message}"); } catch { }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                try { Log.Warn($"DLSS bake manifest row write failed: {e.Message}"); } catch { }
            }
        }
    }
}
