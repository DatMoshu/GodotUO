#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Import and export menus for the legacy multi formats (ADR-0031, phase 2), through <c>tools/multi</c>'s
/// <c>import</c> and <c>export</c> (data_formats section 27). Files are read and written wherever the user points
/// the dialog; nothing here touches the client install.
/// </summary>
public partial class MultiEditView
{
    /// <summary>The formats tools/multi reads and writes, with a file extension and a label for each.</summary>
    public static readonly (string Format, string Ext, string Label)[] LegacyFormats =
    {
        ("txt", "txt", "Text (UOFiddler, Ultima SDK)"),
        ("uoa", "uoa", "UO Architect text"),
        ("uoab", "uoab", "UO Architect binary"),
        ("wsc", "wsc", "WSC world items"),
        ("csv-punt", "csv", "CSV (PUNT)"),
        ("csv-swerv", "csv", "CSV (SWERV)"),
        ("centred", "csv", "CentrED# blueprint"),
        ("uox3", "dfn", "UOX3 house items"),
    };

    private void BuildFormatMenus(Control bar)
    {
        foreach ((string title, bool export) in new[] { ("Import", false), ("Export", true) })
        {
            var menu = new MenuButton { Text = title + " ▾", TooltipText = export ? "Write the multi in another tool's format" : "Open another tool's multi file" };
            PopupMenu pop = menu.GetPopup();
            for (int i = 0; i < LegacyFormats.Length; i++)
            {
                pop.AddItem(LegacyFormats[i].Label, i);
            }

            bool ex = export;
            pop.IdPressed += id => OpenFormatDialog(LegacyFormats[(int)id], ex);
            bar.AddChild(menu);
        }
    }

    private void OpenFormatDialog((string Format, string Ext, string Label) f, bool export)
    {
        var dialog = new FileDialog
        {
            FileMode = export ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = (export ? "Export as " : "Import ") + f.Label,
            CurrentDir = Directory.Exists(MultiStore.EditDir) ? MultiStore.EditDir : EditorData.RepoRoot,
        };
        dialog.AddFilter("*." + f.Ext, f.Label);
        if (export)
        {
            dialog.CurrentFile = MultiStore.SafeName(_doc.Name) + "." + f.Ext;
        }

        dialog.FileSelected += p =>
        {
            if (export)
            {
                _ = ExportFileAsync(p, f.Format);
            }
            else
            {
                GuardUnsaved(() => _ = ImportFileAsync(p, f.Format));
            }

            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(800, 500));
    }

    /// <summary>Writes the whole multi to a file in <paramref name="format"/>. Returns an error text, or null.</summary>
    public async Task<string> ExportFileAsync(string path, string format)
    {
        var comps = new JsonArray();
        var hues = new JsonArray();
        foreach (MultiPart p in _doc.Parts)
        {
            comps.Add((JsonNode)new JsonArray(p.Id, p.X, p.Y, p.Z, p.Shown ? 1 : 0));
            hues.Add(p.Hue);
        }

        JsonObject r = await _gen.RequestAsync("export", new JsonObject
        {
            ["format"] = format,
            ["components"] = comps,
            ["hues"] = hues,
            ["name"] = _doc.Name,
            ["path"] = path,
        });
        string error = r["error"]?.GetValue<string>();
        _status.Text = error != null ? $"export failed: {error}" : $"exported {_doc.Parts.Count} components to {Path.GetFileName(path)} ({format})";
        return error;
    }

    /// <summary>Opens a file of another tool's format (detected from its name and head when <paramref name="format"/> is null).</summary>
    public async Task<string> ImportFileAsync(string path, string format = null)
    {
        var args = new JsonObject { ["path"] = path, ["recentre"] = true };
        if (format != null)
        {
            args["format"] = format;
        }

        JsonObject raw = await _gen.RequestAsync("import", args);
        GenResult r = MultiGenerateClient.Parse(raw);
        if (!r.Ok)
        {
            _status.Text = $"import failed: {r.Error}";
            return r.Error;
        }

        JsonArray hues = raw["hues"] as JsonArray;
        var parts = r.Components.Select((c, i) => new MultiPart
        {
            Id = (ushort)c.Item,
            X = (short)c.X,
            Y = (short)c.Y,
            Z = (short)c.Z,
            Shown = c.Visible,
            Hue = hues != null && i < hues.Count ? (ushort)(int)hues[i] : (ushort)0,
        }).ToList();
        OpenParts(MultiStore.SafeName(Path.GetFileNameWithoutExtension(path)), parts);
        _status.Text = $"imported {parts.Count} components from {Path.GetFileName(path)} ({raw["format"]})";
        return null;
    }
}
#endif
