#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

/// <summary>
/// The inspector buttons phase 5 adds to the Art and Gumps panels (ADR-0020):
/// save the asset on show as a PNG to edit elsewhere, import a PNG as its
/// replacement in the world project, and revert to the install's.
/// </summary>
public static class AssetActions
{
    public static void Add(Inspection ins, EditorData data, AssetKind kind, int id, Image current)
    {
        AssetOverlay assets = data.Assets;
        if (assets == null)
        {
            return;
        }

        bool replaced = assets.Has(kind, id);
        ins.Text += replaced
            ? $"[color=yellow]replaced by the world project[/color]: {assets.RelativePathOf(kind, id)} (in the world project)\n"
            : "from the install\n";

        if (current != null)
        {
            ins.Actions.Add(("Export PNG...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, $"{Stem(kind)}_0x{id:X4}.png",
                path => { EnsureExportDestination(path); if (current.SavePng(path) != Error.Ok) throw new IOException("Could not export PNG."); })));
        }

        if (current != null && kind != AssetKind.Hue)
        {
            ins.ArtKind = kind;
            ins.ArtId = id;
            ins.Actions.Add(("Edit in Pixelorama", () => ReportEdit(EditIn(data, kind, id, current, pinta: false))));
            ins.Actions.Add(("Edit in the Pixelorama tab", () => ReportEdit(EditIn(data, kind, id, current, pinta: false, embedded: true))));
            ins.Actions.Add(("Open configured image editor (Pinta)", () => ReportEdit(EditIn(data, kind, id, current, pinta: true))));
        }

        ins.Actions.Add(("Replace from image...", () => Pick(EditorFileDialog.FileModeEnum.OpenFile, null, path =>
        {
            string why = ImportFile(data, kind, id, path);
            if (why != null)
            {
                GD.PrintErr($"[GUO editor] import {kind} 0x{id:X4}: {why}");
            }
        })));

        if (replaced)
        {
            ins.Actions.Add(("Revert", () =>
            {
                string rel = assets.RelativePathOf(kind, id);
                assets.Revert(kind, id);
                new AssetProvenance(assets).Remove(rel);
                data.ReapplyAssets(kind, id);
            }));
        }
    }

    /// <summary>
    /// The Hues panel's buttons: a hue is saved and imported as a strip, one
    /// pixel per colour of its 32; the name and table range are kept.
    /// </summary>
    public static void AddHue(Inspection ins, EditorData data, int hue, Image strip, string name, ushort start, ushort end)
    {
        AssetOverlay assets = data.Assets;
        if (assets == null)
        {
            return;
        }

        bool replaced = assets.Has(AssetKind.Hue, hue);
        ins.Text += replaced
            ? $"[color=yellow]replaced by the world project[/color]: {assets.RelativePathOf(AssetKind.Hue, hue)} (in the world project)\n"
            : "from the install\n";

        ins.Actions.Add(("Save strip PNG...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, $"hue_{hue}.png",
            path => strip.SavePng(path))));
        ins.Actions.Add(("Import strip PNG...", () => Pick(EditorFileDialog.FileModeEnum.OpenFile, null, path =>
        {
            string why = ImportHueFile(data, hue, path, name, start, end);
            if (why != null)
            {
                GD.PrintErr($"[GUO editor] import hue {hue}: {why}");
            }
        })));

        if (replaced)
        {
            ins.Actions.Add(("Revert", () =>
            {
                assets.Revert(AssetKind.Hue, hue);
                data.ReapplyAssets(AssetKind.Hue, hue);
            }));
        }
    }

    public static string ImportHueFile(EditorData data, int hue, string path, string name, ushort start, ushort end)
    {
        Image img = Image.LoadFromFile(path);
        string why = img == null ? $"could not read {path}" : data.Assets.ImportHue(hue, img, name, start, end);
        if (why == null)
        {
            data.ReapplyAssets(AssetKind.Hue, hue);
        }

        return why;
    }

    /// <summary>Imports a PNG file as a replacement and re-applies. Null on success, else why not.</summary>
    public static string ImportFile(EditorData data, AssetKind kind, int id, string path)
    {
        Image img = Image.LoadFromFile(path);
        if (img == null)
        {
            return $"could not read {path}";
        }

        // The shared post-process and the provenance record (ADR-0029). A PNG of unknown origin is
        // assumed derived from client art: it stays local until its maker says otherwise.
        var prov = new ArtProvenance { Tool = "import-png", Inputs = { Path.GetFileName(path) }, DerivedFromClientArt = true };
        string why = ArtExchange.ImportImage(data, kind, id, img, prov);
        if (why == null)
        {
            GD.Print($"[GUO editor] {kind} 0x{id:X4} replaced from {Path.GetFileName(path)}");
        }

        return why;
    }

    /// <summary>
    /// "Edit in Pixelorama" / "Edit in Pinta": writes the PNG and its sidecar to the exchange folder and
    /// opens the editor. Null on success, else what to tell the user. The result comes back through
    /// <see cref="ArtExchange.Poll"/>.
    /// </summary>
    public static string EditIn(EditorData data, AssetKind kind, int id, Image current, bool pinta, bool embedded = false)
    {
        if (pinta && ExternalTools.FindPinta() == null)
        {
            return ExternalTools.PintaHint;
        }

        string png = ArtExchange.Export(data, kind, id, current, pinta ? "pinta" : "out");
        return pinta ? ExternalTools.OpenPinta(png) : ExternalTools.OpenPixelorama(png, embedded);
    }

    public static void ReportEdit(string why)
    {
        if (why == null)
        {
            return;
        }

        GD.PushWarning($"[GUO editor] {why}");
        var dlg = new AcceptDialog { DialogText = why, Title = "Edit in an outside editor" };
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        EditorInterface.Singleton.GetBaseControl().AddChild(dlg);
        dlg.PopupCentered();
    }

    private static string Stem(AssetKind kind) => kind switch
    {
        AssetKind.Land => "land",
        AssetKind.Static => "static",
        AssetKind.Gump => "gump",
        _ => "hue",
    };

    private static void Pick(EditorFileDialog.FileModeEnum mode, string file, Action<string> then)
    {
        var dlg = new EditorFileDialog
        {
            FileMode = mode,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.png ; PNG image" },
        };

        if (file != null)
        {
            dlg.CurrentFile = file;
        }

        dlg.FileSelected += path =>
        {
            try { then(path); }
            catch (Exception ex) { ReportEdit(ex.Message); }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        EditorInterface.Singleton.GetBaseControl().AddChild(dlg);
        dlg.PopupFileDialog();
    }

    public static void EnsureExportDestination(string path)
    {
        string configured = EditorData.Setting("UO_CLIENT_DATA", "");
        if (string.IsNullOrWhiteSpace(configured)) throw new IOException("Configure the client install before exporting; its read-only boundary must be known.");
        string install = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(path);
        if (target.Equals(install, StringComparison.OrdinalIgnoreCase) || target.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export into the client install is refused.");
    }
}
#endif
