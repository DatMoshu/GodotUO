#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using GUO.Assets;
using GUO.Renderer;

/// <summary>
/// Animations (plan §4.6 panel 3): body, action and direction, played back
/// through the game's own <see cref="GUO.Renderer.Animations.Animations"/>,
/// which is where body conversion, UOP group replacement and the eight to
/// five direction mirroring happen.
/// </summary>
/// <remarks>
/// Frames are read back out of the game's animation atlas and laid out the
/// way <c>MobileView</c> places them: the mobile's foot is the origin, a frame
/// sits at <c>-Center.X</c>, <c>-(Height + Center.Y)</c>, and a mirrored one at
/// <c>Center.X - Width</c>. All frames share one canvas so playback does not
/// jitter.
/// </remarks>
[Tool]
public partial class AnimationPanel : GridPanel
{
    private const int MaxBody = 0x1000;

    private SpinBox _action, _dir;
    private List<int> _ids;

    public override string SmokeQuery => "0x0190";

    protected override int IconSize => 64;

    protected override string Placeholder => "body (0x0190, 400)";

    protected override void BuildToolbar(HBoxContainer bar)
    {
        bar.AddChild(new Label { Text = "action" });
        _action = new SpinBox { MinValue = 0, MaxValue = AnimationsLoader.MAX_ACTIONS - 1, Value = 0 };
        _action.ValueChanged += _ => Reinspect();
        bar.AddChild(_action);
        bar.AddChild(new Label { Text = "dir" });
        _dir = new SpinBox { MinValue = 0, MaxValue = 7, Value = 1 };
        _dir.ValueChanged += _ => Reinspect();
        bar.AddChild(_dir);
    }

    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
        {
            _ids = new List<int>();
            var anims = Data.Animations;
            for (int body = 0; body < MaxBody; body++)
            {
                try
                {
                    if (anims.AnimationExists((ushort)body, 0) || anims.AnimationExists((ushort)body, 4))
                    {
                        _ids.Add(body);
                    }
                }
                catch (Exception)
                {
                    // A broken index entry is the data's problem, not a reason
                    // to lose the panel.
                }
            }
        }

