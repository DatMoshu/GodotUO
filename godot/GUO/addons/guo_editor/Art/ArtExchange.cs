#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GUO.Assets;

/// <summary>One image trading hands with an outside editor: what it is, and where it came from.</summary>
public sealed class ArtSidecar
{
    public string Kind = "";
    public int Id;
    public int Hue;
    public int Width;
    public int Height;
    public string Stem = "";
    public ArtProvenance Provenance = new();
    public JsonObject Animation;

    public static string KindName(AssetKind k) => k switch
    {
        AssetKind.Land => "land",
        AssetKind.Static => "static",
        AssetKind.Gump => "gump",
        AssetKind.Animation => "animation",
        _ => "hue",
    };

    public static bool TryKind(string name, out AssetKind kind)
    {
        switch (name)
        {
            case "land": kind = AssetKind.Land; return true;
            case "static": kind = AssetKind.Static; return true;
            case "gump": kind = AssetKind.Gump; return true;
            case "animation": kind = AssetKind.Animation; return true;
            default: kind = AssetKind.Static; return false;
        }
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["kind"] = Kind,
            ["id"] = Id,
        };
        if (Hue > 0)
        {
            o["hue"] = Hue;
        }

        o["size"] = new JsonArray(Width, Height);
        o["stem"] = Stem;
        o["provenance"] = Provenance.ToJson();
        if (Animation != null) o["animation"] = Animation.DeepClone();
        return o;
    }

    public static ArtSidecar FromJson(JsonNode n)
    {
        var s = new ArtSidecar
        {
            Kind = (string)n?["kind"] ?? "",
            Id = n?["id"] is JsonNode id ? (int)id : 0,
            Hue = n?["hue"] is JsonNode hue ? (int)hue : 0,
            Stem = (string)n?["stem"] ?? "",
            Provenance = ArtProvenance.FromJson(n?["provenance"]),
            Animation = n?["animation"] as JsonObject,
        };
        if (n?["size"] is JsonArray a && a.Count == 2)
        {
            s.Width = (int)a[0];
            s.Height = (int)a[1];
        }

        return s;
    }
}

/// <summary>
/// The exchange folder (<c>UO_ART_EXCHANGE</c>, default <c>build/art_exchange</c>) the editor trades
/// images with Pixelorama and Pinta through (ADR-0029, docs/data_formats.md section 12):
/// <c>out/</c> what the editor hands to Pixelorama, <c>in/</c> what the Pixelorama extension saves back,
/// <c>pinta/</c> files Pinta edits in place, <c>done/</c> and <c>rejected/</c> what the watcher finished
/// with, and <c>hues.json</c> the user's own hue table for the extension. Never inside the client install.
/// </summary>
public static class ArtExchange
{
    public static string Root
    {
        get
        {
            string root = EditorData.Setting("UO_ART_EXCHANGE", "");
            return root.Length > 0 ? root : Path.Combine(EditorData.RepoRoot, "build", "art_exchange");
        }
    }

