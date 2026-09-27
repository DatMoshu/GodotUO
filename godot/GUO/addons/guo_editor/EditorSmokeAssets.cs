#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using GUO.Assets;

/// <summary>
/// Phase 5's smoke stage (ADR-0020): the asset overlay, with fixtures drawn
/// here at test time so no UO-derived image is ever stored. Imports a land
/// tile, a static, a gump and a hue into a project of its own, checks the
/// ported loaders now return exactly the saved images, checks the refusals,
/// reverts one and checks the install's comes back, and leaves the project
/// in place for tools/editor_smoke to export and verify.
/// </summary>
public partial class EditorSmoke
{
    public const int FixtureLand = 0x0244;
    public const int FixtureStatic = 0x0E75;
    public const int FixtureGump = 0x0064;
    public const int FixtureHue = 0x0021;

    private readonly Dictionary<string, object> _assetReport = new();

    private void AssetFail(string why)
    {
        _assetReport["ok"] = false;
        _failures.Add($"Assets: {why}");
    }

    /// <summary>Install files the asset overlay must never touch, with their times.</summary>
    private Dictionary<string, DateTime> AssetStamp()
    {
        var d = new Dictionary<string, DateTime>();
        foreach (string f in Directory.GetFiles(_data.ClientData))
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            if (n.StartsWith("art") || n.StartsWith("gump") || n.StartsWith("hues") || n.StartsWith("verdata"))
            {
                d[n] = File.GetLastWriteTimeUtc(f);
            }
        }

