namespace GUO.Host;

using System;
using System.Collections.Generic;
using Godot;
using GUO.Renderer;

/// <summary>
/// PERF-GPU step 1 (ADR-0007, amendment 2026-10-19): proves the world depth
/// pass puts a quad on the same pixels as the canvas world pass, and that its
/// depth test orders quads the way the sort does.
/// </summary>
/// <remarks>
/// <para>
/// Three checks, all on synthetic art (no UO install needed):
/// </para>
/// <list type="number">
/// <item>Pixel parity: the same quads through <see cref="UltimaBatcher2D"/>
/// and through <see cref="WorldDepthPass"/>, under the camera's view
/// transform at zooms 0.5 to 2.5 and fractional origins, compared pixel by
/// pixel. Nearest sampling on both, so any texel snapping difference shows.</item>
/// <item>Depth order: two overlapping quads with different keys, submitted
/// both ways round, in one page and across two pages. The nearer key must
/// win either way, for adjacent 24-bit keys at the bottom, middle and top of
/// the range. Adjacent 28-bit keys are reported, not required.</item>
/// <item>Ties: equal keys in one page. Reported as "later wins" (the sort's
/// insertion order) or "earlier wins" (the key would need tie bits).</item>
/// </list>
/// <para>
/// Needs a real frame, so not under <c>--headless</c>. Run it once per
/// renderer: the default (Forward+) and <c>--rendering-method gl_compatibility</c>.
/// </para>
/// </remarks>
public partial class DepthProbe : Node
{
    private static readonly Vector2I Size = new(160, 120);
    // The camera scales the world by its zoom itself (Camera.UpdateMatrices);
    // 1.25 and 2.5 put integer positions on half pixels, so quad edges land
    // exactly on pixel centres. The reciprocals add inexact scales.
    private static readonly float[] Scales = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 2.5f, 0.8f, 0.4f, 1f / 0.75f, 1f / 1.5f };
    private static readonly Vector2[] Origins = { Vector2.Zero, new(0.37f, 0.5f), new(0.5f, 0.25f) };

    // Placed as statics are: integer world positions, sub-rects of a page.
    private static readonly (Vector2I At, Rect2I Src)[] Quads =
    {
        (new Vector2I(3, 5), new Rect2I(0, 0, 32, 32)),
        (new Vector2I(40, 10), new Rect2I(4, 6, 20, 18)),
        (new Vector2I(20, 40), new Rect2I(10, 2, 13, 9)),
    };

    private int _failures;
    private int _checks;

    public static void Run(Node parent)
    {
        var probe = new DepthProbe();
        parent.AddChild(probe);
        probe.Verify();
    }

    private async void Verify()
    {
        GD.Print($"[depth-probe] renderer {RenderingServer.GetCurrentRenderingMethod()}"
            + $" ({RenderingServer.GetCurrentRenderingDriverName()}), {RenderingServer.GetVideoAdapterName()}");

        ImageTexture art = BuildArt();
        ImageTexture redBlue = BuildRedBlue();
        ImageTexture green = Solid(new Color8(20, 200, 20, 255));

        var canvasViewport = new SubViewport
        {
            Size = Size,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var host = new Node2D();
        canvasViewport.AddChild(host);
        AddChild(canvasViewport);

        var batcher = new UltimaBatcher2D(host.GetCanvasItem())
        {
            HueTexture = Solid(new Color8(0, 0, 0, 255)),
        };

        var pass = new WorldDepthPass(this, Size);

        // Which way clip y points on this renderer: whichever sign covers the
        // same pixels at scale 1. Coverage only, so a colour difference is
        // reported by the parity checks rather than mistaken for a flip.
        float flip = 0f;
        foreach (float sign in new[] { 1f, -1f })
        {
            pass.FlipY = sign;
            (_, _, int coverage) = await Compare(batcher, canvasViewport, pass, art, 1f, Vector2.Zero);
            if (coverage == 0)
            {
                flip = sign;
                break;
            }
        }

        if (flip == 0f)
        {
            pass.FlipY = 1f;
            Check("clip y sign", false, "the depth pass matches the canvas with neither sign at scale 1");
        }
        else
        {
            pass.FlipY = flip;
            GD.Print($"[depth-probe] clip y sign {flip:+0;-0}");
        }

        foreach (float scale in Scales)
        {
            foreach (Vector2 origin in Origins)
            {
                (int diff, int maxDelta, int coverage) = await Compare(batcher, canvasViewport, pass, art, scale, origin);
                Check($"parity scale {scale:0.000} origin ({origin.X:0.00}, {origin.Y:0.00})", diff == 0,
                    $"{diff} px differ ({coverage} in coverage), max delta {maxDelta}");
            }
        }

        batcher.Dispose();

        // Which way the renderer's depth runs: reverse Z wants the key as is.
        bool reverseOk = await Ordered(pass, redBlue, redBlue, 0.25f, 0.75f);
        pass.ReverseZ = true;
        if (!reverseOk)
        {
            pass.ReverseZ = false;
            bool forwardOk = await Ordered(pass, redBlue, redBlue, 0.25f, 0.75f);
            GD.Print($"[depth-probe] info reverse Z does not order; forward Z {(forwardOk ? "does" : "does not either")}");
            if (!forwardOk)
            {
                pass.ReverseZ = true;
            }
        }
        else
        {
            GD.Print("[depth-probe] info reverse Z orders");
        }

        // Keys as the amendment maps them: integer k over 2^bits.
        foreach (int bits in new[] { 24, 28 })
        {
            double scale = 1.0 / (1L << bits);
            long top = (1L << bits) - 2;
            foreach (long k in new[] { 1L, 1L << (bits - 1), top })
            {
                float near = (float)((k + 1) * scale);
                float far = (float)(k * scale);
                bool distinct = near != far;
                bool sameOk = await Ordered(pass, redBlue, redBlue, far, near);
                bool crossOk = await Ordered(pass, redBlue, green, far, near);
                string label = $"{bits}-bit keys {k} vs {k + 1}" + (distinct ? "" : " (equal as float)");

                if (bits == 24)
                {
                    Check(label + ", one page", sameOk, "the nearer key lost");
                    Check(label + ", two pages", crossOk, "the nearer key lost");
                }
                else
                {
                    GD.Print($"[depth-probe] info {label}: one page {(sameOk ? "ordered" : "NOT ordered")},"
                        + $" two pages {(crossOk ? "ordered" : "NOT ordered")}");
                }
            }
        }

        // Ties in one page.
        foreach (float key in new[] { 0.25f, 0.5f, 0.75f })
        {
            Color8 first = await PairColour(pass, redBlue, redBlue, key, key, false);
            string winner = first.B > first.R ? "later wins" : "earlier wins";
            GD.Print($"[depth-probe] info ties at {key:0.00}, one page: {winner}");
        }

        pass.Dispose();

        GD.Print($"[depth-probe] {_checks - _failures}/{_checks} checks passed");
        GD.Print($"[depth-probe] {(_failures == 0 ? "PASS" : "FAIL")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(string what, bool ok, string why)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
        }

        GD.Print($"[depth-probe] {(ok ? "ok  " : "FAIL")} {what}{(ok ? "" : ": " + why)}");
    }

    private static Transform2D View(float scale, Vector2 origin) =>
        new(new Vector2(scale, 0f), new Vector2(0f, scale), origin);

    /// <summary>GUO_DEPTH_PROBE_DUMP names a folder: each failing case saves both images there.</summary>
    private static void Dump(string name, Image canvas, Image depth)
    {
        string folder = System.Environment.GetEnvironmentVariable("GUO_DEPTH_PROBE_DUMP");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(folder);
        canvas?.SavePng(System.IO.Path.Combine(folder, name + "_canvas.png"));
        depth?.SavePng(System.IO.Path.Combine(folder, name + "_depth.png"));
    }

    private async System.Threading.Tasks.Task<(int Diff, int MaxDelta, int Coverage)> Compare(
        UltimaBatcher2D batcher, SubViewport canvasViewport, WorldDepthPass pass, Texture2D art,
        float scale, Vector2 origin)
    {
        Transform2D view = View(scale, origin);
        Vector3 hue = ShaderHueTranslator.GetHueVector(0);

        batcher.BeginFrame();
        batcher.Begin(view);
        foreach ((Vector2I at, Rect2I src) in Quads)
        {
            batcher.Draw(art, new Vector2(at.X, at.Y), new Compat.Rectangle(src.Position.X, src.Position.Y, src.Size.X, src.Size.Y), hue, 0f);
        }

        batcher.End();

        pass.Begin(view);
        foreach ((Vector2I at, Rect2I src) in Quads)
        {
            pass.AddQuad(art, new Rect2(at.X, at.Y, src.Size.X, src.Size.Y), new Rect2(src.Position, src.Size), 0.5f);
        }

        pass.End();

        await Frames(2);

        Image a = canvasViewport.GetTexture().GetImage();
        Image b = pass.Viewport.GetTexture().GetImage();
        a.Convert(Image.Format.Rgba8);
        b.Convert(Image.Format.Rgba8);

        int diff = 0;
        int maxDelta = 0;
        int coverage = 0;
        for (int y = 0; y < Size.Y; y++)
        {
            for (int x = 0; x < Size.X; x++)
            {
                Color ca = a.GetPixel(x, y);
                Color cb = b.GetPixel(x, y);

                // Fully transparent is transparent, whatever colour it carries.
                if (ca.A8 == 0 && cb.A8 == 0)
                {
                    continue;
                }

                if ((ca.A8 == 0) != (cb.A8 == 0))
                {
                    coverage++;
                }

                int d = Math.Max(Math.Max(Math.Abs(ca.R8 - cb.R8), Math.Abs(ca.G8 - cb.G8)),
                    Math.Max(Math.Abs(ca.B8 - cb.B8), Math.Abs(ca.A8 - cb.A8)));
                if (d > 0)
                {
                    diff++;
                    maxDelta = Math.Max(maxDelta, d);
                }
            }
        }

        if (diff > 0)
        {
            Dump($"parity_s{scale:0.000}_o{origin.X:0.00}-{origin.Y:0.00}_flip{pass.FlipY:+0;-0}", a, b);
        }

        return (diff, maxDelta, coverage);
    }

    /// <summary>Whether the nearer quad wins at the overlap, submitted both ways round.</summary>
    private async System.Threading.Tasks.Task<bool> Ordered(
        WorldDepthPass pass, Texture2D farTexture, Texture2D nearTexture, float farKey, float nearKey)
    {
        bool ok = true;
        foreach (bool nearFirst in new[] { false, true })
        {
            Color8 got = await PairColour(pass, farTexture, nearTexture, farKey, nearKey, nearFirst);

            // The far quad is always red; the near one is blue (one page) or green (two).
            ok &= got.R < 100 && got.A > 0;
        }

        return ok;
    }

    /// <summary>
    /// Two 20x20 quads overlapping at (22, 22): the first red, from
    /// <paramref name="a"/>; the second blue (or <paramref name="b"/>'s colour).
    /// </summary>
    private async System.Threading.Tasks.Task<Color8> PairColour(
        WorldDepthPass pass, Texture2D a, Texture2D b, float keyA, float keyB, bool bFirst)
    {
        // In texture pixels: the red half of the red/blue art, then its blue
        // half, or all of a second page.
        var red = new Rect2(0, 0, 4, 4);
        Rect2 other = a == b ? new Rect2(4, 0, 4, 4) : new Rect2(0, 0, 4, 4);

        pass.Begin(Transform2D.Identity);
        if (bFirst)
        {
            pass.AddQuad(b, new Rect2(15, 15, 20, 20), other, keyB);
            pass.AddQuad(a, new Rect2(10, 10, 20, 20), red, keyA);
        }
        else
        {
            pass.AddQuad(a, new Rect2(10, 10, 20, 20), red, keyA);
            pass.AddQuad(b, new Rect2(15, 15, 20, 20), other, keyB);
        }

        pass.End();
        await Frames(2);

        Image image = pass.Viewport.GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        Color c = image.GetPixel(22, 22);
        Dump($"pair_{keyA:0.########}_{keyB:0.########}_{(bFirst ? "bfirst" : "afirst")}{(a == b ? "" : "_2p")}", null, image);

        return new Color8((byte)c.R8, (byte)c.G8, (byte)c.B8, (byte)c.A8);
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        }
    }

    /// <summary>32x32, every texel its own colour, with a transparent 4x4 hole.</summary>
    private static ImageTexture BuildArt()
    {
        Image image = Image.CreateEmpty(32, 32, false, Image.Format.Rgba8);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool hole = x >= 12 && x < 16 && y >= 12 && y < 16;
                image.SetPixel(x, y, hole
                    ? new Color(0, 0, 0, 0)
                    : Color.Color8((byte)(x * 8), (byte)(y * 8), (byte)(((x + y) & 7) * 32), 255));
            }
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>8x4: the left half red, the right half blue.</summary>
    private static ImageTexture BuildRedBlue()
    {
        Image image = Image.CreateEmpty(8, 4, false, Image.Format.Rgba8);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                image.SetPixel(x, y, x < 4 ? Color.Color8(200, 20, 20) : Color.Color8(20, 20, 200));
            }
        }

        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture Solid(Color8 c)
    {
        Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Color.Color8(c.R, c.G, c.B, c.A));

        return ImageTexture.CreateFromImage(image);
    }

    private readonly record struct Color8(byte R, byte G, byte B, byte A);
}
