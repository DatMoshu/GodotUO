namespace GUO.Bootstrap;

using System;
using Godot;
using GUO.Renderer;

/// <summary>
/// Draws through the real <see cref="UltimaBatcher2D"/> and reads the pixels
/// back, to prove the hue actually survives the trip.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0002 carries ClassicUO's (hue index, shader mode, alpha) in a canvas
/// item's modulate colour. That is four bytes doing the work of three floats,
/// and the two halves of it — the C# packing in <c>UltimaBatcher2D.Encode</c>
/// and the GLSL unpacking in <c>uo_hue.gdshader</c> — can disagree silently:
/// the wrong palette row is still a plausible-looking colour. Nothing about
/// that shows up in a compile.
/// </para>
/// <para>
/// So this builds a synthetic palette whose every entry is a known value,
/// draws a known brightness through a known hue, and checks the pixel that
/// comes out is the palette entry it should be. The palette is synthetic on
/// purpose: it makes a wrong row or a wrong column obvious, where real
/// hues.mul rows differ by a shade or two and would hide it.
/// </para>
/// <para>
/// Needs a real frame, so it cannot run under <c>--headless</c> — the dummy
/// renderer never produces one.
/// </para>
/// </remarks>
public partial class BatcherProbe : Node
{
    // The palette geometry the shader assumes, and upstream's IsometricWorld.fx
    // before it: 16 palettes across, 1024 down, 32 entries each.
    private const int HueColumns = 16;
    private const int HueWidth = 32;
    private const int HueRows = 1024;

    private const int Scale = 4;

    private static readonly int[] _testHues = { 1, 17, 33, 100, 2000 };
    private static readonly int[] _testGrays = { 0, 1, 2, 3, 15, 30, 31 };

    private int _failures;

    public static void Run(Node parent)
    {
        var probe = new BatcherProbe();
        parent.AddChild(probe);
        probe.Verify();
    }

