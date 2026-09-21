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

    private int _checks;

    private int _stageStart;

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
                byte[] want = PaletteEntry(hue, gray);

                Color got = await DrawAndRead(batcher, viewport, ramp, gray, hue + 1, false);

                Check($"hue {hue} gray {gray}", want, got);

                // Again through the triangle-array path. The call above takes
                // the batcher's fast path -- an axis-aligned, unflipped sprite
                // becomes CanvasItemAddTextureRectRegion, whose modulate is a
                // per-item colour. Mirroring it forces the quad path instead,
                // where the same three packed bytes travel as a PER-VERTEX
                // colour through CanvasItemAddTriangleArray. That is a
                // different vertex format with its own precision, and the hue
                // index spends 12 bits across two channels, so a quantisation
                // step lands on a visibly wrong colour rather than a slightly
                // wrong one. Both paths draw every sprite in the client.
                Color flipped = await DrawAndRead(batcher, viewport, ramp, gray, hue + 1, true);

                Check($"hue {hue} gray {gray} mirrored", want, flipped);
            }
        }

        Stage("hue");

        await VerifyBlends(batcher, viewport);
        Stage("blend states");

        await VerifyLandLight(batcher, viewport);
        Stage("land light");

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
        UltimaBatcher2D batcher, SubViewport viewport, Texture2D ramp, int gray, int hue,
        bool mirrored = false)
    {
        batcher.Begin();

        if (mirrored)
        {
            batcher.Draw(
                ramp,
                new Vector2(0, 0),
                new Compat.Rectangle(gray, 0, 1, 1),
                ShaderHueTranslator.GetHueVector(hue),
                0f,
                Vector2.Zero,
                Scale,
                SpriteEffects.FlipHorizontally,
                0f);
        }
        else
        {
            batcher.Draw(
                ramp,
                new Compat.Rectangle(0, 0, Scale, Scale),
                new Compat.Rectangle(gray, 0, 1, 1),
                ShaderHueTranslator.GetHueVector(hue),
                0f);
        }

        batcher.End();

        await ToSignal(
            RenderingServer.Singleton,
            RenderingServerInstance.SignalName.FramePostDraw);

        return viewport.GetTexture().GetImage().GetPixel(1, 1);
    }


    /// <summary>
    /// Draws one colour over another under each blend state ClassicUO's
    /// effects use, and checks the result against the equation worked out in
    /// C#.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of these five have no equivalent among Godot's fixed canvas blend
    /// modes, so the batcher turns hardware blending off and evaluates XNA's
    /// blend equation in the shader against a back-buffer copy — see ADR-0003.
    /// That is a reimplementation of a fixed-function unit, and the way it
    /// fails is by looking merely plausible: an effect slightly too bright is
    /// not something anyone spots in a screenshot.
    /// </para>
    /// <para>
    /// So the expected value is computed here from the same factors, rather
    /// than read off a reference image. If the shader and this disagree about
    /// what DestinationColor means, the numbers say so.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Prints how many comparisons a stage made. A stage that silently drew
    /// nothing, or looped zero times, otherwise reports the same PASS as one
    /// that checked everything.
    /// </summary>
    private void Stage(string name)
    {
        GD.Print($"[batcher-probe] {name,-22} {_checks - _stageStart,3} checks");

        _stageStart = _checks;
    }

    private async System.Threading.Tasks.Task VerifyBlends(
        UltimaBatcher2D batcher, SubViewport viewport)
    {
        // Upstream's five, from GameEffectView, plus the default for a control.
        var cases = new (string Name, BlendState State)[]
        {
            ("default (premul alpha)", null),
            ("multiply", new BlendState
            {
                ColorSourceBlend = Blend.Zero,
                ColorDestinationBlend = Blend.SourceColor,
            }),
            ("screen", new BlendState
            {
                ColorSourceBlend = Blend.One,
                ColorDestinationBlend = Blend.One,
            }),
            ("screenLess", new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.InverseSourceAlpha,
            }),
            ("normalHalf", new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.SourceColor,
            }),
            ("shadowBlue", new BlendState
            {
                ColorSourceBlend = Blend.SourceColor,
                ColorDestinationBlend = Blend.InverseSourceColor,
                ColorBlendFunction = BlendFunction.ReverseSubtract,
            }),
        };

        // Deliberately not round numbers: a blend that ignores one of its two
        // factors still lands on the right answer when the operands are 0 or 1.
        var under = new[] { (byte)204, (byte)153, (byte)102 };
        var over = new[] { (byte)64, (byte)128, (byte)32 };

        Texture2D underTexture = BuildSolid(under);
        Texture2D overTexture = BuildSolid(over);

        Vector3 plain = ShaderHueTranslator.GetHueVector(0);

        foreach ((string name, BlendState state) in cases)
        {
            batcher.Begin();

            batcher.Draw(underTexture, new Compat.Rectangle(0, 0, Scale * 2, Scale * 2), plain, 0f);

            batcher.SetBlendState(state);
            batcher.Draw(overTexture, new Compat.Rectangle(0, 0, Scale * 2, Scale * 2), plain, 0f);
            batcher.SetBlendState(null);

            batcher.End();

            await ToSignal(
                RenderingServer.Singleton,
                RenderingServerInstance.SignalName.FramePostDraw);

            Color got = viewport.GetTexture().GetImage().GetPixel(Scale, Scale);
            byte[] want = Expected(state ?? BlendState.AlphaBlend, over, under);

            Check($"blend {name}", want, got);
        }
    }

    /// <summary>
    /// XNA's blend equation, on the CPU, for one opaque source over one opaque
    /// destination.
    /// </summary>
    private static byte[] Expected(BlendState state, byte[] src, byte[] dst)
    {
        var result = new byte[3];

        for (int i = 0; i < 3; i++)
        {
            float s = src[i] / 255f;
            float d = dst[i] / 255f;

            // Both quads are drawn fully opaque, so every alpha term is 1.
            float sf = Factor(state.ColorSourceBlend, s, d);
            float df = Factor(state.ColorDestinationBlend, s, d);

            float value = state.ColorBlendFunction switch
            {
                BlendFunction.Subtract => s * sf - d * df,
                BlendFunction.ReverseSubtract => d * df - s * sf,
                BlendFunction.Min => Math.Min(s * sf, d * df),
                BlendFunction.Max => Math.Max(s * sf, d * df),
                _ => s * sf + d * df,
            };

            result[i] = (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
        }

        return result;
    }

    private static float Factor(Blend blend, float s, float d) => blend switch
    {
        Blend.One => 1f,
        Blend.Zero => 0f,
        Blend.SourceColor => s,
        Blend.InverseSourceColor => 1f - s,
        Blend.SourceAlpha => 1f,
        Blend.InverseSourceAlpha => 0f,
        Blend.DestinationColor => d,
        Blend.InverseDestinationColor => 1f - d,
        Blend.DestinationAlpha => 1f,
        Blend.InverseDestinationAlpha => 0f,
        _ => 1f,
    };

    private static Texture2D BuildSolid(byte[] rgb)
    {
        var rgba = new byte[] { rgb[0], rgb[1], rgb[2], 255 };

        return ImageTexture.CreateFromImage(
            Image.CreateFromData(1, 1, false, Image.Format.Rgba8, rgba));
    }


    /// <summary>
    /// Draws a stretched-land quad through a <see cref="MeshLayer"/> and checks
    /// that each corner comes back lit by its own normal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one thing ADR-0004 bought. Stretched land is the only place
    /// in the client where a per-vertex value beyond position, UV and colour
    /// has to reach the shader, and the packed colour had no channel left for
    /// it, so the light travels as CUSTOM0 on a mesh -- a path nothing else in
    /// the batcher uses.
    /// </para>
    /// <para>
    /// The four corners get four different normals, so a shader that dropped
    /// CUSTOM0, or read a constant, is wrong at three of them rather than
    /// looking plausible everywhere. The expected value is upstream's get_light
    /// recomputed here, at both ends of the brightlight range, because that
    /// uniform is the half of the formula the CPU does not do.
    /// </para>
    /// </remarks>
    private async System.Threading.Tasks.Task VerifyLandLight(
        UltimaBatcher2D batcher, SubViewport viewport)
    {
        // A flat grey, so the only thing modulating it is the light.
        var texel = new byte[] { 160, 160, 160 };
        Texture2D grey = BuildSolid(texel);

        // Straight up, and three tilted away from the light. The first is the
        // flat-tile case whose value the brightlight blend pulls towards.
        var normals = new[]
        {
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 1f, 1f),
            new Vector3(0f, -1f, 1f),
            new Vector3(1f, 0f, 0.2f),
        };

        var layer = new MeshLayer();
        layer.EnsureCapacity(1);
        layer.Count = 1;

        Vector3 hue = ShaderHueTranslator.GetHueVector(0);
        hue.Y = ShaderHueTranslator.SHADER_LAND;

        layer.WriteQuadAt(0, grey, new Compat.Rectangle(0, 0, 1, 1), 0, 0, hue, 0f);

        // WriteQuadAt writes the flat normal to all four; a stretched tile is
        // exactly the case where they differ, so overwrite them here the way
        // ChunkMesh.WriteStretchedLand does.
        // Taken by value, not by ref: a ref local cannot survive the awaits
        // below. The writes go back through layer.Vertices[0].
        MeshQuad quad = layer.Vertices[0];
        quad.Position1.X = Scale * 2;
        quad.Position3.X = Scale * 2;
        quad.Position2.Y = Scale * 2;
        quad.Position3.Y = Scale * 2;
        quad.Light0 = MeshLayer.LightFromNormal(normals[0]);
        quad.Light1 = MeshLayer.LightFromNormal(normals[1]);
        quad.Light2 = MeshLayer.LightFromNormal(normals[2]);
        quad.Light3 = MeshLayer.LightFromNormal(normals[3]);
        layer.Vertices[0] = quad;

        foreach (float brightlight in new[] { 0f, 1f })
        {
            layer.ResetVisibility();
            layer.SetVisible(0, 0xFF);
            layer.BuildVisibleIndices();

            batcher.Begin();
            batcher.SetBrightlight(brightlight);
            batcher.DrawMeshLayer(layer);
            batcher.End();

            await ToSignal(
                RenderingServer.Singleton,
                RenderingServerInstance.SignalName.FramePostDraw);

            Image image = viewport.GetTexture().GetImage();

            // The corner texels, where the interpolant is closest to that
            // corner's own value. Position0 is top-left, 1 top-right,
            // 2 bottom-left, 3 bottom-right -- upstream's quad order.
            var corners = new[]
            {
                (0, 0, quad.Light0),
                (Scale * 2 - 1, 0, quad.Light1),
                (0, Scale * 2 - 1, quad.Light2),
                (Scale * 2 - 1, Scale * 2 - 1, quad.Light3),
            };

            for (int i = 0; i < corners.Length; i++)
            {
                (int x, int y, float baseLight) = corners[i];

                // The corner texel's centre is half a pixel in, so the
                // interpolated light there is not quite the corner's own.
                float u = (x + 0.5f) / (Scale * 2);
                float v = (y + 0.5f) / (Scale * 2);
                float interpolated = Bilinear(
                    quad.Light0, quad.Light1, quad.Light2, quad.Light3, u, v);

                float light = ExpectedLight(interpolated, brightlight);
                byte want = (byte)Math.Round(Math.Clamp(texel[0] / 255f * light, 0f, 1f) * 255f);

                Color got = image.GetPixel(x, y);

                Check($"land light corner {i} brightlight {brightlight}",
                    new byte[] { want, want, want }, got);

                _ = baseLight;
            }
        }

        batcher.SetBrightlight(0f);
        layer.Dispose();
    }

    /// <summary>Upstream's get_light, with the per-vertex half already done.</summary>
    private static float ExpectedLight(float baseLight, float brightlight)
    {
        return baseLight
            + ((brightlight * (baseLight - 0.85355339f)) - (baseLight - 0.85355339f));
    }

    private static float Bilinear(float v0, float v1, float v2, float v3, float u, float v)
    {
        return (v0 * (1f - u) + v1 * u) * (1f - v) + (v2 * (1f - u) + v3 * u) * v;
    }

    private void Check(string what, byte[] want, Color got)
    {
        // One 8-bit step. The hue path is exact and would pass at zero, but
        // the blend and land-light arithmetic runs through floats on the GPU
        // and again here, and demanding bit equality of that would be testing
        // the rounding rather than the equation.
        bool ok = Math.Abs(want[0] - got.R8) <= 1
               && Math.Abs(want[1] - got.G8) <= 1
               && Math.Abs(want[2] - got.B8) <= 1;

        _checks++;

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
