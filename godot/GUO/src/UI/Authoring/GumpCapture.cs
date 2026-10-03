// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using Classic = GUO.Game.UI.Controls;

namespace GUO.UI.Authoring;

/// <summary>Opt-in snapshots of live client gumps. Existing instances keep their original behavior.</summary>
internal static class GumpCapture
{
    public static string DirectoryPath => ProjectSettings.GlobalizePath("user://gump_studio");
    public static void SaveOpen(World world)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            int count = 0;
            foreach (var gump in UIManager.Gumps.Where(g => !g.IsDisposed && g.IsVisible))
            {
                var doc = Capture(gump);
                string path = Path.Combine(DirectoryPath, $"{gump.GetType().Name}_{count++}.gump.json");
                File.WriteAllText(path, doc.ToJson());
            }
            GameActions.Print(world, $"Captured {count} gumps into {DirectoryPath}");
        }
        catch (Exception ex) { GameActions.Print(world, "Gump capture: " + ex.Message); }
    }

    internal static GumpDocument Capture(Gump gump)
    {
        var doc = new GumpDocument
        {
            Name = gump.GetType().Name, SourceGump = gump.GetType().FullName,
            SourceLocalSerial = gump.LocalSerial, SourceServerSerial = gump.ServerSerial,
            Width = Math.Clamp(gump.Width, 16, 8192), Height = Math.Clamp(gump.Height, 16, 8192)
        };
        doc.Notes.Add("Visual snapshot. Custom drawing retains selectable bounds. Original client logic is not serialized.");
        doc.Notes.Add("gumpapply applies geometry to a matching open instance; save under user://gump_studio. Text entry contents are deliberately not captured.");
        void Walk(Classic.Control parent, string prefix, int x, int y, int inheritedPage)
        {
            for (int i = 0; i < parent.Children.Count; i++)
            {
                var c = parent.Children[i];
                if (c.IsDisposed) continue;
                string path = prefix.Length == 0 ? i.ToString() : prefix + "/" + i;
                int page = c.Page == 0 ? inheritedPage : c.Page;
                var e = new GumpElement
                {
                    Name = c.GetType().Name, SourceType = c.GetType().FullName, SourcePath = path,
                    X = x + c.X + c.Offset.X, Y = y + c.Y + c.Offset.Y,
                    Width = Math.Clamp(c.Width, 1, 8192), Height = Math.Clamp(c.Height, 1, 8192),
                    Page = Math.Clamp(page, 0, 65535), Visible = c.IsVisible, Kind = GumpElementKind.Raw,
                    Graphic = ReadInt(c, "Graphic"), Hue = ReadInt(c, "Hue"),
                    Text = c is Classic.StbTextBox ? "" : ReadText(c)
                };
                switch (c)
                {
                    case Classic.ResizePic: e.Kind = GumpElementKind.Panel; break;
                    case Classic.GumpPic: e.Kind = GumpElementKind.Image; break;
                    case Classic.GumpPicTiled: e.Kind = GumpElementKind.TiledImage; break;
                    case Classic.Label:
                        e.Kind = GumpElementKind.Label; e.FontSize = Math.Clamp(c.Height - 2, 8, 18);
                        e.Color = e.Hue == 0xFFFF ? "eeeade" : "1c1812"; break;
                    case Classic.HtmlControl: e.Kind = GumpElementKind.Html; break;
                    case Classic.Button b:
                        e.Kind = GumpElementKind.Button; e.ReplyId = b.ButtonID; e.TargetPage = b.ToPage;
                        e.PageButton = b.ButtonAction == Classic.ButtonAction.SwitchPage;
                        e.Graphic = ReadInt(c, "_normal"); e.GraphicDown = ReadInt(c, "_pressed"); break;
                    case Classic.Checkbox check:
                        e.Kind = c.GetType().Name.Contains("Radio", StringComparison.Ordinal) ? GumpElementKind.Radio : GumpElementKind.CheckBox;
                        e.Checked = check.IsChecked; e.ReplyId = (int)Math.Min(c.LocalSerial, int.MaxValue);
                        e.Group = Math.Clamp(ReadInt(c, "GroupIndex"), 0, 65535);
                        e.Graphic = ReadInt(c, "_inactive"); e.GraphicDown = ReadInt(c, "_active"); break;
                    case Classic.StbTextBox:
                        e.Kind = GumpElementKind.TextEntry; e.EntryId = (int)Math.Min(c.LocalSerial, ushort.MaxValue); break;
                }
                if (e.Kind == GumpElementKind.Raw) doc.Notes.Add($"Custom control: {e.Name} at {path}; geometry can still be edited.");
                doc.Elements.Add(e);
                Walk(c, path, e.X, e.Y, page);
            }
        }
        Walk(gump, "", 0, 0, 0);
        doc.Validate();
        return doc;
    }
    private static int ReadInt(object source, string member)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try { return Convert.ToInt32(source.GetType().GetProperty(member, flags)?.GetValue(source) ?? source.GetType().GetField(member, flags)?.GetValue(source) ?? 0); }
        catch { return 0; }
    }
    private static string ReadText(object source)
    {
        try { return source.GetType().GetProperty("Text")?.GetValue(source) as string ?? ""; }
        catch { return ""; }
    }

    public static void ApplyFile(World world, string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0].Equals("gumpapply", StringComparison.OrdinalIgnoreCase)) args = args[1..];
            if (args.Length == 0) throw new InvalidDataException("Usage: gumpapply filename.gump.json (from user://gump_studio)");
            string name = args[0];
            if (name != Path.GetFileName(name)) throw new InvalidDataException("Use a filename within user://gump_studio.");
            var doc = GumpDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, name)));
            var targets = UIManager.Gumps.Where(g => !g.IsDisposed && g.GetType().FullName == doc.SourceGump
                && g.LocalSerial == doc.SourceLocalSerial && g.ServerSerial == doc.SourceServerSerial).ToArray();
            if (targets.Length != 1) throw new InvalidDataException("Keep exactly one matching original gump open before applying this capture.");
            var target = targets[0];
            Apply(doc, target);
            GameActions.Print(world, $"Applied {doc.Name} to the open gump. Reopen it to restore the original layout.");
        }
        catch (Exception ex) { GameActions.Print(world, "Gump apply: " + ex.Message); }
    }

    internal static void Apply(GumpDocument doc, Gump target)
    {
        doc.Validate();
        if (target.GetType().FullName != doc.SourceGump || target.LocalSerial != doc.SourceLocalSerial || target.ServerSerial != doc.SourceServerSerial)
            throw new InvalidDataException("Snapshot belongs to another gump instance.");
        var map = new Dictionary<string, Classic.Control>();
        void Walk(Classic.Control p, string prefix)
        {
            for (int i = 0; i < p.Children.Count; i++)
            {
                string key = prefix.Length == 0 ? i.ToString() : prefix + "/" + i;
                map[key] = p.Children[i]; Walk(p.Children[i], key);
            }
        }
        Walk(target, "");
        if (doc.Elements.Select(e => e.SourcePath).Distinct().Count() != doc.Elements.Count)
            throw new InvalidDataException("Duplicate capture paths: apply geometry from an unduplicated capture.");
        // Validate all paths before modifying anything: a changed live hierarchy is not a match.
        foreach (var e in doc.Elements)
            if (e.SourcePath.Length == 0 || !map.TryGetValue(e.SourcePath, out var c) || c.IsDisposed || c.GetType().FullName != e.SourceType)
                throw new InvalidDataException($"Control hierarchy changed at {e.Name}. Capture this gump again.");
        var positions = doc.Elements.ToDictionary(e => e.SourcePath);
        foreach (var e in doc.Elements)
        {
            var c = map[e.SourcePath];
            int slash = e.SourcePath.LastIndexOf('/');
            string parent = slash < 0 ? "" : e.SourcePath[..slash];
            int px = positions.TryGetValue(parent, out var pe) ? pe.X : 0, py = pe?.Y ?? 0;
            c.X = e.X - px - c.Offset.X; c.Y = e.Y - py - c.Offset.Y;
            c.Width = e.Width; c.Height = e.Height; c.IsVisible = e.Visible;
            // Never set text, switch state, pages or callbacks here: those can execute game logic.
        }
    }
}
