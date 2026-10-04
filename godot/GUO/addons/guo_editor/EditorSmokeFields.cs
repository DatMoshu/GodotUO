#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

/// <summary>
/// The asset fields on the real install: what the catalog suggests for a hex
/// id, a decimal id, bare hex digits and a name; that a field commits a typed
/// id and refuses one the install does not have; and that Gump Studio's
/// "Add gump art" box adds art on Enter and stays ready for the next.
/// Windowed, the gump proof also saves a frame of the suggestion list open.
/// </summary>
public partial class EditorSmoke
{
    private void CheckAssetFields()
    {
        var report = new Dictionary<string, object>();
        var failed = new List<string>();
        void Check(string name, bool ok, string got = "")
        {
            report[name] = ok ? "ok" : $"FAIL {got}";
            if (!ok)
            {
                failed.Add($"{name} ({got})");
            }
        }

        AssetCatalog cat = AssetCatalog.Of(_data);
        if (cat == null || !cat.Ready)
        {
            _failures.Add("Asset fields: the client data is not loaded");
            return;
        }

        string Top(AssetPickKind kind, string text, int n = 1) =>
            string.Join(",", cat.Query(kind, text, n).Select(h => AssetCatalog.Format(kind, h.Id)));

        var sw = Stopwatch.StartNew();
        foreach (AssetPickKind kind in new[] { AssetPickKind.Static, AssetPickKind.Gump, AssetPickKind.Hue })
        {
            sw.Restart();
            int count = cat.Ids(kind).Count;
            report[$"{kind}_ids"] = count;
            report[$"{kind}_build_ms"] = sw.ElapsedMilliseconds;
        }

        Check("static_hex", Top(AssetPickKind.Static, "0x0E75") == "0x0E75", Top(AssetPickKind.Static, "0x0E75"));
        Check("static_decimal", Top(AssetPickKind.Static, "3701") == "0x0E75", Top(AssetPickKind.Static, "3701"));
        Check("static_bare_hex", Top(AssetPickKind.Static, "e75") == "0x0E75", Top(AssetPickKind.Static, "e75"));
        Check("static_name", Top(AssetPickKind.Static, "backpack", 5).Contains("0x0E75"), Top(AssetPickKind.Static, "backpack", 5));
        Check("gump_hex", Top(AssetPickKind.Gump, "0x0BB8") == "0x0BB8", Top(AssetPickKind.Gump, "0x0BB8"));
        Check("gump_name", cat.Query(AssetPickKind.Gump, "paperdoll", 5).Count > 0, "no gump named paperdoll");
        Check("hue_decimal", Top(AssetPickKind.Hue, "33") == "0x0021", Top(AssetPickKind.Hue, "33"));
        sw.Restart();
        cat.Query(AssetPickKind.Static, "wooden chair", 40);
        report["static_name_query_ms"] = sw.ElapsedMilliseconds;
        Check("static_name_query_fast", sw.ElapsedMilliseconds < 150, $"{sw.ElapsedMilliseconds} ms");

        var field = new AssetField(_data, AssetPickKind.Gump);
        AddChild(field);
        try
        {
            int committed = -1;
            field.Committed += id => committed = id;
            field.Edit.Text = "bb8";
            field.CommitTyped();
            Check("field_commits_bare_hex", committed == 0x0BB8 && field.Value == 0x0BB8, $"0x{committed:X}");
            Check("field_shows_hex", field.Edit.Text == "0x0BB8", field.Edit.Text);

            committed = -1;
            field.Edit.Text = "0xFFFFF";
            field.CommitTyped();
            Check("field_refuses_missing_id", committed == -1 && field.Value == 0x0BB8, $"0x{committed:X}");

            field.Kind = AssetPickKind.Hue;
            field.AllowZero = true;
            committed = -1;
            field.Edit.Text = "";
            field.CommitTyped();
            Check("hue_field_empty_is_none", committed == 0, committed.ToString());
        }
        finally
        {
            RemoveChild(field);
            field.QueueFree();
        }

        GumpStudio studio = GuoEditorPlugin.GumpsMain;
        if (studio?.QuickAddField is AssetField quick)
        {
            int before = studio.Document.Elements.Count;
            quick.Edit.Text = "0x0BB8";
            quick.CommitTyped();
            GumpElement added = studio.Document.Elements.LastOrDefault();
            Check("quick_add_adds_art", studio.Document.Elements.Count == before + 1 && added?.Graphic == 0x0BB8 && added.Kind == GumpElementKind.Image,
                $"{before} -> {studio.Document.Elements.Count}, graphic {added?.Graphic}");
            Check("quick_add_clears", quick.Edit.Text.Length == 0, quick.Edit.Text);
            quick.Edit.Text = "stone";
            quick.CommitTyped();
            Check("quick_add_by_name", studio.Document.Elements.Count == before + 2, $"{studio.Document.Elements.Count - before} added");
            studio.Undo();
            studio.Undo();
            Check("quick_add_undoes", studio.Document.Elements.Count == before, studio.Document.Elements.Count.ToString());
        }
        else
        {
            Check("quick_add_present", false, "Gump Studio has no Add gump art box");
        }

        report["ok"] = failed.Count == 0;
        _report["asset_fields"] = report;
        foreach (string f in failed)
        {
            _failures.Add("Asset fields: " + f);
        }
    }

    /// <summary>Windowed gump proof: types into "Add gump art" and saves the open suggestion list.</summary>
    private void OpenQuickAddSuggestions()
    {
        AssetField quick = GuoEditorPlugin.GumpsMain?.QuickAddField;
        if (quick == null)
        {
            return;
        }

        quick.Edit.GrabFocus();
        quick.Edit.Text = "stone";
        quick.Suggest.Open("stone");
    }

    private void CaptureQuickAddSuggestions()
    {
        AssetField quick = GuoEditorPlugin.GumpsMain?.QuickAddField;
        int rows = quick?.Suggest.Ids.Count() ?? 0;
        if (rows == 0)
        {
            _failures.Add("Asset fields: the suggestion list showed no rows for \"stone\"");
        }

        using var image = _gumpProofWindow.GetTexture().GetImage();
        image.SavePng(Path.Combine(_out, $"asset_field_suggest{Suffix}.png"));
        _report["asset_field_suggest_rows"] = rows;
        quick?.Suggest.Close();
        if (quick != null)
        {
            quick.Edit.Text = "";
            quick.Edit.ReleaseFocus();
        }
    }
}
#endif
