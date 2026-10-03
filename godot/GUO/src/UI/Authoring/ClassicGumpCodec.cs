// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GUO.UI.Authoring;

/// <summary>Classic wire layout plus its separate text table. Unknown commands round-trip verbatim.</summary>
public static class ClassicGumpCodec
{
    public static GumpDocument Import(string layout, IEnumerable<string> texts)
    {
        if (layout.Length > 4_000_000) throw new InvalidDataException("Layout is too large.");
        var doc = new GumpDocument { Name = "Imported classic gump", Texts = texts.ToList() };
        int page = 0, group = 0, end = 0;
        foreach (Match match in Regex.Matches(layout, @"\{([^{}]*)\}"))
        {
            if (!string.IsNullOrWhiteSpace(layout[end..match.Index]))
                throw new InvalidDataException("Expected a brace-delimited classic layout.");
            end = match.Index + match.Length;
            string raw = match.Groups[1].Value.Trim();
            string[] p = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            int N(int i) => i >= p.Length ? throw new FormatException("Missing argument") :
                p[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt32(p[i][2..], 16) : int.Parse(p[i], CultureInfo.InvariantCulture);
            string T(int i) => N(i) >= 0 && N(i) < doc.Texts.Count ? doc.Texts[N(i)] :
                throw new FormatException("Text index is outside the text table");
            var e = new GumpElement { Name = p[0], Page = page, Group = group, Raw = raw, Kind = GumpElementKind.Raw };
            try
            {
                int expected = p[0].ToLowerInvariant() switch
                {
                    "page" or "group" => 2, "gumppic" => 4, "gumppictiled" or "resizepic" => 6,
                    "text" => 5, "croppedtext" or "checkbox" or "radio" => 7,
                    "htmlgump" or "button" or "textentry" => 8, "textentrylimited" => 9, _ => 0
                };
                if (expected != 0 && p.Length != expected) throw new FormatException("Unsupported argument count");
                switch (p[0].ToLowerInvariant())
                {
                    case "page": page = N(1); continue;
                    case "group": group = N(1); continue;
                    case "gumppic":
                        // Hue and extended arguments stay raw until explicitly supported.
                        if (p.Length != 4) break;
                        e.Kind = GumpElementKind.Image; e.Graphic = N(3); break;
                    case "gumppictiled":
                        e.Kind = GumpElementKind.TiledImage; e.Width = N(3); e.Height = N(4); e.Graphic = N(5); break;
                    case "resizepic":
                        e.Kind = GumpElementKind.Panel; e.Graphic = N(3); e.Width = N(4); e.Height = N(5); break;
                    case "text":
                        e.Kind = GumpElementKind.Label; e.Hue = N(3); e.Text = T(4); break;
                    case "croppedtext":
                        e.Kind = GumpElementKind.Label; e.Width = N(3); e.Height = N(4); e.Hue = N(5); e.Text = T(6); break;
                    case "htmlgump":
                        // Keep background and scrollbar flags in Raw for export.
                        e.Kind = GumpElementKind.Html; e.Width = N(3); e.Height = N(4); e.Text = T(5); N(6); N(7); break;
                    case "button":
                        e.Kind = GumpElementKind.Button; e.Graphic = N(3); e.GraphicDown = N(4);
                        e.PageButton = N(5) == 0; e.TargetPage = N(6); e.ReplyId = N(7); e.Text = $"Button {e.ReplyId}"; break;
                    case "checkbox": case "radio":
                        e.Kind = p[0].Equals("radio", StringComparison.OrdinalIgnoreCase) ? GumpElementKind.Radio : GumpElementKind.CheckBox;
                        e.Graphic = N(3); e.GraphicDown = N(4); e.Checked = N(5) != 0; e.ReplyId = N(6); break;
                    case "textentry": case "textentrylimited":
                        e.Kind = GumpElementKind.TextEntry; e.Width = N(3); e.Height = N(4); e.Hue = N(5);
                        e.EntryId = N(6); e.Text = T(7); e.MaxLength = p.Length > 8 ? N(8) : 239; break;
                }
                if (e.Kind != GumpElementKind.Raw) { e.X = N(1); e.Y = N(2); }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                e = new GumpElement { Name = p[0], Kind = GumpElementKind.Raw, Raw = raw, Page = page, Group = group };
                doc.Notes.Add($"Preserved malformed command: {raw} ({ex.Message})");
            }
            if (e.Kind == GumpElementKind.Raw) doc.Notes.Add($"Not rendered; preserved: {raw}");
            doc.Elements.Add(e);
            doc.Width = Math.Clamp(Math.Max(doc.Width, e.X + e.Width), 16, 8192);
            doc.Height = Math.Clamp(Math.Max(doc.Height, e.Y + e.Height), 16, 8192);
        }
        if (!string.IsNullOrWhiteSpace(layout[end..])) throw new InvalidDataException("Unclosed or invalid layout command.");
        doc.Validate();
        return doc;
    }

    public static (string Layout, string[] Texts) Export(GumpDocument doc)
    {
        doc.Validate();
        var text = new List<string>(doc.Texts);
        int Text(string value) { text.Add(value); return text.Count - 1; }
        var layout = new StringBuilder();
        int page = -1, group = -1;
        foreach (var e in doc.Elements.Where(e => e.Visible))
        {
            if (e.Page != page) { page = e.Page; layout.Append($"{{ page {page} }}\n"); }
            if (e.Group != group) { group = e.Group; layout.Append($"{{ group {group} }}\n"); }
            string xy = $"{e.X} {e.Y}", wh = $"{e.Width} {e.Height}";
            string command = e.Kind switch
            {
                GumpElementKind.Image => $"gumppic {xy} {e.Graphic}",
                GumpElementKind.TiledImage => $"gumppictiled {xy} {wh} {e.Graphic}",
                GumpElementKind.Panel when e.Graphic > 0 => $"resizepic {xy} {e.Graphic} {wh}",
                GumpElementKind.Label => e.Raw.StartsWith("text ", StringComparison.OrdinalIgnoreCase)
                    ? $"text {xy} {e.Hue} {Text(e.Text)}" : $"croppedtext {xy} {wh} {e.Hue} {Text(e.Text)}",
                GumpElementKind.Html => $"htmlgump {xy} {wh} {Text(e.Text)} {HtmlFlags(e.Raw)}",
                GumpElementKind.Button => $"button {xy} {e.Graphic} {e.GraphicDown} {(e.PageButton ? 0 : 1)} {e.TargetPage} {e.ReplyId}",
                GumpElementKind.CheckBox or GumpElementKind.Radio => $"{(e.Kind == GumpElementKind.Radio ? "radio" : "checkbox")} {xy} {e.Graphic} {e.GraphicDown} {(e.Checked ? 1 : 0)} {e.ReplyId}",
                GumpElementKind.TextEntry => $"textentrylimited {xy} {wh} {e.Hue} {e.EntryId} {Text(e.Text)} {e.MaxLength}",
                GumpElementKind.Raw when !string.IsNullOrWhiteSpace(e.Raw) && !e.Raw.Contains('{') && !e.Raw.Contains('}') => e.Raw,
                _ => throw new InvalidDataException($"{e.Name}: {e.Kind} has no classic equivalent. Choose classic art or export the modern document.")
            };
            layout.Append("{ ").Append(command).Append(" }\n");
        }
        return (layout.ToString(), text.ToArray());
    }
    private static string HtmlFlags(string raw)
    {
        string[] p = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 8 ? $"{p[6]} {p[7]}" : "0 1";
    }
}