    private async void Verify()
    {
        GD.Print("[batcher-probe] drawing through UltimaBatcher2D into a SubViewport");

        Texture2D palette = BuildPalette();
        Texture2D ramp = BuildGrayRamp();

        var host = new Node2D();

        var viewport = new SubViewport
        {
            Size = new Vector2I(Scale * 2, Scale * 2),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };

        viewport.AddChild(host);
        AddChild(viewport);

        var batcher = new UltimaBatcher2D(host.GetCanvasItem())
        {
            HueTexture = palette,
        };

        // First, with no hueing at all: SHADER_NONE passes the sampled texel
        // straight through, so this says what red the shader actually sees.
        // Without it a wrong palette entry and a wrong sample look identical.
        foreach (int gray in _testGrays)
        {
            Color raw = await DrawAndRead(batcher, viewport, ramp, gray, 0);

            GD.Print($"[batcher-probe] raw red at gray {gray,2}: {raw.R8,3}"
                + $"  -> column position {raw.R8 * 32.0 / 255.0:F4}");
        }

        foreach (int hue in _testHues)
        {
            foreach (int gray in _testGrays)
            {
                Color got = await DrawAndRead(batcher, viewport, ramp, gray, hue + 1);
                byte[] want = PaletteEntry(hue, gray);

                Check($"hue {hue} gray {gray}", want, got);
            }
        }

        batcher.Dispose();

        GD.Print($"[batcher-probe] {(_failures == 0 ? "PASS" : "FAIL")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Draws brightness <paramref name="gray"/> under <paramref name="hue"/>
    /// and returns the pixel that comes back. Hue 0 means SHADER_NONE, which
    /// hues nothing and so reports the raw sample.
    /// </summary>
    private async System.Threading.Tasks.Task<Color> DrawAndRead(
        UltimaBatcher2D batcher, SubViewport viewport, Texture2D ramp, int gray, int hue)
    {
        batcher.Begin();

        batcher.Draw(
            ramp,
            new Compat.Rectangle(0, 0, Scale, Scale),
            new Compat.Rectangle(gray, 0, 1, 1),
            ShaderHueTranslator.GetHueVector(hue),
            0f);

        batcher.End();

        await ToSignal(
            RenderingServer.Singleton,
            RenderingServerInstance.SignalName.FramePostDraw);

        return viewport.GetTexture().GetImage().GetPixel(1, 1);
    }

    private void Check(string what, byte[] want, Color got)
    {
        // Exact. Nearest sampling, no filtering and an 8-bit target mean there
        // is nothing in this path that should round, and a tolerance here
        // would hide precisely the kind of off-by-one that costs a day.
        bool ok = want[0] == got.R8 && want[1] == got.G8 && want[2] == got.B8;

        if (!ok)
        {
            _failures++;

            GD.PrintErr(
                $"[batcher-probe]   {what}: want "
                + $"({want[0]},{want[1]},{want[2]}) got ({got.R8},{got.G8},{got.B8})");
        }
    }

    /// <summary>
    /// The colour this probe puts at hue <paramref name="hue"/>, brightness
    /// <paramref name="gray"/>, as raw bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The addressing is read straight off the shader's get_rgb: column is
    /// <c>hue % 16</c>, row is <c>hue / 16</c>, and the brightness picks one of
    /// the 32 entries across the column.
    /// </para>
    /// <para>
    /// Each channel varies with a different one of the three coordinates, so a
    /// wrong row, a wrong column and a wrong brightness each show up as a
    /// different channel being off rather than as one undifferentiated miss.
    /// </para>
    /// </remarks>
    private static byte[] PaletteEntry(int hue, int gray)
    {
        int column = hue % HueColumns;
        int row = hue / HueColumns;

        return new[] { (byte)(row & 0xFF), (byte)(column * 16), (byte)(gray * 8) };
    }

    /// <remarks>
    /// Built from raw bytes rather than <c>Image.SetPixel</c>. SetPixel takes a
    /// float Color and converts back by truncating, so Color8(8,8,8) is stored
    /// as 7 -- which is exactly the sort of quiet off-by-one this probe exists
    /// to catch, and it should not be coming from the probe itself.
    /// </remarks>
    private static Texture2D BuildPalette()
    {
        const int width = HueColumns * HueWidth;

        var rgba = new byte[width * HueRows * 4];

        for (int row = 0; row < HueRows; row++)
        {
            for (int column = 0; column < HueColumns; column++)
            {
                for (int gray = 0; gray < HueWidth; gray++)
                {
                    byte[] entry = PaletteEntry(row * HueColumns + column, gray);

                    int at = (row * width + column * HueWidth + gray) * 4;

                    rgba[at] = entry[0];
                    rgba[at + 1] = entry[1];
                    rgba[at + 2] = entry[2];
                    rgba[at + 3] = 255;
                }
            }
        }

        return ImageTexture.CreateFromImage(
            Image.CreateFromData(width, HueRows, false, Image.Format.Rgba8, rgba));
    }

    /// <summary>
    /// A 32x1 strip whose red channel steps through the brightness range the
    /// shader indexes the palette with. Pixel <c>g</c> is brightness <c>g</c>.
    /// </summary>
    private static Texture2D BuildGrayRamp()
    {
        var rgba = new byte[HueWidth * 4];

        for (int gray = 0; gray < HueWidth; gray++)
        {
            // Over 31, not 32, so the last entry lands on 1.0 -- which is what
            // the shader's clamp expects to find at the end of a column.
            byte level = (byte)Math.Round(gray * 255.0 / (HueWidth - 1));

            rgba[gray * 4] = level;
            rgba[gray * 4 + 1] = level;
            rgba[gray * 4 + 2] = level;
            rgba[gray * 4 + 3] = 255;
        }

        return ImageTexture.CreateFromImage(
            Image.CreateFromData(HueWidth, 1, false, Image.Format.Rgba8, rgba));
    }
}
