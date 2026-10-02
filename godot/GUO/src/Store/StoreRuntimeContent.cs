// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Assets;
using GUO.Utility;
using Environment = System.Environment;

namespace GUO.Store;

/// <summary>Startup-only verified overlays. No disk reads or allocations in lookup paths.</summary>
internal sealed class StoreRuntimeContent : IDisposable
{
    internal sealed record Pixels(uint[] Data, int Width, int Height);
    private readonly Dictionary<(string, int), Pixels> _images = new();
    private readonly Dictionary<int, string> _strings = new();
    private readonly Dictionary<int, ushort[]> _hues = new();
    private readonly Dictionary<int, byte[]> _sounds = new();
    private readonly Dictionary<int, byte[]> _music = new();
    private readonly Dictionary<int, List<MultiInfo>> _multis = new();
    private readonly Dictionary<int, StaticTiles> _tiles = new();
    private readonly Dictionary<(int, int, int), AnimationsLoader.FrameInfo[]> _animations = new();
    private readonly Dictionary<int, AnimationGroupsType> _animationTypes = new();
    private readonly StoreMapOverlay _maps = new();
    public void ApplyMap(MapLoader maps, int map) => _maps.Apply(maps, map);
    public void Dispose() => _maps.Dispose();
    public bool TryImage(string type, int id, out Pixels pixels) => _images.TryGetValue((type, id), out pixels);
    public bool TryString(int id, out string value) => _strings.TryGetValue(id, out value);
    public bool TrySound(int id, out byte[] value) => _sounds.TryGetValue(id, out value);
    public bool TryMusic(int id, out byte[] value) => _music.TryGetValue(id, out value);
    public bool TryMulti(int id, out List<MultiInfo> value) => _multis.TryGetValue(id, out value);
    public bool TryAnimation(int id, int action, int direction, out AnimationsLoader.FrameInfo[] frames) => _animations.TryGetValue((id, action, direction), out frames);
    public bool TryAnimationType(int id, out AnimationGroupsType type) => _animationTypes.TryGetValue(id, out type);

