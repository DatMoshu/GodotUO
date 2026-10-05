#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using GUO.Assets;

/// <summary>One explicit action/direction, with the client's signed centres preserved.</summary>
public sealed class OverlayAnimationClip
{
    public int Action;
    public int Direction;
    public double Fps = 8;
    public Image[] Frames = Array.Empty<Image>();
    public Vector2I[] Centers = Array.Empty<Vector2I>();

    internal Rect2I Bounds()
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int i = 0; i < Frames.Length; i++)
        {
            int x = -Centers[i].X, y = -Frames[i].GetHeight() - Centers[i].Y;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + Frames[i].GetWidth());
            bottom = Math.Max(bottom, y + Frames[i].GetHeight());
        }

        return new Rect2I(left, top, right - left, bottom - top);
    }

    /// <summary>Common foot-aligned canvases, using the same centre maths as AnimationPanel.</summary>
    public Image[] PreviewFrames()
    {
        Rect2I bounds = Bounds();
        var images = new Image[Frames.Length];
        for (int i = 0; i < Frames.Length; i++)
        {
            Image frame = Frames[i];
            Image canvas = Image.CreateEmpty(bounds.Size.X, bounds.Size.Y, false, Image.Format.Rgba8);
            canvas.BlitRect(frame, new Rect2I(0, 0, frame.GetWidth(), frame.GetHeight()),
                new Vector2I(-Centers[i].X - bounds.Position.X, -frame.GetHeight() - Centers[i].Y - bounds.Position.Y));
            images[i] = canvas;
        }

        return images;
    }
}

public sealed partial class AssetOverlay
{
    private static string ValidateAnimation(int body, OverlayAnimationClip clip)
    {
        if (body < 0 || body >= 0x1000 || clip == null || clip.Action < 0 || clip.Action >= AnimationsLoader.MAX_ACTIONS
            || clip.Direction < 0 || clip.Direction > 7)
        {
            return "animation body, action or direction is outside the client's range";
        }

        if (!double.IsFinite(clip.Fps) || clip.Fps < 1 || clip.Fps > 60 || clip.Frames == null || clip.Centers == null
            || clip.Frames.Length < 1 || clip.Frames.Length > 256 || clip.Centers.Length != clip.Frames.Length)
        {
            return "animation needs 1..256 paired frames/centres and 1..60 fps";
        }

        long pixels = 0;
        for (int i = 0; i < clip.Frames.Length; i++)
        {
            Image frame = clip.Frames[i];
            Vector2I center = clip.Centers[i];
            if (frame == null || frame.IsEmpty() || frame.GetWidth() > MaxStaticSize || frame.GetHeight() > MaxStaticSize
                || center.X < -4096 || center.X > 4096 || center.Y < -4096 || center.Y > 4096)
            {
                return "animation frame size or centre is outside the supported range";
            }

            pixels += (long)frame.GetWidth() * frame.GetHeight();
        }

        Rect2I bounds = clip.Bounds();
        return pixels > 16777216 || bounds.Size.X > 4096 || bounds.Size.Y > 4096
            || (long)bounds.Size.X * bounds.Size.Y * clip.Frames.Length > 16777216
            ? "animation exceeds the total pixels or common canvas limit" : null;
    }

    private JsonObject AnimationManifest(int body)
    {
        string path = PathOf(AssetKind.Animation, body);
        if (!File.Exists(path))
        {
            return new JsonObject { ["format"] = 1, ["body"] = body, ["clips"] = new JsonArray() };
        }

        if (new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("animation manifest exceeds 2 MiB");
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root == null || (int?)root["format"] != 1 || (int?)root["body"] != body
            || root["clips"] is not JsonArray clips || clips.Count > AnimationsLoader.MAX_ACTIONS * 8)
        {
            throw new InvalidDataException("invalid animation manifest");
        }

        var keys = new HashSet<(int, int)>();
        foreach (JsonNode node in clips)
        {
            int action = (int)node["action"], direction = (int)node["direction"];
            if (action < 0 || action >= AnimationsLoader.MAX_ACTIONS || direction < 0 || direction > 7
                || !keys.Add((action, direction)))
            {
                throw new InvalidDataException("animation clips need unique valid action/direction pairs");
            }
        }

        return root;
    }

