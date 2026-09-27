namespace GUO.Host;

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

        await VerifyFonts();
        Stage("fonts");

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
        batcher.BeginFrame();
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
            batcher.BeginFrame();
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
    /// that every pixel comes back lit by its own interpolated normal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one thing ADR-0004 bought. Stretched land is the only place
    /// in the client where a per-vertex value beyond position, UV and colour
    /// has to reach the shader, and the packed colour had no channel left for
    /// it, so the normal travels as CUSTOM0 on a mesh -- a path nothing else
    /// in the batcher uses.
    /// </para>
    /// <para>
    /// The four corners get four different normals, so a shader that dropped
    /// CUSTOM0, or read a constant, is wrong at three of them rather than
    /// looking plausible everywhere. The expected value is upstream's get_light
    /// of the normal interpolated across the quad's two triangles, taken at
    /// every pixel centre, at both ends of the brightlight range. Lighting the
    /// corners and interpolating the light instead -- what this layer did
    /// before -- differs inside the quad, not at its corners.
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
        quad.Normal0 = normals[0];
        quad.Normal1 = normals[1];
        quad.Normal2 = normals[2];
        quad.Normal3 = normals[3];
        layer.Vertices[0] = quad;

        foreach (float brightlight in new[] { 0f, 1f })
        {
            layer.ResetVisibility();
            layer.SetVisible(0, 0xFF);
            layer.BuildVisibleIndices();

            batcher.BeginFrame();
            batcher.Begin();
            batcher.SetBrightlight(brightlight);
            batcher.DrawMeshLayer(layer);
            batcher.End();

            await ToSignal(
                RenderingServer.Singleton,
                RenderingServerInstance.SignalName.FramePostDraw);

            Image image = viewport.GetTexture().GetImage();

            int size = Scale * 2;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // The pixel centre, as a fraction of the quad. Position0
                    // is top-left, 1 top-right, 2 bottom-left, 3 bottom-right
                    // -- upstream's quad order.
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;

                    float light = ExpectedLight(
                        MeshLayer.LightFromNormal(Interpolate(normals, u, v)), brightlight);
                    byte want = (byte)Math.Round(Math.Clamp(texel[0] / 255f * light, 0f, 1f) * 255f);

                    Color got = image.GetPixel(x, y);

                    Check($"land light ({x},{y}) brightlight {brightlight}",
                        new byte[] { want, want, want }, got);
                }
            }
        }

        batcher.SetBrightlight(0f);
        layer.Dispose();
    }

    /// <summary>Upstream's brightlight blend, applied to get_light's base.</summary>
    private static float ExpectedLight(float baseLight, float brightlight)
    {
        return baseLight
            + ((brightlight * (baseLight - 0.85355339f)) - (baseLight - 0.85355339f));
    }

    /// <summary>
    /// The normal at (u, v) across the quad's two triangles, 0 1 2 and 1 3 2,
    /// which is what the rasteriser interpolates.
    /// </summary>
    private static Vector3 Interpolate(Vector3[] n, float u, float v)
    {
        if (u + v <= 1f)
        {
            return n[0] * (1f - u - v) + n[1] * u + n[2] * v;
        }

        return n[3] * (u + v - 1f) + n[1] * (1f - v) + n[2] * (1f - u);
    }


    /// <summary>
    /// Parses all eight embedded .xnb fonts and draws one through the batcher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SpriteFont.Create is a hand-written reader for XNA's compiled-content
    /// format, and the port replaced its one graphics call: upstream pushes the
    /// decoded level data into an FNA Texture2D with SetDataPointerEXT, GUO
    /// hands the same bytes to Image.CreateFromData as Rgba8. If that channel
    /// order or that stride is wrong, the font still draws -- as a sheet of
    /// wrongly coloured or offset rectangles. Nothing about it fails to build.
    /// </para>
    /// <para>
    /// So the checks below are the ones that can actually fail: the glyph
    /// tables have to be self-consistent with the texture they index into, the
    /// measured width has to agree with where the ink lands, and a font with
    /// no ink at all has to be a failure rather than a blank pass.
    /// </para>
    /// </remarks>
    private async System.Threading.Tasks.Task VerifyFonts()
    {
        Fonts.Initialize();

        var fonts = new (string Name, SpriteFont Font)[]
        {
            ("Regular", Fonts.Regular), ("Bold", Fonts.Bold),
            ("Map1", Fonts.Map1), ("Map2", Fonts.Map2), ("Map3", Fonts.Map3),
            ("Map4", Fonts.Map4), ("Map5", Fonts.Map5), ("Map6", Fonts.Map6),
        };

        foreach ((string name, SpriteFont font) in fonts)
        {
            if (!CheckThat($"font {name} parsed", font?.Texture != null, "null"))
            {
                continue;
            }

            int width = font.Texture.GetWidth();
            int height = font.Texture.GetHeight();

            CheckThat($"font {name} texture", width > 0 && height > 0,
                $"{width}x{height}");

            CheckThat($"font {name} has glyphs",
                font.CharacterMap.Count > 0
                    && font.GlyphData.Count == font.CharacterMap.Count
                    && font.CroppingData.Count == font.CharacterMap.Count
                    && font.Kerning.Count == font.CharacterMap.Count,
                $"{font.CharacterMap.Count} chars, {font.GlyphData.Count} glyphs, "
                    + $"{font.CroppingData.Count} crops, {font.Kerning.Count} kerns");

            // Every glyph rectangle has to lie inside the sheet it indexes.
            // A stride or channel mistake in the decode would not move these,
            // but a version mismatch in the container reader would, and this
            // is the cheapest place to catch that.
            bool inside = true;

            foreach (Compat.Rectangle g in font.GlyphData)
            {
                inside &= g.X >= 0 && g.Y >= 0
                    && g.X + g.Width <= width && g.Y + g.Height <= height;
            }

            CheckThat($"font {name} glyphs inside the sheet", inside, "");

            CheckThat($"font {name} knows a capital A",
                font.CharacterMap.Contains('A'), "");
        }

        if (Fonts.Regular?.Texture == null)
        {
            return;
        }

        // MeasureString is pure arithmetic over the kerning table, so it is
        // checked for the properties the callers rely on rather than against
        // hard-coded pixel counts that would change with the font.
        SpriteFont regular = Fonts.Regular;
        Vector2 one = regular.MeasureString("M");
        Vector2 two = regular.MeasureString("MM");
        Vector2 stacked = regular.MeasureString("M\nM");

        CheckThat("MeasureString has a size", one.X > 0f && one.Y > 0f,
            $"{one.X:F1}x{one.Y:F1}");
        CheckThat("MeasureString grows sideways", two.X > one.X,
            $"{one.X:F1} -> {two.X:F1}");
        CheckThat("MeasureString grows downwards", stacked.Y > one.Y,
            $"{one.Y:F1} -> {stacked.Y:F1}");

        // Now draw it. "Ink" is any pixel that differs from whatever the
        // viewport clears to, which is read off a blank frame below rather
        // than assumed -- an opaque SubViewport clears to the project's
        // background colour, not to black.
        var host = new Node2D();

        var viewport = new SubViewport
        {
            Size = new Vector2I(256, 64),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };

        viewport.AddChild(host);
        AddChild(viewport);

        var batcher = new UltimaBatcher2D(host.GetCanvasItem())
        {
            HueTexture = BuildPalette(),
        };

        const string text = "MMMM";
        const int originX = 4;

        // An empty draw first. If the reader below counts ink on a blank
        // frame, every later count means nothing.
        batcher.BeginFrame();
        batcher.Begin();
        batcher.End();

        await ToSignal(
            RenderingServer.Singleton,
            RenderingServerInstance.SignalName.FramePostDraw);

        Color background = viewport.GetTexture().GetImage().GetPixel(0, 0);

        CheckThat("a blank frame has no ink",
            InkExtent(viewport.GetTexture().GetImage(), background, out _) == 0,
            $"background {background.R8},{background.G8},{background.B8}");

        batcher.BeginFrame();
        batcher.Begin();
        batcher.DrawString(
            regular, text, originX, 4, ShaderHueTranslator.GetHueVector(0), 0f);
        batcher.End();

        await ToSignal(
            RenderingServer.Singleton,
            RenderingServerInstance.SignalName.FramePostDraw);

        int inked = InkExtent(
            viewport.GetTexture().GetImage(), background, out int rightmost);

        CheckThat("DrawString puts ink on the screen", inked > 0, $"{inked} px");

        // The one check that ties the two halves together: the ink has to stop
        // roughly where MeasureString says the string ends. A glyph sheet read
        // with the wrong stride still draws ink, but not in that column.
        float measured = regular.MeasureString(text).X;
        int wantRight = originX + (int)measured;

        CheckThat("ink ends where MeasureString says",
            rightmost <= wantRight && rightmost > wantRight - (int)measured / 2,
            $"ink to x={rightmost}, measured x={wantRight}");

        batcher.Dispose();
        viewport.QueueFree();
    }

    /// <summary>
    /// Counts pixels that differ from <paramref name="background"/> and reports
    /// the rightmost column holding one.
    /// </summary>
    private static int InkExtent(Image image, Color background, out int rightmost)
    {
        int count = 0;
        rightmost = -1;

        for (int y = 0; y < image.GetHeight(); y++)
        {
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color pixel = image.GetPixel(x, y);

                if (Math.Abs(pixel.R - background.R) > 0.05f
                    || Math.Abs(pixel.G - background.G) > 0.05f
                    || Math.Abs(pixel.B - background.B) > 0.05f)
                {
                    count++;

                    if (x > rightmost)
                    {
                        rightmost = x;
                    }
                }
            }
        }

        return count;
    }

    private bool CheckThat(string what, bool ok, string detail)
    {
        _checks++;

        if (!ok)
        {
            _failures++;

            GD.PrintErr($"[batcher-probe]   {what}: {detail}");
        }

        return ok;
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
