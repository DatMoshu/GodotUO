// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.IO.Audio
{
    /// <summary>
    /// Ambient music zones: tile rectangles with a playlist of generated
    /// tracks (ComfyUI Stable Audio MP3s under the music dir). Walking in
    /// starts the zone's round-robin; tracks advance as each ends; walking
    /// out stops. The game's own music system is untouched outside zones.
    /// Manifest: music.json beside the tracks (GUO_MUSIC_DIR, else
    /// %APPDATA%/GUO/music).
    /// </summary>
    internal static class MusicZones
    {
        public sealed class Zone
        {
            public string Name = "";
            public int Facet;
            public int X0, Y0, X1, Y1;
            public List<string> Tracks = new();

            public bool Contains(int facet, int x, int y) =>
                (Facet < 0 || Facet == facet)
                && x >= Math.Min(X0, X1) && x <= Math.Max(X0, X1)
                && y >= Math.Min(Y0, Y1) && y <= Math.Max(Y0, Y1);
        }

        public static string Status { get; private set; } = "idle";

        public static string MusicDir()
        {
            string dir = Environment.GetEnvironmentVariable("GUO_MUSIC_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GUO", "music");
        }

        public static string TracksDir()
        {
            return Path.Combine(MusicDir(), "tracks");
        }

        public static List<Zone> Load()
        {
            var zones = new List<Zone>();
            try
            {
                string path = Path.Combine(MusicDir(), "music.json");
                if (!File.Exists(path))
                {
                    return zones;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("zones", out JsonElement list))
                {
                    return zones;
                }

                foreach (JsonElement z in list.EnumerateArray())
                {
                    var zone = new Zone
                    {
                        Name = z.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                        Facet = z.TryGetProperty("facet", out JsonElement f) ? f.GetInt32() : -1,
                        X0 = z.TryGetProperty("x0", out JsonElement x0) ? x0.GetInt32() : 0,
                        Y0 = z.TryGetProperty("y0", out JsonElement y0) ? y0.GetInt32() : 0,
                        X1 = z.TryGetProperty("x1", out JsonElement x1) ? x1.GetInt32() : 0,
                        Y1 = z.TryGetProperty("y1", out JsonElement y1) ? y1.GetInt32() : 0,
                    };
                    if (z.TryGetProperty("tracks", out JsonElement tracks))
                    {
                        foreach (JsonElement t in tracks.EnumerateArray())
                        {
                            string file = t.GetString();
                            if (!string.IsNullOrWhiteSpace(file))
                            {
                                zone.Tracks.Add(file);
                            }
                        }
                    }

                    zones.Add(zone);
                }
            }
            catch (Exception)
            {
            }

            return zones;
        }

        public static bool Save(List<Zone> zones)
        {
            try
            {
                Directory.CreateDirectory(MusicDir());
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("format", 1);
                    writer.WriteStartArray("zones");
                    foreach (Zone z in zones)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", z.Name);
                        writer.WriteNumber("facet", z.Facet);
                        writer.WriteNumber("x0", z.X0);
                        writer.WriteNumber("y0", z.Y0);
                        writer.WriteNumber("x1", z.X1);
                        writer.WriteNumber("y1", z.Y1);
                        writer.WriteStartArray("tracks");
                        foreach (string t in z.Tracks)
                        {
                            writer.WriteStringValue(t);
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                File.WriteAllText(Path.Combine(MusicDir(), "music.json"),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
                _zones = zones;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static List<string> TrackFiles()
        {
            var files = new List<string>();
            try
            {
                string dir = TracksDir();
                if (!Directory.Exists(dir))
                {
                    return files;
                }

                foreach (string path in Directory.EnumerateFiles(dir, "*.mp3"))
                {
                    files.Add(Path.GetFileName(path));
                }

                foreach (string path in Directory.EnumerateFiles(dir, "*.wav"))
                {
                    files.Add(Path.GetFileName(path));
                }

                files.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
            }

            return files;
        }

        private static List<Zone> _zones;
        private static Godot.AudioStreamPlayer _player;
        private static string _currentZone;
        private static int _trackIndex;

        /// <summary>The live zone list (player and gump share it; Save persists).</summary>
        public static List<Zone> Zones
        {
            get
            {
                _zones ??= Load();
                return _zones;
            }
        }

        private static void EnsurePlayer()
        {
            if (_player == null || !Godot.GodotObject.IsInstanceValid(_player))
            {
                _player = AudioHost.CreatePlayer();
            }
        }

        /// <summary>Per-frame tick from the scene update: zone lookup, start, advance, stop.</summary>
        public static void Update(Game.World world)
        {
            List<Zone> zones = Zones;

            Game.GameObjects.PlayerMobile player = world?.Player;
            if (player == null || !world.InGame)
            {
                Stop();
                return;
            }

            Zone inside = null;
            foreach (Zone z in zones)
            {
                if (z.Tracks.Count > 0 && z.Contains(world.MapIndex, player.X, player.Y))
                {
                    inside = z;
                    break;
                }
            }

            if (inside == null)
            {
                if (_currentZone != null)
                {
                    Stop();
                    Status = "idle";
                }

                return;
            }

            var profile = Configuration.ProfileManager.CurrentProfile;
            if (profile == null || !profile.EnableMusic)
            {
                Stop();
                Status = "idle (music off)";
                return;
            }

            if (_currentZone != inside.Name)
            {
                _currentZone = inside.Name;
                _trackIndex = 0;
                PlayTrack(inside, profile);
                return;
            }

            if (_player != null && !_player.Playing)
            {
                _trackIndex = (_trackIndex + 1) % inside.Tracks.Count;
                PlayTrack(inside, profile);
            }
        }

        private static void PlayTrack(Zone zone, Configuration.Profile profile)
        {
            for (int i = 0; i < zone.Tracks.Count; i++)
            {
                int at = (_trackIndex + i) % zone.Tracks.Count;
                string path = Path.Combine(TracksDir(), zone.Tracks[at]);
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception)
                {
                    continue;
                }

                try
                {
                    EnsurePlayer();
                    if (_player == null)
                    {
                        Status = "no audio host";
                        return;
                    }

                    Godot.AudioStream stream = zone.Tracks[at].EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        ? (Godot.AudioStream)new Godot.AudioStreamWav { Data = bytes }
                        : new Godot.AudioStreamMP3 { Data = bytes };
                    _player.Stream = stream;
                    float vol = profile.MusicVolume / 250f;
                    _player.VolumeDb = vol >= 1f ? 0f : 20f * MathF.Log10(Math.Max(0.001f, vol));
                    _player.Play();
                    _trackIndex = at;
                    Status = $"playing {zone.Name}/{zone.Tracks[at]}";
                    return;
                }
                catch (Exception ex)
                {
                    Status = $"unplayable {zone.Tracks[at]}: {ex.Message}";
                }
            }

            Stop();
            Status = $"idle ({zone.Name}: no playable tracks)";
        }

        private static void Stop()
        {
            try
            {
                if (_player != null)
                {
                    _player.Stop();
                }
            }
            catch (Exception)
            {
            }

            _currentZone = null;
        }
    }
}
