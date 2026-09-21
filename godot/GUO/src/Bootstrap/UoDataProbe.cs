namespace GUO.Host;

using System;
using System.Diagnostics;
using Godot;
using GUO.Assets;
using GUO.Utility;

/// <summary>
/// First pixels: loads the real UO client data through the ported reader stack
/// and puts decoded art on screen.
/// </summary>
/// <remarks>
/// <para>
/// This is the proof that the mechanical port is real rather than merely
/// compiling. It exercises the whole bottom half of the client — the .mul and
/// .uop readers in <c>GUO.IO</c>, the loaders in <c>GUO.Assets</c>, and the
/// hue table — and ends with pixels a human can look at.
/// </para>
/// <para>
/// It is deliberately NOT the renderer. There is no sorting, no picking, no
/// atlas and no batching here; see
/// <c>docs/architecture/ADR-0001-render-presenter-seam.md</c> for the shape
/// the real one takes. This is a probe, and it should stay small enough to
/// delete once <c>ClassicPresenter</c> exists.
/// </para>
/// </remarks>
public sealed partial class UoDataProbe : Node2D
{
    /// <summary>Land tile ids are below this; statics are offset past it.</summary>
    private const uint LandCount = 0x4000;

    private UOFileManager _files;

    /// <summary>
    /// Packs a dotted version string into the value <see cref="ClientVersion"/>
    /// encodes. The enum only names the versions upstream cares about, but the
    /// comparisons it is used in are ordinal, so an unnamed version works fine
    /// as long as it is packed the same way.
    /// </summary>
    public static ClientVersion ParseVersion(string text)
    {
        // major << 24 | minor << 16 | build << 8 | extra
        int major = 7, minor = 0, build = 0, extra = 0;

        if (!string.IsNullOrWhiteSpace(text))
        {
            string[] parts = text.Trim().Split('.');
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out build);
            if (parts.Length > 3) int.TryParse(parts[3], out extra);
        }

        return (ClientVersion)(
            ((major & 0xFF) << 24)
            | ((minor & 0xFF) << 16)
            | ((build & 0xFF) << 8)
            | (extra & 0xFF)
        );
    }

    /// <summary>
    /// Opens the client install and loads every archive. Returns false and
    /// prints why if the install cannot be read.
    /// </summary>
    public bool LoadClientData(string clientData, string versionText, string lang)
    {
        ClientVersion version = ParseVersion(versionText);
        GD.Print($"[GUO] client version: {versionText} -> 0x{(int)version:X8}");

        var sw = Stopwatch.StartNew();
        try
        {
            _files = new UOFileManager(version, clientData);
            _files.Load(useVerdata: false, lang: lang);
        }
        catch (Exception ex)
        {
            // A failure here is nearly always a real data problem — a missing
            // archive, or a version mismatch that makes an index unreadable.
            // Print the type as well as the message; "index out of range" with
            // no context is the least useful possible diagnostic.
            GD.PrintErr($"[GUO] FATAL: loading client data failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        sw.Stop();
        GD.Print($"[GUO] loaded all archives in {sw.ElapsedMilliseconds} ms");
        return true;
    }

    /// <summary>
    /// Decodes one piece of art into a Godot texture, or null if that id is
    /// empty — which is normal and not an error: UO's id space is sparse.
    /// </summary>
    public ImageTexture TryGetArt(uint index)
    {
        byte[] rgba;
        int w, h;

        try
        {
            // ArtInfo is a ref struct holding a Span over the loader's buffer,
            // so the pixels must be copied out before anything else touches
            // the loader. Do not try to hold on to the span.
            ArtInfo art = _files.Arts.GetArt(index);
            w = art.Width;
            h = art.Height;
            if (w <= 0 || h <= 0 || art.Pixels.Length < w * h)
            {
                return null;
            }

            rgba = new byte[w * h * 4];
            // Color16To32 packs as R | G<<8 | B<<16, so the little-endian byte
            // order in memory is already R,G,B,A — the same as Godot's Rgba8.
            // Alpha is 0 for UO's transparent pixels and 0xFF elsewhere.
            Span<byte> dst = rgba;
            for (int i = 0; i < w * h; i++)
            {
                uint px = art.Pixels[i];
                int o = i * 4;
                dst[o + 0] = (byte)(px & 0xFF);
                dst[o + 1] = (byte)((px >> 8) & 0xFF);
                dst[o + 2] = (byte)((px >> 16) & 0xFF);
                dst[o + 3] = (byte)(px == 0 ? 0 : 0xFF);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] art {index} failed to decode: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        Image img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Lays out a sample of land and static art so the result can be eyeballed
    /// and screenshotted.
    /// </summary>
    public int ShowSample()
    {
        int shown = 0;
        int x = 16, y = 16, rowHeight = 0;
        const int Margin = 4;
        const int Wrap = 1200;

        // A spread of land ids, then a spread of statics. Both are sparse, so
        // this walks a range and draws whatever actually exists.
        foreach (uint id in SampleIds())
        {
            ImageTexture tex = TryGetArt(id);
            if (tex == null)
            {
                continue;
            }

            var sprite = new Sprite2D
            {
                Texture = tex,
                Centered = false,
                Position = new Vector2(x, y),
                // Belt and braces: the project sets default_texture_filter=0,
                // but a filtered sprite here would silently undo the one thing
                // the whole port must never get wrong.
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            AddChild(sprite);

            shown++;
            rowHeight = Math.Max(rowHeight, tex.GetHeight());
            x += tex.GetWidth() + Margin;
            if (x > Wrap)
            {
                x = 16;
                y += rowHeight + Margin;
                rowHeight = 0;
            }
        }

        GD.Print($"[GUO] drew {shown} art tiles from the client install.");
        return shown;
    }

    private static System.Collections.Generic.IEnumerable<uint> SampleIds()
    {
        // Grass, dirt and water sit low in the land range.
        for (uint i = 3; i < 60; i++)
        {
            yield return i;
        }

        // Statics: trees, walls and furniture are scattered through the low
        // few hundred ids.
        for (uint i = 0; i < 120; i++)
        {
            yield return LandCount + i;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _files?.Dispose();
            _files = null;
        }

        base.Dispose(disposing);
    }
}