        return d;
    }

    // --- fixtures: made here, never read from the install -------------------

    /// <summary>A 44x44 land tile: two gradients crossed, and one black cell (land's 0 is black).</summary>
    public static Image FixtureLandImage()
    {
        Image img = Image.CreateEmpty(44, 44, false, Image.Format.Rgba8);
        for (int y = 0; y < 44; y++)
        {
            for (int x = 0; x < 44; x++)
            {
                img.SetPixel(x, y, Color.Color8((byte)(x * 5), (byte)(y * 5), (byte)((x ^ y) * 4), 255));
            }
        }

        img.SetPixel(22, 22, Color.Color8(0, 0, 0, 255));
        return img;
    }

    /// <summary>
    /// A 30x40 static: a ring with a hole (two spans per row), empty rows at
    /// top and bottom, and opaque black pixels (which must stay visible).
    /// </summary>
    public static Image FixtureStaticImage()
    {
        Image img = Image.CreateEmpty(30, 40, false, Image.Format.Rgba8);
        for (int y = 3; y < 37; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                float dx = x - 14.5f, dy = (y - 20) * 0.8f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                if (r < 14 && r > 5)
                {
                    img.SetPixel(x, y, Color.Color8((byte)(200 - y * 3), (byte)(60 + x * 5), 90, 255));
                }
            }
        }

        img.SetPixel(14, 4, Color.Color8(0, 0, 0, 255));
        img.SetPixel(15, 4, Color.Color8(0, 0, 0, 255));
        return img;
    }

    /// <summary>A 50x20 gump: horizontal bands (long runs), a transparent window, a checker patch.</summary>
    public static Image FixtureGumpImage()
    {
        Image img = Image.CreateEmpty(50, 20, false, Image.Format.Rgba8);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                bool window = x >= 10 && x < 20 && y >= 5 && y < 15;
                bool checker = x >= 30 && x < 40 && y >= 5 && y < 15;
                Color c = window ? new Color(0, 0, 0, 0)
                    : checker ? ((x + y) % 2 == 0 ? Color.Color8(250, 250, 250, 255) : Color.Color8(0, 0, 0, 255))
                    : Color.Color8((byte)(40 + y * 10), 30, (byte)(200 - y * 8), 255);
                img.SetPixel(x, y, c);
            }
        }

        return img;
    }

    /// <summary>A hue strip: 32 colours from dark red to pale gold.</summary>
    public static Image FixtureHueImage()
    {
        Image img = Image.CreateEmpty(32, 1, false, Image.Format.Rgba8);
        for (int i = 0; i < 32; i++)
        {
            img.SetPixel(i, 0, Color.Color8((byte)(60 + i * 6), (byte)(i * 7), (byte)(i * 3), 255));
        }

        return img;
    }

    // --- the stage ------------------------------------------------------------

    private string _assetRoot;
    private Dictionary<string, DateTime> _assetStamp;

    private void RunAssets()
    {
        _report["assets"] = _assetReport;
        _assetStamp = AssetStamp();
        _assetRoot = Path.Combine(_out, $"asset_project{Suffix}");
        _assetReport["project"] = _assetRoot;

        try
        {
            if (Directory.Exists(_assetRoot))
            {
                Directory.Delete(_assetRoot, recursive: true);
            }

            // project.json (the install's fingerprint) so tools/world accepts it.
            WorldProject.OpenOrCreate(_assetRoot, _data.ClientData, _data.ClientVersion).Dispose();
            AssetOverlay assets = _data.OpenAssets(_assetRoot);

            uint landIndex = FixtureLand;
            uint staticIndex = EditorData.LandCount + FixtureStatic;
            Image installStatic = _data.ArtImage(staticIndex);
            _assetReport["install_static_size"] = installStatic != null ? $"{installStatic.GetWidth()}x{installStatic.GetHeight()}" : null;

            // Refusals first: nothing may be written for them.
            string wrongLand = assets.Import(AssetKind.Land, FixtureLand, Image.CreateEmpty(40, 40, false, Image.Format.Rgba8));
            string hugeStatic = assets.Import(AssetKind.Static, FixtureStatic, Image.CreateEmpty(1100, 8, false, Image.Format.Rgba8));
            _assetReport["refused_land_40x40"] = wrongLand;
            _assetReport["refused_static_1100"] = hugeStatic;
            if (wrongLand == null || hugeStatic == null || assets.Count != 0)
            {
                AssetFail("a wrong-sized land tile or an oversized static was accepted");
            }

            Import(assets, AssetKind.Land, FixtureLand, FixtureLandImage());
            Import(assets, AssetKind.Static, FixtureStatic, FixtureStaticImage());
            Import(assets, AssetKind.Gump, FixtureGump, FixtureGumpImage());
            string hueWhy = assets.ImportHue(FixtureHue, FixtureHueImage(), "guo smoke", 0, 31);
            if (hueWhy != null)
            {
                AssetFail($"hue import refused: {hueWhy}");
            }

            int applied = _data.ReapplyAssets(AssetKind.Static, FixtureStatic);
            _assetReport["applied"] = applied;
            if (applied != 4)
            {
                AssetFail($"{applied} replacements applied, expected 4");
            }

            Same("land", _data.ArtImage(landIndex), assets.Load(AssetKind.Land, FixtureLand));
            Same("static", _data.ArtImage(staticIndex), assets.Load(AssetKind.Static, FixtureStatic));
            Same("gump", _data.GumpImage(FixtureGump), assets.Load(AssetKind.Gump, FixtureGump));

            // The black pixels: kept visible in a static, black in land.
            Image st = _data.ArtImage(staticIndex);
            if (st != null && st.GetPixel(14, 4).A8 != 255)
            {
                AssetFail("the static's opaque black pixel came back transparent");
            }

            var (_, colors, _, _) = assets.ReadHue(FixtureHue);
            ref HuesBlock hb = ref _data.Files.Hues.HuesRange[(FixtureHue - 1) >> 3].Entries[(FixtureHue - 1) & 7];
            int hueMatches = 0;
            for (int i = 0; i < 32; i++)
            {
                hueMatches += hb.ColorTable[i] == colors[i] ? 1 : 0;
            }

            _assetReport["hue_colours_matching"] = hueMatches;
            if (hueMatches != 32)
            {
                AssetFail($"hue {FixtureHue}: {hueMatches} of 32 colours match the project");
            }

            // Revert gives the install's static back; import it again for the export.
            assets.Revert(AssetKind.Static, FixtureStatic);
            _data.ReapplyAssets(AssetKind.Static, FixtureStatic);
            Same("static_reverted", _data.ArtImage(staticIndex), installStatic);
            Import(assets, AssetKind.Static, FixtureStatic, FixtureStaticImage());
            _data.ReapplyAssets(AssetKind.Static, FixtureStatic);

            // The Art panel shows what the loaders now return, and says so.
            ArtPanel art = _assets.Panel<ArtPanel>();
            if (art != null)
            {
                art.Search($"0x{FixtureStatic:X4}");
                string text = _inspector.Current?.Text ?? "";
                _assetReport["inspector_says_replaced"] = text.Contains("replaced by the world project");
                if (!text.Contains("replaced by the world project"))
                {
                    AssetFail("the UO Inspector does not say the static is replaced");
                }

                Directory.CreateDirectory(_out);
                _inspector.Current?.Image?.SavePng(Path.Combine(_out, $"asset_static{Suffix}.png"));
            }

            foreach (var (name, when) in AssetStamp())
            {
                if (!_assetStamp.TryGetValue(name, out DateTime before) || before != when)
                {
                    AssetFail($"the install's {name} changed during the asset stage");
                }
            }
        }
        catch (Exception ex)
        {
            AssetFail($"{ex.GetType().Name}: {ex.Message}");
        }

        _assetReport.TryAdd("ok", true);
    }

    private void Import(AssetOverlay assets, AssetKind kind, int id, Image img)
    {
        string why = assets.Import(kind, id, img);
        if (why != null)
        {
            AssetFail($"{kind} 0x{id:X4} import refused: {why}");
        }
    }

    /// <summary>Pixel-for-pixel equality, transparent pixels compared as transparent only.</summary>
    private void Same(string what, Image got, Image want)
    {
        if (got == null || want == null)
        {
            AssetFail($"{what}: {(got == null ? "the loader returned nothing" : "no expected image")}");
            return;
        }

        if (got.GetWidth() != want.GetWidth() || got.GetHeight() != want.GetHeight())
        {
            AssetFail($"{what}: {got.GetWidth()}x{got.GetHeight()}, expected {want.GetWidth()}x{want.GetHeight()}");
            return;
        }

        int diff = 0;
        for (int y = 0; y < got.GetHeight(); y++)
        {
            for (int x = 0; x < got.GetWidth(); x++)
            {
                Color a = got.GetPixel(x, y), b = want.GetPixel(x, y);
                bool clearA = a.A8 == 0, clearB = b.A8 == 0;
                if (clearA != clearB || (!clearA && (a.R8 != b.R8 || a.G8 != b.G8 || a.B8 != b.B8)))
                {
                    diff++;
                }
            }
        }

        _assetReport[$"{what}_pixels_differing"] = diff;
        if (diff != 0)
        {
            AssetFail($"{what}: {diff} pixels differ from the saved image");
        }
    }

    /// <summary>After the World tab boots: its own loaders and renderer see the imported static.</summary>
    private void CheckWorldAssets()
    {
        if (!_world.IsBooted || _data.Assets == null || !_data.Assets.Has(AssetKind.Static, FixtureStatic))
        {
            return;
        }

        ArtInfo art = Client.Game.UO.FileManager.Arts.GetArt(EditorData.LandCount + FixtureStatic);
        Image got = EditorData.FromPixels(art.Pixels, art.Width, art.Height);
        int before = _failures.Count;
        Same("world_static", got, _data.Assets.Load(AssetKind.Static, FixtureStatic));
        _assetReport["world_loader_sees_import"] = _failures.Count == before;
    }
}
#endif
