using System;
using System.IO;
using Godot;
using GUO.Assets;
using GUO.Utility;
using Environment = System.Environment;

namespace GUO.Store;

public partial class StoreContentProbe : Node
{
    public override void _Ready()
    {
        try
        {
            using var files = new UOFileManager(GUO.Host.UoDataProbe.ParseVersion(Environment.GetEnvironmentVariable("UO_CLIENT_VERSION") ?? "7.0.107.76"), Environment.GetEnvironmentVariable("UO_CLIENT_DATA"));
            files.Load(false, "enu");
            var art = files.Arts.GetArt(0x4000 + 0x0e75);
            StorePack.Require(art.Width == 32 && art.Height == 48 && art.Pixels[0] == (HuesHelper.Color16To32((ushort)(15 << 10 | 18 << 5 | 20)) | 0xff000000), "Static overlay pixels differ");
            var land = files.Arts.GetArt(0x244);
            int count = 0; foreach (uint pixel in land.Pixels) if (pixel != 0) count++;
            StorePack.Require(count == 1012 && land.Width == 44 && land.Height == 44, "Land diamond differs");
            var tex = files.Texmaps.GetTexmap(1);
            StorePack.Require(tex.Width == 64 && tex.Pixels[0] == (HuesHelper.Color16To32((ushort)(10 << 10 | 18 << 5 | 9)) | 0xff000000), "Texmap pixels differ");
            var gump = files.Gumps.GetGump(100);
            StorePack.Require(gump.Width == 120 && gump.Height == 80, "Gump overlay absent");
            for (int i = 0; i < 32; i++) StorePack.Require(files.Hues.HuesRange[4].Entries[0].ColorTable[i] == i * 1057, "Hue overlay differs");
            StorePack.Require(files.Clilocs.GetString(3100001) == "Welcome to the example shard", "Translation overlay absent");
            StorePack.Require(files.Sounds.TryGetSound(2000, out var sound, out _) && sound.Length == 4410, "Sound overlay absent");
            var sounds = new GUO.Renderer.Sounds.Sound(files.Sounds);
            StorePack.Require(sounds.GetMusic(100) is StoreMusic, "Music consumer absent");
            var light = files.Lights.GetLight(1);
            StorePack.Require(light.Width == 16 && light.Height == 16, "Light overlay absent");
            var multi = files.Multis.GetMultis(1);
            StorePack.Require(multi.Count == 1 && multi[0].ID == 3701 && multi[0].IsVisible, "Multi overlay absent");
            StorePack.Require(files.TileData.StaticData[3701].Name == "Example stone" && files.TileData.StaticData[3701].Height == 8, "Tiledata overlay absent");
            StorePack.Require(files.TileData.StaticData[3701].IsWearable && files.TileData.StaticData[3701].AnimID == 400 && files.TileData.StaticData[3701].Layer == 5, "Wearable linkage absent");
            var paperdoll = files.Gumps.GetGump(50400);
            StorePack.Require(paperdoll.Width == 32 && paperdoll.Height == 48, "Paperdoll art absent");
            var animations = new GUO.Renderer.Animations.Animations(files.Animations);
            for (byte dir = 0; dir < 5; dir++)
            {
                var frames = animations.GetAnimationFrames(400, 0, dir, out _, out _);
                StorePack.Require(frames.Length == 2 && frames[0].Texture != null && frames[0].Center.X == 16 && frames[1].Center.Y == 1, "Animation atlas/anchor mismatch");
            }
            string output = Environment.GetEnvironmentVariable("UO_CONTENT_PROOF_IMAGE");
            if (!string.IsNullOrWhiteSpace(output))
            {
                using var image = Image.CreateEmpty(340, 150, false, Image.Format.Rgba8);
                image.Fill(new Color(0.08f, 0.08f, 0.1f));
                Draw(image, art.Pixels, art.Width, art.Height, 12, 12);
                Draw(image, land.Pixels, land.Width, land.Height, 70, 12);
                Draw(image, tex.Pixels, tex.Width, tex.Height, 130, 12);
                Draw(image, gump.Pixels, gump.Width, gump.Height, 210, 12);
                StorePack.Require(image.SavePng(output) == Error.Ok, "Could not save decoded pixel evidence");
            }
            GD.Print("[content probe] PASS: installed statics, 1012-pixel land diamond, texmap, gump, 32 hue colors, cliloc, PCM sound/music, light, multi, tiledata, wearable and five-direction animation atlas/anchors");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("[content probe] FAIL: " + e); GetTree().Quit(1); }
    }

    private static void Draw(Image image, ReadOnlySpan<uint> pixels, int width, int height, int x0, int y0)
    {
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            uint c = pixels[y * width + x];
            if ((c >> 24) != 0) image.SetPixel(x0+x, y0+y, new Color((c & 255)/255f, ((c >> 8)&255)/255f, ((c >> 16)&255)/255f));
        }
    }
}
