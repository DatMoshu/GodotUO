// SPDX-License-Identifier: BSD-2-Clause

using GUO.IO;
using GUO.Utility;
using GUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GUO.Assets
{
    public sealed class ArtLoader : UOFileLoader
    {
        private UOFile _file;
        public const int MAX_LAND_DATA_INDEX_COUNT = 0x4000;
        public const int MAX_STATIC_DATA_INDEX_COUNT = 0x14000;

        /// <summary>
        /// Art of the shard's own, as loose files, by the number the server asks for.
        ///
        /// Two dictionaries because they are two formats sharing one archive, and nothing but the
        /// colour is common to them: a static is run-length with its size in front, a land tile is
        /// 1,012 raw pixels of a diamond and nothing else at all.
        ///
        /// The archive uses 39,516 of the 81,920 static numbers and its highest is 62,763, so
        /// there is room for close to twenty thousand of ours above it. Land is the other way
        /// round: all 16,384 numbers exist and its highest is the last of them, so a land file
        /// here can only ever replace ground the client already draws, never add new ground.
        /// </summary>
        private readonly Dictionary<int, string> _ourStatics = new Dictionary<int, string>();

        private readonly Dictionary<int, string> _ourLand = new Dictionary<int, string>();

        // PORT DEVIATION (GUO): Enhanced HD repaint detail (pre-downscale
        // PNGs under overrides/HD/Art). Path dicts only; no decode here.
        private readonly Dictionary<int, string> _hdStatics = new Dictionary<int, string>();

        private readonly Dictionary<int, string> _hdLand = new Dictionary<int, string>();

        public ArtLoader(UOFileManager fileManager) : base(fileManager)
        {
        }


        public UOFile File => _file;


        public override void Load()
        {
            string filePath = FileManager.GetUOFilePath("artLegacyMUL.uop");

            if (FileManager.IsUOPInstallation && System.IO.File.Exists(filePath))
            {
                _file = new UOFileUop(filePath, "build/artlegacymul/{0:D8}.tga");
            }
            else
            {
                filePath = FileManager.GetUOFilePath("art.mul");
                string idxPath = FileManager.GetUOFilePath("artidx.mul");

                if (System.IO.File.Exists(filePath) && System.IO.File.Exists(idxPath))
                {
                    _file = new UOFileMul(filePath, idxPath);
                }
            }

            _file.FillEntries();

            LoadOurs();
        }

        /// <summary>
        /// Find the shard's own art once, at load, rather than asking the disk every time
        /// something is drawn.
        ///
        /// A File.Exists per tile would be a disk hit for every square of ground on the screen,
        /// which is several hundred of them, sixty times a second.
        /// </summary>
        /// <summary>
        /// Player-authored art overrides (the repaint gump's home): loose
        /// .art files under GUO_ART_OVERRIDE (else %APPDATA%/GUO/overrides),
        /// Art/Statics/&lt;id&gt;.art and Art/Land/&lt;id&gt;.art, in raw
        /// archive bytes. Never the install: the client reads its own data
        /// read-only and paints these over it.
        /// </summary>
        public static string OverrideDir()
        {
            string dir = Environment.GetEnvironmentVariable("GUO_ART_OVERRIDE");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GUO", "overrides");
        }

        /// <summary>Re-reads the override folders (after the repaint gump writes).</summary>
        public void RescanOverrides()
        {
            LoadOurs();
        }

        private void LoadOurs()
        {
            _ourStatics.Clear();
            _ourLand.Clear();
            _hdStatics.Clear();
            _hdLand.Clear();

            // Two folders and not one, keyed by the numbers a shard author actually types - the
            // item id a `new Item(0x2818)` uses, and the land tile id. The archive's own indexing
            // puts statics 0x4000 along from land in a single run, and nobody thinks in those.
            //
            // PORT DEVIATION (GUO): the override folder paints first, so player
            // art wins over both the install and the data folder, which only
            // fill numbers the overrides leave alone.
            string over = OverrideDir();
            Gather(Path.Combine(over, "Art", "Statics"),
                   _ourStatics, MAX_STATIC_DATA_INDEX_COUNT - MAX_LAND_DATA_INDEX_COUNT);
            Gather(Path.Combine(over, "Art", "Land"),
                   _ourLand, MAX_LAND_DATA_INDEX_COUNT);

            Gather(Path.Combine(FileManager.BasePath, "Art", "Statics"),
                   _ourStatics, MAX_STATIC_DATA_INDEX_COUNT - MAX_LAND_DATA_INDEX_COUNT, false);
            Gather(Path.Combine(FileManager.BasePath, "Art", "Land"),
                   _ourLand, MAX_LAND_DATA_INDEX_COUNT, false);

            // PORT DEVIATION (GUO): Enhanced HD PNGs (the repaint gump's
            // pre-downscale saves). Same two-folder shape, .png only.
            GatherHd(Path.Combine(over, "HD", "Art", "Statics"),
                   _hdStatics, MAX_STATIC_DATA_INDEX_COUNT - MAX_LAND_DATA_INDEX_COUNT);
            GatherHd(Path.Combine(over, "HD", "Art", "Land"),
                   _hdLand, MAX_LAND_DATA_INDEX_COUNT);
            GatherHd(Path.Combine(FileManager.BasePath, "HD", "Art", "Statics"),
                   _hdStatics, MAX_STATIC_DATA_INDEX_COUNT - MAX_LAND_DATA_INDEX_COUNT, false);
            GatherHd(Path.Combine(FileManager.BasePath, "HD", "Art", "Land"),
                   _hdLand, MAX_LAND_DATA_INDEX_COUNT, false);

            if (_ourStatics.Count > 0 || _ourLand.Count > 0)
            {
                Log.Trace($"{_ourStatics.Count} static(s) and {_ourLand.Count} land tile(s) of our own");
            }

            if (_hdStatics.Count > 0 || _hdLand.Count > 0)
            {
                Log.Trace($"{_hdStatics.Count} static(s) and {_hdLand.Count} land tile(s) of HD");
            }
        }

        /// <summary>
        /// Enhanced HD path lookup (no decode in the loader). True when an
        /// HD PNG exists for this graphic; the Art cache decodes it.
        /// </summary>
        public bool TryGetHdPath(bool isLand, int graphic, out string path)
        {
            if (isLand)
            {
                return _hdLand.TryGetValue(graphic, out path);
            }

            return _hdStatics.TryGetValue(graphic, out path);
        }

        /// <summary>Enhanced HD root (overrides/HD/Art).</summary>
        public static string HdDir()
        {
            return Path.Combine(OverrideDir(), "HD", "Art");
        }

        /// <summary>Enhanced HD PNG path for one graphic.</summary>
        public static string HdPathFor(bool isLand, ushort graphic)
        {
            return Path.Combine(HdDir(), isLand ? "Land" : "Statics", $"{graphic}.png");
        }

        /// <summary>Enhanced HD manifest (one JSON dict of rows).</summary>
        public static string HdManifestPath()
        {
            return Path.Combine(HdDir(), "manifest.json");
        }

        /// <summary>
        /// One Enhanced HD manifest row. PNG without a row still loads;
        /// the row is provenance (what generation made this PNG).
        /// </summary>
        public sealed class HdManifestRow
        {
            [System.Text.Json.Serialization.JsonPropertyName("native_w")]
            public int NativeW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("native_h")]
            public int NativeH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("hd_w")]
            public int HdW { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("hd_h")]
            public int HdH { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("scale")]
            public double Scale { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("prompt")]
            public string Prompt { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("seed")]
            public int Seed { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("backend")]
            public string Backend { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("sampler")]
            public string Sampler { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("scheduler")]
            public string Scheduler { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("steps")]
            public int Steps { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("cfg")]
            public double Cfg { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("denoise")]
            public double Denoise { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("masked")]
            public bool Masked { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha256")]
            public string Sha256 { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("created_utc")]
            public string CreatedUtc { get; set; } = "";
        }

        private static string HdManifestKey(bool isLand, ushort graphic)
        {
            return $"{(isLand ? "Land" : "Statics")}/{graphic}";
        }

        private static Dictionary<string, HdManifestRow> ReadHdManifest()
        {
            try
            {
                string path = HdManifestPath();
                if (!System.IO.File.Exists(path))
                {
                    return new Dictionary<string, HdManifestRow>();
                }

                string json = System.IO.File.ReadAllText(path);
                var rows = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, HdManifestRow>>(json);
                return rows ?? new Dictionary<string, HdManifestRow>();
            }
            catch (Exception e)
            {
                Log.Warn($"HD manifest unreadable: {e.Message}");
                return new Dictionary<string, HdManifestRow>();
            }
        }

        private static void WriteHdManifestAtomic(Dictionary<string, HdManifestRow> rows)
        {
            string path = HdManifestPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = System.Text.Json.JsonSerializer.Serialize(rows,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            try
            {
                System.IO.File.Move(tmp, path, true);
            }
            catch
            {
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                    }

                    System.IO.File.Move(tmp, path);
                }
                catch (Exception e)
                {
                    Log.Warn($"HD manifest write failed: {e.Message}");
                }
            }
        }

        /// <summary>Upserts one Enhanced HD manifest row (atomic write).</summary>
        public static void WriteHdManifestRow(bool isLand, ushort graphic, HdManifestRow row)
        {
            try
            {
                var rows = ReadHdManifest();
                rows[HdManifestKey(isLand, graphic)] = row;
                WriteHdManifestAtomic(rows);
            }
            catch (Exception e)
            {
                Log.Warn($"HD manifest row write failed: {e.Message}");
            }
        }

        /// <summary>Deletes one Enhanced HD manifest row, if present.</summary>
        public static void RemoveHdManifestRow(bool isLand, ushort graphic)
        {
            try
            {
                var rows = ReadHdManifest();
                if (rows.Remove(HdManifestKey(isLand, graphic)))
                {
                    WriteHdManifestAtomic(rows);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"HD manifest row delete failed: {e.Message}");
            }
        }

        /// <summary>Reads one Enhanced HD manifest row, if present.</summary>
        public static bool TryGetHdManifestRow(bool isLand, ushort graphic, out HdManifestRow row)
        {
            row = null;
            try
            {
                var rows = ReadHdManifest();
                return rows.TryGetValue(HdManifestKey(isLand, graphic), out row);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SHA-256 hex of bytes (the HD manifest's integrity field).</summary>
        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        /// <summary>Deletes one Enhanced HD PNG plus its manifest row.</summary>
        public static void DeleteHd(bool isLand, ushort graphic)
        {
            try
            {
                string path = HdPathFor(isLand, graphic);
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"HD delete failed: {e.Message}");
            }

            RemoveHdManifestRow(isLand, graphic);
        }

        private static void Gather(string folder, Dictionary<int, string> into, int limit, bool overwrite = true)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.art"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)
                    && id >= 0 && id < limit && (overwrite || !into.ContainsKey(id)))
                {
                    into[id] = path;
                }
            }
        }

        private static void GatherHd(string folder, Dictionary<int, string> into, int limit, bool overwrite = true)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.png"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)
                    && id >= 0 && id < limit && (overwrite || !into.ContainsKey(id)))
                {
                    into[id] = path;
                }
            }
        }

        private static byte[] Slurp(string path)
        {
            try
            {
                return System.IO.File.ReadAllBytes(path);
            }
            catch (IOException e)
            {
                Log.Warn($"could not read {path}: {e.Message}");

                return null;
            }
        }

        /// <summary>
        /// One static of ours off the disk: the same bytes the archive would have held.
        ///
        /// The file carries its own size, which is not a format of our own - it is exactly what
        /// the archive stores, header and all, so the reader below is the archive's reader.
        /// </summary>
        private static uint[] OurArt(string path, out short width, out short height)
        {
            width = 0;
            height = 0;

            var raw = Slurp(path);

            if (raw == null || raw.Length < 8)
            {
                Log.Warn($"{Path.GetFileName(path)} is too short to be static art");

                return null;
            }

            width = (short)(raw[4] | (raw[5] << 8));
            height = (short)(raw[6] | (raw[7] << 8));

            // Believed, a bad size asks for a picture of some billions of pixels and takes the
            // client down with it, from inside the drawing code and a long way from this file.
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
            {
                Log.Warn($"{Path.GetFileName(path)} claims to be {width}x{height}");

                return null;
            }

            if (raw.Length < 8 + height * 2)
            {
                Log.Warn($"{Path.GetFileName(path)} has no room for {height} rows");

                return null;
            }

            var buf = new byte[raw.Length - 8];
            Array.Copy(raw, 8, buf, 0, buf.Length);

            return Runs(buf, width, height);
        }

        /// <summary>One land tile of ours: 1,012 pixels of diamond and nothing else.</summary>
        private static uint[] OurLand(string path)
        {
            var raw = Slurp(path);

            if (raw == null)
            {
                return null;
            }

            // 44 x 44 is 1,936, and a land tile is not that. The four corners are outside the
            // diamond and are not stored, so it is 1,012 pixels - 2,024 bytes, always.
            if (raw.Length < 2024)
            {
                Log.Warn($"{Path.GetFileName(path)} is {raw.Length} bytes; a land tile is 2024");

                return null;
            }

            return Diamond(raw);
        }

        // public Rectangle GetRealArtBounds(int index) =>
        //     index + 0x4000 >= _spriteInfos.Length
        //         ? Rectangle.Empty
        //         : _spriteInfos[index + 0x4000].ArtBounds;

        private static uint[] LoadLand(UOFile file, ref readonly UOFileIndex entry, out short width, out short height)
        {
            if (entry.Length == 0)
            {
                width = 0;
                height = 0;

                return Array.Empty<uint>();
            }

            width = 44;
            height = 44;

            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, SeekOrigin.Begin);

            var raw = new byte[2024];
            file.Read(raw);

            return Diamond(raw);
        }

        /// <summary>
        /// The 1,012 pixels of a land tile, laid into a 44 by 44 square.
        ///
        /// Two triangles meeting in the middle: the top half widens by two pixels a row and the
        /// bottom half narrows by two. The corners of the square are never written and stay
        /// transparent, because they are not part of the tile.
        ///
        /// There is no transparency inside the diamond and no colour reserved for it, so a colour
        /// of zero here is simply black - which is the one thing that differs from every other
        /// picture format in the client, and the thing that quietly breaks an encoder that
        /// assumes otherwise.
        /// </summary>
        private static uint[] Diamond(byte[] raw)
        {
            var data = new uint[44 * 44];
            var at = 0;

            for (int i = 0; i < 22; ++i)
            {
                int start = 22 - (i + 1);
                int pos = i * 44 + start;
                int end = start + ((i + 1) << 1);

                for (int j = start; j < end; ++j, at += 2)
                {
                    data[pos++] = HuesHelper.Color16To32((ushort)(raw[at] | (raw[at + 1] << 8)))
                                  | 0xFF_00_00_00;
                }
            }

            for (int i = 0; i < 22; ++i)
            {
                int pos = (i + 22) * 44 + i;
                int end = i + ((22 - i) << 1);

                for (int j = i; j < end; ++j, at += 2)
                {
                    data[pos++] = HuesHelper.Color16To32((ushort)(raw[at] | (raw[at + 1] << 8)))
                                  | 0xFF_00_00_00;
                }
            }

            return data;
        }

        private static unsafe uint[] LoadArt(UOFile file, ref readonly UOFileIndex entry, out short width, out short height)
        {
            if (entry.Length == 0)
            {
                width = 0;
                height = 0;

                return Array.Empty<uint>();
            }

            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, SeekOrigin.Begin);

            var flags = file.ReadUInt32();
            width = file.ReadInt16();
            height = file.ReadInt16();

            var buf = new byte[entry.Length];
            file.Read(buf);

            return Runs(buf, width, height);
        }

        /// <summary>
        /// The rows of a static, from wherever they came from.
        ///
        /// A lookup of one 16-bit word per row saying where that row's runs begin, counted in
        /// pairs of bytes from the end of the lookup itself; then, for each row, a gap and a run
        /// length followed by that many colours, until a gap and run of zero end the row.
        ///
        /// The gap is a skip rather than a position, and transparency is those gaps - a static
        /// stores no transparent pixel at all, which is why a tree costs so much less than the
        /// box it stands in.
        /// </summary>
        private static unsafe uint[] Runs(byte[] buf, short width, short height)
        {
            var data = new uint[width * height];

            fixed (byte* startPtr = buf)
            {
                ushort* lineoffsets = (ushort*)startPtr;
                byte* datastart = (byte*)startPtr + height * 2;
                int x = 0;
                int y = 0;
                var ptr = (ushort*)(datastart + lineoffsets[0] * 2);

                while (y < height)
                {
                    ushort xoffs = *ptr++;
                    ushort run = *ptr++;

                    if (xoffs + run >= 2048)
                    {
                        break;
                    }

                    if (xoffs + run != 0)
                    {
                        x += xoffs;
                        int pos = y * width + x;

                        for (int j = 0; j < run; ++j, ++pos)
                        {
                            ushort val = *ptr++;

                            if (val != 0)
                            {
                                data[pos] = HuesHelper.Color16To32(val) | 0xFF_00_00_00;
                            }
                        }

                        x += run;
                    }
                    else
                    {
                        x = 0;
                        ++y;
                        ptr = (ushort*)(datastart + lineoffsets[y] * 2);
                    }
                }
            }

            return data;
        }

        private static void AddBlackBorder(Span<uint> pixels, int width, int height)
        {
            for (int yy = 0; yy < height; yy++)
            {
                int startY = yy != 0 ? -1 : 0;
                int endY = yy + 1 < height ? 2 : 1;

                for (int xx = 0; xx < width; xx++)
                {
                    ref uint pixel = ref pixels[yy * width + xx];

                    if (pixel == 0)
                    {
                        continue;
                    }

                    int startX = xx != 0 ? -1 : 0;
                    int endX = xx + 1 < width ? 2 : 1;

                    for (int i = startY; i < endY; i++)
                    {
                        int currentY = yy + i;

                        for (int j = startX; j < endX; j++)
                        {
                            int currentX = xx + j;

                            ref uint currentPixel = ref pixels[currentY * width + currentX];

                            if (currentPixel == 0u)
                            {
                                pixel = 0xFF_00_00_00;
                            }
                        }
                    }
                }
            }
        }

        public ArtInfo GetArt(uint idx)
        {
            // PORT DEVIATION (GUO): mounted pack art follows the same atlas and picking path.
            if (FileManager.Content != null && FileManager.Content.TryImage(idx < 0x4000 ? "land" : "static", (int)(idx < 0x4000 ? idx : idx - 0x4000), out var packed))
                return new ArtInfo { Pixels = packed.Data, Width = (short)packed.Width, Height = (short)packed.Height };
            var loadLand = idx < MAX_LAND_DATA_INDEX_COUNT;

            // Ours first, so a file corrects art the archive already has rather than only filling
            // a number it lacks. For land that is the only thing a file can do - every one of the
            // sixteen thousand land numbers is already taken.
            //
            // A broken file falls through to the archive rather than leaving a hole, so a bad
            // import shows the old art and a warning instead of a gap in the ground.
            if (loadLand)
            {
                if (_ourLand.Count > 0 && _ourLand.TryGetValue((int)idx, out string land))
                {
                    var mine = OurLand(land);

                    if (mine != null)
                    {
                        return new ArtInfo { Pixels = mine, Width = 44, Height = 44 };
                    }
                }
            }
            else if (_ourStatics.Count > 0
                     && _ourStatics.TryGetValue((int)(idx - MAX_LAND_DATA_INDEX_COUNT),
                                                out string statik))
            {
                var mine = OurArt(statik, out var mw, out var mh);

                if (mine != null)
                {
                    return new ArtInfo { Pixels = mine, Width = mw, Height = mh };
                }
            }

            ref var entry = ref _file.GetValidRefEntry((int)idx);
            var pixels = loadLand ?
                LoadLand(_file, in entry, out var width, out var height)
                :
                LoadArt(_file, in entry, out width, out height);

            return new ArtInfo()
            {
                Pixels = pixels,
                Width = width,
                Height = height
            };
        }
    }

    public ref struct ArtInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
    }
}
