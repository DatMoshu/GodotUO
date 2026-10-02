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
            ins.Actions.Add(("Save PNG...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, $"{Stem(kind)}_0x{id:X4}.png",
                path => current.SavePng(path))));
        }

        ins.Actions.Add(("Import PNG...", () => Pick(EditorFileDialog.FileModeEnum.OpenFile, null, path =>
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
                assets.Revert(kind, id);
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

        string why = data.Assets.Import(kind, id, img);
        if (why == null)
        {
            data.ReapplyAssets(kind, id);
            GD.Print($"[GUO editor] {kind} 0x{id:X4} replaced from {Path.GetFileName(path)}");
        }

        return why;
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
            then(path);
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        EditorInterface.Singleton.GetBaseControl().AddChild(dlg);
        dlg.PopupFileDialog();
    }
}
#endif