    public static StoreRuntimeContent LoadConfigured(UOFileManager files, string language)
    {
        string path = Environment.GetEnvironmentVariable("UO_CONTENT_LOCK");
        string root = Environment.GetEnvironmentVariable("UO_CONTENT_STORE");
        if (string.IsNullOrWhiteSpace(root)) root = ProjectSettings.GlobalizePath("user://store");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(root, ".active-content.json");
            if (!File.Exists(path)) return null;
        }
        using var store = new StoreClient("http://127.0.0.1:18865", root, GUO.Configuration.PlatformDefaults.CurrentVersion);
        try { return Load(files, language, StoreContentLock.Read(path), store); }
        catch (Exception ex) when (SessionLock != null && Path.GetFullPath(path) == Path.GetFullPath(SessionLock))
        {
            // Nothing was applied: Load stages every component before it changes a loader.
            GD.PrintErr("[GUO] shard content not mounted: " + ex.Message);
            SessionLockFailed?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>The lock a shard session mounts (Main), and what to do when it can't be:
    /// the session is dropped and GUO starts with the player's own files.</summary>
    public static string SessionLock { get; set; }
    public static Action<string> SessionLockFailed { get; set; }

    internal static StoreRuntimeContent Load(UOFileManager files, string language, StoreContentLock contentLock, StoreClient store, bool apply = true)
    {
        var snapshot = contentLock.Verify(store);
        var result = new StoreRuntimeContent();
        long pixelsTotal = 0;
        long payloadTotal = 0;
        var wearables = new List<(StoreComponent Component, byte[] Data)>();
        var asciiGlyphs = new Dictionary<(int Font, int Code), FontCharacterData>();
        var unicodeGlyphs = new Dictionary<(int Font, int Code), FontCharacterDataUnicode>();
        foreach (var pack in snapshot.Packs.Values)
            foreach (var component in pack.Manifest.Components ?? new())
            {
                if (component.Target == "server" || component.Type == "script") continue; // Scripts have their own explicit approval/enable/run lifecycle.
                StorePack.Require(component.Type is "static" or "land" or "texmap" or "gump" or "hue" or "translation" or "sound" or "music" or "multi" or "tiledata" or "light" or "animation" or "wearable" or "font" or "map", "No client consumer for " + component.Type);
                byte[] data = pack.ReadPayload(component.Entry);
                payloadTotal += data.Length;
                StorePack.Require(payloadTotal <= 256 * 1024 * 1024, "Content payload memory budget exceeded");
                if (component.Type == "wearable") { wearables.Add((component, data)); continue; }
                if (component.Type == "translation")
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.GetProperty("locale").GetString() != (string.IsNullOrEmpty(language) ? "enu" : language)) continue;
                    foreach (var row in doc.RootElement.GetProperty("strings").EnumerateObject())
                    {
                        StorePack.Require(int.TryParse(row.Name, out int key) && key >= 0, "Invalid cliloc number");
                        string text = row.Value.GetString();
                        StorePack.Require(text != null && text.Length <= 65536 && result._strings.TryAdd(key, text), "Invalid or conflicting translation");
                    }
                    continue;
                }
                string identity = pack.Id + ":" + component.Id;
                StorePack.Require(contentLock.Bindings.TryGetValue(identity, out var binding), "Missing numeric binding: " + identity);
                int id = binding.Id;
                if (component.Type == "animation")
                {
                    using var doc = JsonDocument.Parse(data);
                    var rootNode = doc.RootElement;
                    StorePack.Require(Enum.TryParse<AnimationGroupsType>(rootNode.GetProperty("group_type").GetString(), out var groupType) && Enum.IsDefined(groupType), "Invalid animation group type");
                    result._animationTypes.Add(id, groupType);
                    var sequences = rootNode.GetProperty("sequences");
                    StorePack.Require(sequences.GetArrayLength() is > 0 and <= 400, "Invalid animation sequence count");
                    foreach (var sequence in sequences.EnumerateArray())
                    {
                        int action = sequence.GetProperty("action").GetInt32(), dir = sequence.GetProperty("direction").GetInt32();
                        StorePack.Require(action >= 0 && action < AnimationsLoader.MAX_ACTIONS && dir >= 0 && dir < AnimationsLoader.MAX_DIRECTIONS, "Invalid animation action/direction");
                        var rows = sequence.GetProperty("frames");
                        StorePack.Require(rows.GetArrayLength() is > 0 and <= 255, "Invalid animation frame count");
                        var frames = new AnimationsLoader.FrameInfo[rows.GetArrayLength()];
                        int frameIndex = 0;
                        foreach (var row in rows.EnumerateArray())
                        {
                            var pixels = DecodeImage(pack.ReadPayload(row.GetProperty("image").GetString()), "animation", ref pixelsTotal);
                            frames[frameIndex] = new AnimationsLoader.FrameInfo { Num = frameIndex, Width = (short)pixels.Width, Height = (short)pixels.Height, Pixels = pixels.Data,
                                CenterX = row.GetProperty("center_x").GetInt16(), CenterY = row.GetProperty("center_y").GetInt16() };
                            frameIndex++;
                        }
                        StorePack.Require(result._animations.TryAdd((id, action, dir), frames), "Duplicate animation sequence");
                    }
                    continue;
                }
                if (component.Type == "map") { result._maps.Add(id, data, files.Maps); continue; }
                if (component.Type == "font")
                {
                    using var doc = JsonDocument.Parse(data);
                    var glyphs = doc.RootElement.GetProperty("glyphs");
                    StorePack.Require(glyphs.GetArrayLength() is > 0 and <= 65536, "Invalid glyph count");
                    foreach (var glyph in glyphs.EnumerateArray())
                    {
                        string encoding = glyph.GetProperty("encoding").GetString();
                        int code = glyph.GetProperty("codepoint").GetInt32();
                        StorePack.Require(encoding is "ascii" or "unicode", "Unknown glyph encoding");
                        StorePack.Require(encoding == "ascii" ? id < files.Fonts.FontCount && code is >= 32 and <= 255
                            : id < 20 && files.Fonts.UnicodeFontExists((byte)id) && code is >= 32 and <= 65535 && code is not (>= 0xd800 and <= 0xdfff), "Invalid font slot or codepoint");
                        var pixels = DecodeImage(pack.ReadPayload(glyph.GetProperty("image").GetString()), "font", ref pixelsTotal);
                        if (encoding == "ascii")
                        {
                            var colors = new ushort[pixels.Data.Length];
                            for (int i = 0; i < colors.Length; i++) colors[i] = pixels.Data[i] == 0 ? (ushort)0 : HuesHelper.Color32To16(pixels.Data[i]);
                            StorePack.Require(asciiGlyphs.TryAdd((id, code), new FontCharacterData((byte)pixels.Width, (byte)pixels.Height, colors)), "Duplicate ASCII glyph");
                        }
                        else
                        {
                            int stride = (pixels.Width + 7) / 8;
                            var mask = new byte[stride * pixels.Height];
                            for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
                                if (pixels.Data[y * pixels.Width + x] != 0) mask[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                            sbyte ox = glyph.TryGetProperty("offset_x", out var value) ? value.GetSByte() : (sbyte)0;
                            sbyte oy = glyph.TryGetProperty("offset_y", out value) ? value.GetSByte() : (sbyte)0;
                            StorePack.Require(unicodeGlyphs.TryAdd((id, code), new FontCharacterDataUnicode((sbyte)pixels.Width, (sbyte)pixels.Height, ox, oy, mask)), "Duplicate Unicode glyph");
                        }
                    }
                    continue;
                }
                if (component.Type == "sound") { result._sounds.Add(id, DecodeWave(data)); continue; }
                if (component.Type == "music") { result._music.Add(id, DecodeWave(data)); continue; }
                if (component.Type == "multi")
                {
                    using var doc = JsonDocument.Parse(data);
                    var rows = doc.RootElement.GetProperty("items");
                    StorePack.Require(rows.GetArrayLength() is > 0 and <= 65536 && id < MultiLoader.MAX_MULTI_DATA_INDEX_COUNT, "Invalid multi");
                    var items = new List<MultiInfo>();
                    foreach (var row in rows.EnumerateArray())
                        items.Add(new MultiInfo { ID = row.GetProperty("graphic").GetUInt16(), X = row.GetProperty("x").GetInt16(), Y = row.GetProperty("y").GetInt16(), Z = row.GetProperty("z").GetInt16(), IsVisible = row.GetProperty("visible").GetBoolean() });
                    result._multis.Add(id, items); continue;
                }
                if (component.Type == "tiledata")
                {
                    using var doc = JsonDocument.Parse(data);
                    var row = doc.RootElement;
                    StoreTileDefinition.Validate(row);
                    StorePack.Require(id < files.TileData.StaticData.Length, "Tiledata index exceeds client capacity");
                    var tile = files.TileData.StaticData[id];
                    if (row.TryGetProperty("flags", out var v)) tile.Flags = (TileFlag)v.GetUInt64();
                    if (row.TryGetProperty("height", out v)) tile.Height = v.GetByte();
                    if (row.TryGetProperty("weight", out v)) tile.Weight = v.GetByte();
                    if (row.TryGetProperty("layer", out v)) tile.Layer = v.GetByte();
                    if (row.TryGetProperty("animation", out v)) tile.AnimID = v.GetUInt16();
                    if (row.TryGetProperty("light", out v)) tile.LightIndex = v.GetUInt16();
                    if (row.TryGetProperty("name", out v)) { tile.Name = v.GetString(); StorePack.Require(tile.Name != null && tile.Name.Length <= 20, "Invalid tile name"); }
                    result._tiles.Add(id, tile); continue;
                }
                if (component.Type == "hue")
                {
                    using var doc = JsonDocument.Parse(data);
                    var rows = doc.RootElement.GetProperty("colors");
                    StorePack.Require(rows.GetArrayLength() == 32 && id <= files.Hues.HuesCount, "Invalid hue table or index");
                    var colors = new ushort[32];
                    for (int i = 0; i < 32; i++) { colors[i] = rows[i].GetUInt16(); StorePack.Require(colors[i] <= 32767, "Invalid hue color"); }
                    result._hues.Add(id, colors);
                    continue;
                }
                if (component.Type is "static" or "land")
                    StorePack.Require(id + (component.Type == "static" ? 0x4000 : 0) < files.Arts.File.Entries.Length,
                        "Art binding exceeds this client's renderer capacity");
                result._images.Add((component.Type, id), DecodeImage(data, component.Type, ref pixelsTotal));
            }
        var equippedArt = new HashSet<int>();
        foreach (var (component, data) in wearables)
        {
            using var doc = JsonDocument.Parse(data);
            var row = doc.RootElement;
            int Resolve(string field, string type)
            {
                string reference = row.GetProperty(field).GetString();
                StorePack.Require(component.References != null && component.References.Contains(reference)
                    && contentLock.Bindings.TryGetValue(reference, out var unused), "Undeclared wearable reference");
                var binding = contentLock.Bindings[reference];
                StorePack.Require(binding.Type == type, "Wearable reference type mismatch");
                return binding.Id;
            }
            int art = Resolve("art", "static"), animation = Resolve("animation", "animation"), paperdoll = Resolve("paperdoll", "gump");
            StorePack.Require(equippedArt.Add(art), "Conflicting wearable definitions for one item");
            byte layer = row.GetProperty("layer").GetByte();
            StorePack.Require(layer is > 0 and <= 29 && art < files.TileData.StaticData.Length, "Invalid equipment layer/art");
            StorePack.Require(paperdoll == 50000 + animation || paperdoll == 60000 + animation, "Paperdoll ID must follow classic body-to-gump mapping");
            StorePack.Require(result._animationTypes.TryGetValue(animation, out var type) && type is AnimationGroupsType.Human or AnimationGroupsType.Equipment, "Wearable animation must be Human or Equipment");
            var tile = result._tiles.TryGetValue(art, out var patched) ? patched : files.TileData.StaticData[art];
            tile.AnimID = (ushort)animation; tile.Layer = layer; tile.Flags |= TileFlag.Wearable;
            result._tiles[art] = tile;
        }
        // Commit only after all components and payloads validate.
        if (!apply) return result;
        result._maps.Compile();
        for (int map = 0; map < MapLoader.MAPS_COUNT; map++) result.ApplyMap(files.Maps, map);
        foreach (var (key, glyph) in asciiGlyphs) files.Fonts.ApplyContentGlyph(key.Font, key.Code, glyph);
        foreach (var (key, glyph) in unicodeGlyphs) files.Fonts.ApplyContentGlyph(key.Font, key.Code, glyph);
        foreach (var (id, colors) in result._hues)
            for (int i = 0; i < 32; i++) files.Hues.HuesRange[(id - 1) / 8].Entries[(id - 1) % 8].ColorTable[i] = colors[i];
        foreach (var (id, tile) in result._tiles) files.TileData.StaticData[id] = tile;
        return result;
    }