        return _ids.Concat(Data.Assets?.Ids(AssetKind.Animation) ?? new List<int>()).Distinct().OrderBy(id => id);
    }

    protected override string Caption(int id) => $"{id:X4}";

    protected override bool Matches(int id, string query) => false;

    protected override Image Icon(int id)
    {
        Image[] frames = Frames(id, 0, 1, out _, out _, out _);
        return frames.Length > 0 ? frames[0] : null;
    }

    protected override Inspection Describe(int id)
    {
        byte action = (byte)_action.Value;
        byte dir = (byte)_dir.Value;
        Image[] frames = Frames(id, action, dir, out ushort hue, out string how, out double fps);

        var anims = Data.Animations;
        var sb = new StringBuilder();
        sb.Append($"[b]Body 0x{id:X4}[/b] ({id})\n");
        sb.Append($"type   {anims.GetAnimType((ushort)id)}\n");
        sb.Append($"flags  {anims.GetAnimFlags((ushort)id)}\n");
        sb.Append($"action {action}   dir {dir} {how}\n");
        sb.Append($"frames {frames.Length}" + (frames.Length > 0 ? $"   canvas {frames[0].GetWidth()} x {frames[0].GetHeight()}" : "") + "\n");
        if (hue != 0)
        {
            sb.Append($"hue    {hue} (from body conversion)\n");
        }

        var inspection = new Inspection
        {
            Source = "Animations",
            Id = $"0x{id:X4}",
            Frames = frames,
            Fps = fps,
            Text = sb.ToString(),
        };
        inspection.Actions.Add(("Edit in Pixelorama", () =>
        {
            try
            {
                OverlayAnimationClip clip = ExchangeClip(id, action, dir);
                string png = ArtExchange.ExportAnimation(Data, id, clip);
                string why = ExternalTools.OpenPixelorama(png);
                if (why != null) GD.PrintErr($"[GUO editor] {why}");
            }
            catch (Exception ex) { GD.PrintErr($"[GUO editor] animation export: {ex.Message}"); }
        }));
        return inspection;
    }

    internal OverlayAnimationClip ExchangeClip(int body, byte action, byte dir)
    {
        if (Data.Assets?.LoadAnimation(body, action, dir, out _) is OverlayAnimationClip overlay) return overlay;
        bool mirror = false;
        byte stored = dir;
        Data.Animations.GetAnimDirection(ref stored, ref mirror);
        var sprites = Data.Animations.GetAnimationFrames((ushort)body, action, stored, out _, out _);
        var frames = new List<Image>();
        var centers = new List<Vector2I>();
        foreach (SpriteInfo sprite in sprites)
        {
            Image image = ReadFrame(sprite);
            if (image == null) image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            else if (mirror) image.FlipX();
            frames.Add(image);
            centers.Add(new Vector2I(mirror ? image.GetWidth() - sprite.Center.X : sprite.Center.X, sprite.Center.Y));
        }

        return new OverlayAnimationClip { Action = action, Direction = dir, Fps = 8,
            Frames = frames.ToArray(), Centers = centers.ToArray() };
    }

    /// <summary>One action in one of the eight directions, as composited frames.</summary>
    private Image[] Frames(int body, byte action, byte dir, out ushort hue, out string how, out double fps)
    {
        hue = 0;
        how = "";
        fps = 8;
        if (Data.Assets?.LoadAnimation(body, action, dir, out _) is OverlayAnimationClip clip)
        {
            how = "-> editor overlay (explicit direction)";
            fps = clip.Fps;
            return clip.PreviewFrames();
        }
        var anims = Data.Animations;
        bool mirror = false;
        byte fileDir = dir;
        anims.GetAnimDirection(ref fileDir, ref mirror);

        Span<SpriteInfo> sprites;
        bool uop;
        try
        {
            sprites = anims.GetAnimationFrames((ushort)body, action, fileDir, out hue, out uop);
        }
        catch (Exception ex)
        {
            how = $"(failed: {ex.GetType().Name})";
            return Array.Empty<Image>();
        }

        how = $"-> stored dir {fileDir}{(mirror ? " mirrored" : "")}, {(uop ? "UOP" : "MUL")}";

        // Bounds of every frame around the foot, so one canvas holds them all.
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (SpriteInfo s in sprites)
        {
            if (s.Texture == null)
            {
                continue;
            }

            int x = mirror ? s.Center.X - s.UV.Width : -s.Center.X;
            int y = -(s.UV.Height + s.Center.Y);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + s.UV.Width);
            maxY = Math.Max(maxY, y + s.UV.Height);
        }

        if (minX == int.MaxValue)
        {
            return Array.Empty<Image>();
        }

        int w = maxX - minX, h = maxY - minY;
        var result = new List<Image>();
        foreach (SpriteInfo s in sprites)
        {
            Image canvas = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            if (s.Texture != null)
            {
                Image frame = ReadFrame(s);
                if (frame != null)
                {
                    if (mirror)
                    {
                        frame.FlipX();
                    }

                    int x = mirror ? s.Center.X - s.UV.Width : -s.Center.X;
                    int y = -(s.UV.Height + s.Center.Y);
                    canvas.BlitRect(frame, new Rect2I(0, 0, frame.GetWidth(), frame.GetHeight()), new Vector2I(x - minX, y - minY));
                }
            }

            result.Add(canvas);
        }

        return result.ToArray();
    }

    private static Image ReadFrame(SpriteInfo s)
    {
        int w = s.UV.Width, h = s.UV.Height;
        var px = new uint[w * h];
        if (!TextureAtlas.TryReadRegion(s.Texture, s.UV, px))
        {
            return null;
        }

        // Atlas pages are Rgba8; keep their alpha where it is set, and treat
        // any other non-zero pixel as opaque, as the loaders do.
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i];
            byte a = (byte)(p >> 24);
            if (a == 0 && (p & 0xFFFFFF) != 0)
            {
                a = 0xFF;
            }

            rgba[i * 4 + 0] = (byte)(p & 0xFF);
            rgba[i * 4 + 1] = (byte)((p >> 8) & 0xFF);
            rgba[i * 4 + 2] = (byte)((p >> 16) & 0xFF);
            rgba[i * 4 + 3] = a;
        }

        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
    }
}
#endif