    /// <summary>Import a single clip without trimming its frames or touching the installed animation files.</summary>
    public string ImportAnimation(int body, OverlayAnimationClip clip, ArtProvenance provenance = null)
    {
        string why = ValidateAnimation(body, clip);
        if (why != null)
        {
            return why;
        }

        try
        {
            JsonObject root = AnimationManifest(body);
            JsonArray clips = (JsonArray)root["clips"];
            var frames = new JsonArray();
            string run = Guid.NewGuid().ToString("N");
            string folder = Path.Combine(Root, Folder(AssetKind.Animation));
            Directory.CreateDirectory(Path.Combine(folder, $"0x{body:X4}"));
            for (int i = 0; i < clip.Frames.Length; i++)
            {
                string relative = $"0x{body:X4}/a{clip.Action:D2}_d{clip.Direction}_{run}_{i:D3}.png";
                UoPostProcess.Result processed = UoPostProcess.Run(clip.Frames[i], AssetKind.Animation);
                if (processed.Error != null)
                {
                    return processed.Error;
                }

                Error error = processed.Image.SavePng(Path.Combine(folder, relative));
                if (error != Error.Ok)
                {
                    return $"could not save animation frame: {error}";
                }

                frames.Add(new JsonObject { ["image"] = relative,
                    ["center"] = new JsonArray(clip.Centers[i].X, clip.Centers[i].Y) });
            }

            JsonObject replacement = new() { ["action"] = clip.Action, ["direction"] = clip.Direction,
                ["fps"] = clip.Fps, ["frames"] = frames };
            int old = -1;
            for (int i = 0; i < clips.Count; i++)
            {
                if ((int?)clips[i]?["action"] == clip.Action && (int?)clips[i]?["direction"] == clip.Direction)
                {
                    old = i;
                    break;
                }
            }

            if (old >= 0) clips[old] = replacement;
            else clips.Add(replacement);
            string path = PathOf(AssetKind.Animation, body);
            string manifest = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            if (Encoding.UTF8.GetByteCount(manifest) > 2 * 1024 * 1024)
            {
                return "animation manifest exceeds 2 MiB";
            }

            File.WriteAllText(path + ".tmp", manifest);
            File.Move(path + ".tmp", path, true);
            var records = new AssetProvenance(this);
            string relativePath = RelativePathOf(AssetKind.Animation, body);
            ArtProvenance previous = records.Get(relativePath);
            provenance ??= new ArtProvenance { Tool = "import-animation", DerivedFromClientArt = true };
            // A body can retain other clips; importing an original must not erase their derived origin.
            provenance.DerivedFromClientArt |= previous?.DerivedFromClientArt ?? false;
            provenance.AiGenerated |= previous?.AiGenerated ?? false;
            if (previous != null)
            {
                provenance.Inputs = provenance.Inputs.Concat(previous.Inputs).Distinct().ToList();
            }

            records.Record(relativePath, provenance);
            return null;
        }
        catch (Exception ex)
        {
            return $"could not import animation: {ex.Message}";
        }
    }

    /// <summary>Load only the requested explicit direction. Missing clips leave the client animation in use.</summary>
    public OverlayAnimationClip LoadAnimation(int body, int action, int direction, out string why)
    {
        why = null;
        if (body < 0 || body >= 0x1000 || action < 0 || action >= AnimationsLoader.MAX_ACTIONS || direction < 0 || direction > 7)
        {
            why = "animation body, action or direction is outside the client's range";
            return null;
        }
        try
        {
            JsonArray clips = (JsonArray)AnimationManifest(body)["clips"];
            JsonNode node = clips.FirstOrDefault(c => (int?)c?["action"] == action && (int?)c?["direction"] == direction);
            if (node == null) return null;
            if (node["frames"] is not JsonArray frames || frames.Count < 1 || frames.Count > 256)
            {
                throw new InvalidDataException("invalid animation frame count");
            }

            var clip = new OverlayAnimationClip { Action = action, Direction = direction, Fps = (double)node["fps"],
                Frames = new Image[frames.Count], Centers = new Vector2I[frames.Count] };
            string folder = Path.GetFullPath(Path.Combine(Root, Folder(AssetKind.Animation)));
            string prefix = Path.Combine(folder, $"0x{body:X4}") + Path.DirectorySeparatorChar;
            for (int i = 0; i < frames.Count; i++)
            {
                string name = (string)frames[i]?["image"];
                if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name)
                    || name.Replace('\\', '/').Split('/').Any(part => part is "." or "..")
                    || !string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("animation image must be relative to its body folder");
                }

                string path = Path.GetFullPath(Path.Combine(folder, name));
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)
                    || frames[i]?["center"] is not JsonArray center || center.Count != 2)
                {
                    throw new InvalidDataException("invalid animation image path or centre");
                }

                clip.Frames[i] = Image.LoadFromFile(path);
                clip.Frames[i]?.Convert(Image.Format.Rgba8);
                clip.Centers[i] = new Vector2I((int)center[0], (int)center[1]);
            }

            why = ValidateAnimation(body, clip);
            return why == null ? clip : null;
        }
        catch (Exception ex)
        {
            why = $"could not load animation: {ex.Message}";
            return null;
        }
    }
}
#endif