    private static Pixels DecodeImage(byte[] data, string type, ref long pixelsTotal)
    {
                StorePack.Require(data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "Expected PNG image");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)), h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
                int max = type == "font" ? 127 : type == "gump" ? 2048 : 1024;
                StorePack.Require(w > 0 && h > 0 && w <= max && h <= max, "Image dimensions exceed limit");
                StorePack.Require(type != "land" || w == 44 && h == 44, "Land must be 44x44");
                StorePack.Require(type != "texmap" || w == h && w is 64 or 128, "Texmap must be 64 or 128 square");
                pixelsTotal += w * h;
                StorePack.Require(pixelsTotal <= 64 * 1024 * 1024, "Content image memory budget exceeded");
                using var image = new Image();
                StorePack.Require(image.LoadPngFromBuffer(data) == Error.Ok, "Invalid PNG image");
                image.Convert(Image.Format.Rgba8);
                byte[] rgba = image.GetData();
                var pixels = new uint[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int p = (y * (int)w + x) * 4;
                    bool land = type == "land", opaque = land || type == "texmap";
                    if (land && (y < 22 ? x < 21-y || x >= 23+y : x < y-22 || x >= 66-y)) continue;
                    if (!opaque && rgba[p + 3] < 128) continue;
                    ushort color = (ushort)((rgba[p] >> 3) << 10 | (rgba[p + 1] >> 3) << 5 | rgba[p + 2] >> 3);
                    if (!opaque && color == 0) color = 0x421;
                    pixels[y * w + x] = HuesHelper.Color16To32(color) | 0xff000000;
                }
                return new Pixels(pixels, (int)w, (int)h);
    }

    private static byte[] DecodeWave(byte[] data)
    {
        StorePack.Require(data.Length >= 44 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WAVE", "Expected RIFF WAVE");
        bool format = false; byte[] pcm = null;
        for (int offset = 12; offset + 8 <= data.Length;)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            StorePack.Require(length <= data.Length - offset - 8, "Truncated WAVE chunk");
            string kind = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var chunk = data.AsSpan(offset + 8, (int)length);
            if (kind == "fmt ")
            {
                StorePack.Require(length >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(chunk) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) == 22050
                    && BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) == 16, "Sound requires PCM 22050Hz mono 16-bit");
                format = true;
            }
            if (kind == "data") { StorePack.Require(pcm == null && length > 0 && length % 2 == 0, "Invalid WAVE data"); pcm = chunk.ToArray(); }
            offset += 8 + (int)length + (int)(length & 1);
        }
        StorePack.Require(format && pcm != null, "Incomplete WAVE");
        return pcm;
    }
}