    public static string Sub(string name)
    {
        string p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string Stem(AssetKind kind, int id, int hue) =>
        $"{ArtSidecar.KindName(kind)}_0x{id:X4}" + (hue > 0 ? $"_h{hue}" : "");

    /// <summary>Export raw frame rectangles in common foot-aligned cells, with immutable centres.</summary>
    public static string ExportAnimation(EditorData data, int body, OverlayAnimationClip clip)
    {
        string why = AssetOverlay.ValidateAnimation(body, clip);
        if (why != null) throw new InvalidDataException(why);
        Image[] preview = clip.PreviewFrames();
        Vector2I cell = preview[0].GetSize();
        int count = preview.Length;
        int columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)),
            (int)Math.Ceiling((double)count / (16384 / cell.Y)), 16384 / cell.X);
        int rows = (count + columns - 1) / columns;
        Image sheet = Image.CreateEmpty(cell.X * columns, cell.Y * rows, false, Image.Format.Rgba8);
        Rect2I bounds = clip.Bounds();
        var frames = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            sheet.BlitRect(preview[i], new Rect2I(Vector2I.Zero, cell), new Vector2I(i % columns * cell.X, i / columns * cell.Y));
            int x = -clip.Centers[i].X - bounds.Position.X;
            int y = -clip.Frames[i].GetHeight() - clip.Centers[i].Y - bounds.Position.Y;
            frames.Add(new JsonObject { ["rect"] = new JsonArray(x, y, clip.Frames[i].GetWidth(), clip.Frames[i].GetHeight()),
                ["center"] = new JsonArray(clip.Centers[i].X, clip.Centers[i].Y) });
        }

        bool overlay = data.Assets?.LoadAnimation(body, clip.Action, clip.Direction, out _) != null;
        ArtProvenance known = overlay ? new AssetProvenance(data.Assets).Get(data.Assets.RelativePathOf(AssetKind.Animation, body)) : null;
        var provenance = new ArtProvenance { Tool = "guo-export", AiGenerated = known?.AiGenerated ?? false,
            Model = known?.Model, Workflow = known?.Workflow, Seed = known?.Seed,
            DerivedFromClientArt = !overlay || (known?.DerivedFromClientArt ?? true),
            Inputs = new List<string> { $"{(overlay ? "overlay" : "client")}:animation:0x{body:X4}:a{clip.Action}:d{clip.Direction}" } };
        string stem = $"animation_0x{body:X4}_a{clip.Action:D2}_d{clip.Direction}";
        var side = new ArtSidecar { Kind = "animation", Id = body, Stem = stem, Width = sheet.GetWidth(), Height = sheet.GetHeight(),
            Provenance = provenance, Animation = new JsonObject { ["action"] = clip.Action, ["direction"] = clip.Direction,
                ["fps"] = clip.Fps, ["columns"] = columns, ["cell_size"] = new JsonArray(cell.X, cell.Y), ["frames"] = frames } };
        string png = Path.Combine(Sub("out"), stem + ".png");
        Error error = sheet.SavePng(png);
        if (error != Error.Ok) throw new IOException($"could not save animation sheet: {error}");
        File.WriteAllText(Path.ChangeExtension(png, ".json"), side.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return png;
    }

    private static string ImportAnimationSheet(EditorData data, ArtSidecar side, Image sheet)
    {
        try
        {
            JsonObject meta = side.Animation;
            if (meta?["frames"] is not JsonArray frames || frames.Count < 1 || frames.Count > 256
                || meta["cell_size"] is not JsonArray cell || cell.Count != 2)
                return "animation sheet needs frame metadata and cell size";
            int w = (int)cell[0], h = (int)cell[1], columns = (int)meta["columns"];
            if (w < 1 || w > 4096 || h < 1 || h > 4096 || columns < 1 || columns > frames.Count)
                return "invalid animation sheet cell layout";
            int rows = (frames.Count + columns - 1) / columns;
            long width = (long)w * columns, height = (long)h * rows;
            if (width > 16384 || height > 16384 || width * height > 33554432
                || sheet.GetWidth() != width || sheet.GetHeight() != height || side.Width != width || side.Height != height)
                return "animation sheet dimensions do not match its bounded layout";
            var clip = new OverlayAnimationClip { Action = (int)meta["action"], Direction = (int)meta["direction"], Fps = (double)meta["fps"],
                Frames = new Image[frames.Count], Centers = new Vector2I[frames.Count] };
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i]?["rect"] is not JsonArray rect || rect.Count != 4
                    || frames[i]?["center"] is not JsonArray center || center.Count != 2)
                    return "animation frame needs its original rectangle and centre";
                var region = new Rect2I((int)rect[0], (int)rect[1], (int)rect[2], (int)rect[3]);
                if (region.Position.X < 0 || region.Position.Y < 0 || region.Size.X < 1 || region.Size.Y < 1
                    || region.Size.X > AssetOverlay.MaxStaticSize || region.Size.Y > AssetOverlay.MaxStaticSize
                    || (long)region.Position.X + region.Size.X > w || (long)region.Position.Y + region.Size.Y > h)
                    return "animation frame rectangle is outside its cell";
                Vector2I origin = new(i % columns * w, i / columns * h);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (!region.HasPoint(new Vector2I(x, y)) && sheet.GetPixel(origin.X + x, origin.Y + y).A8 >= 128)
                            return "animation edits extend outside an original frame rectangle";
                clip.Frames[i] = sheet.GetRegion(new Rect2I(origin + region.Position, region.Size));
                clip.Centers[i] = new Vector2I((int)center[0], (int)center[1]);
            }

            for (int i = frames.Count; i < rows * columns; i++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (sheet.GetPixel(i % columns * w + x, i / columns * h + y).A8 >= 128)
                            return "animation sheet has paint in an unused cell";

            if (data?.Assets == null) return "no world project is open";
            string why = data.Assets.ImportAnimation(side.Id, clip, side.Provenance);
            if (why == null) data.ReapplyAssets(AssetKind.Animation, side.Id);
            return why;
        }
        catch (Exception ex) { return $"invalid animation sheet: {ex.Message}"; }
    }

    /// <summary>
    /// Writes the asset as PNG plus sidecar into <paramref name="folder"/> (<c>out</c> or <c>pinta</c>)
    /// and returns the PNG path. The provenance says the image is derived from client art unless the
    /// world project already holds an original (non-derived) replacement of it.
    /// </summary>
    public static string Export(EditorData data, AssetKind kind, int id, Image img, string folder = "out", int hue = 0)
    {
        string stem = Stem(kind, id, hue);
        string dir = Sub(folder);
        string png = Path.Combine(dir, stem + ".png");
        img.SavePng(png);

        bool derived = true;
        if (data.Assets != null && data.Assets.Has(kind, id))
        {
            ArtProvenance known = new AssetProvenance(data.Assets).Get(data.Assets.RelativePathOf(kind, id));
            derived = known?.DerivedFromClientArt ?? true;
        }

        var side = new ArtSidecar
        {
            Kind = ArtSidecar.KindName(kind),
            Id = id,
            Hue = hue,
            Width = img.GetWidth(),
            Height = img.GetHeight(),
            Stem = stem,
            Provenance = new ArtProvenance
            {
                Tool = folder == "pinta" ? "pinta" : "pixelorama",
                Inputs = new List<string> { $"{(derived ? "client" : "overlay")}:{ArtSidecar.KindName(kind)}:0x{id:X4}" },
                DerivedFromClientArt = derived,
            },
        };
        string json = Path.Combine(dir, stem + ".json");
        File.WriteAllText(json, side.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        // Pinta saves over the PNG: remember "unedited" as the sidecar's time, equal to the PNG's.
        File.SetLastWriteTimeUtc(json, File.GetLastWriteTimeUtc(png));
        WriteHues(data);
        return png;
    }

    /// <summary>
    /// The user's own hue table as <c>hues.json</c> ({format, hues: [{id, name, colors: [32 x RRGGBB]}]})
    /// for the Pixelorama extension's palettes. Read from the loaded hues.mul, written only to the exchange folder.
    /// </summary>
    public static unsafe void WriteHues(EditorData data)
    {
        if (!data.IsLoaded)
        {
            return;
        }

        string path = Path.Combine(Root, "hues.json");
        Directory.CreateDirectory(Root);
        if (File.Exists(path))
        {
            return;
        }

        HuesLoader hues = data.Files.Hues;
        int count = Math.Min(hues.HuesCount, hues.HuesRange.Length * 8);
        var list = new JsonArray();
        for (int hue = 1; hue <= count; hue++)
        {
            ref HuesBlock b = ref hues.HuesRange[(hue - 1) >> 3].Entries[(hue - 1) % 8];
            int n = 0;
            fixed (byte* p = b.Name)
            {
                while (n < 20 && p[n] != 0)
                {
                    n++;
                }

                string name = System.Text.Encoding.ASCII.GetString(p, n);
                var colors = new JsonArray();
                for (int i = 0; i < 32; i++)
                {
                    uint c = GUO.Utility.HuesHelper.Color16To32(b.ColorTable[i]);
                    colors.Add($"{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}");
                }

                list.Add(new JsonObject { ["id"] = hue, ["name"] = name, ["colors"] = colors });
            }
        }

        File.WriteAllText(path, new JsonObject { ["format"] = 1, ["hues"] = list }.ToJsonString());
    }

    // --- the watcher ----------------------------------------------------------

    public sealed class Outcome
    {
        public string Stem;
        public string Source;
        public bool Imported;
        public string Why;
        public List<string> Notes = new();
    }

    /// <summary>
    /// One look at <c>in/</c> (saved by the Pixelorama extension) and <c>pinta/</c> (edited in place by
    /// Pinta): every finished image is validated, post-processed, imported into the world project's overlay
    /// and recorded in its provenance file. Finished <c>in/</c> pairs move to <c>done/</c> or <c>rejected/</c>.
    /// </summary>
    public static List<Outcome> Poll(EditorData data)
    {
        var outcomes = new List<Outcome>();
        if (data?.Assets == null || !Directory.Exists(Root))
        {
            return outcomes;
        }

        string inbox = Path.Combine(Root, "in");
        if (Directory.Exists(inbox))
        {
            foreach (string png in Directory.GetFiles(inbox, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                string json = Path.ChangeExtension(png, ".json");
                if (!File.Exists(json))
                {
                    continue;
                }

                Outcome o = ImportPair(data, png, json, "pixelorama");
                if (o == null)
                {
                    continue; // still being written
                }

                Settle(png, json, o.Imported ? "done" : "rejected", o.Why);
                outcomes.Add(o);
            }
        }

        string pinta = Path.Combine(Root, "pinta");
        if (Directory.Exists(pinta))
        {
            foreach (string png in Directory.GetFiles(pinta, "*.png"))
            {
                string json = Path.ChangeExtension(png, ".json");
                if (!File.Exists(json) || File.GetLastWriteTimeUtc(png) <= File.GetLastWriteTimeUtc(json).AddMilliseconds(500))
                {
                    continue;
                }

                Outcome o = ImportPair(data, png, json, "pinta");
                if (o == null)
                {
                    continue;
                }

                // Edited again later counts again; an unchanged file is not imported twice.
                File.SetLastWriteTimeUtc(json, File.GetLastWriteTimeUtc(png));
                outcomes.Add(o);
            }
        }

        return outcomes;
    }

    private static void Settle(string png, string json, string folder, string why)
    {
        string dir = Sub(folder);
        foreach (string f in new[] { png, json })
        {
            string to = Path.Combine(dir, Path.GetFileName(f));
            File.Move(f, to, overwrite: true);
        }

        if (why != null)
        {
            File.WriteAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(png) + ".reason.txt"), why);
        }
    }

    /// <summary>Imports one PNG with its sidecar. Null when the file cannot be read yet.</summary>
    public static Outcome ImportPair(EditorData data, string png, string json, string tool)
    {
        var o = new Outcome { Stem = Path.GetFileNameWithoutExtension(png), Source = tool };
        ArtSidecar side;
        try
        {
            side = ArtSidecar.FromJson(JsonNode.Parse(File.ReadAllText(json)));
        }
        catch (Exception ex)
        {
            o.Why = $"unreadable sidecar: {ex.Message}";
            return o;
        }

        if (!ArtSidecar.TryKind(side.Kind, out AssetKind kind))
        {
            o.Why = $"unsupported kind '{side.Kind}' (land, static, gump and animation are imported)";
            return o;
        }

        Image img = Image.LoadFromFile(png);
        if (img == null || img.IsEmpty())
        {
            // A save in progress: try again at the next look, unless it is old.
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(png) < TimeSpan.FromSeconds(10)
                ? null
                : Fail(o, $"could not read {Path.GetFileName(png)}");
        }

        string why = kind == AssetKind.Animation ? ImportAnimationSheet(data, side, img)
            : ImportImage(data, kind, side.Id, img, side.Provenance, o.Notes);
        if (why != null)
        {
            return Fail(o, why);
        }

        o.Imported = true;
        GD.Print($"[GUO editor] art exchange: {side.Kind} 0x{side.Id:X4} imported from {tool} ({string.Join("; ", o.Notes)})");
        return o;
    }

    private static Outcome Fail(Outcome o, string why)
    {
        o.Why = why;
        GD.PrintErr($"[GUO editor] art exchange: {o.Stem} refused: {why}");
        return o;
    }

    /// <summary>
    /// The shared import path of every outside image (the exchange watcher, the Art dock): post-process,
    /// import into the overlay, apply, record provenance. Null on success, else why not.
    /// </summary>
    public static string ImportImage(EditorData data, AssetKind kind, int id, Image img, ArtProvenance prov, List<string> notes = null)
    {
        UoPostProcess.Result r = UoPostProcess.Run(img, kind);
        if (r.Error != null)
        {
            return r.Error;
        }

        notes?.AddRange(r.Notes);
        string why = data.Assets.Import(kind, id, r.Image);
        if (why != null)
        {
            return why;
        }

        new AssetProvenance(data.Assets).Record(data.Assets.RelativePathOf(kind, id), prov);
        data.ReapplyAssets(kind, id);
        return null;
    }
}
#endif
