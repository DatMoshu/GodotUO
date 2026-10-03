// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.UI.Authoring;

/// <summary>Portable authored UI. Art stays in the local install; documents store IDs only.</summary>
public sealed class GumpDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled gump";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;
    public bool Modern { get; set; }
    public List<string> Texts { get; set; } = new();
    public List<GumpElement> Elements { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string SourceGump { get; set; } = "";
    public uint SourceLocalSerial { get; set; }
    public uint SourceServerSerial { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public GumpDocument Clone() => Parse(ToJson());
    public static GumpDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<GumpDocument>(json, Options)
            ?? throw new InvalidDataException("Empty gump document.");
        doc.Validate();
        return doc;
    }
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException($"Unsupported gump version {Version}.");
        if (Width < 16 || Height < 16 || Width > 8192 || Height > 8192)
            throw new InvalidDataException("Canvas must be between 16 and 8192 pixels.");
        if (Elements == null || Texts == null || Notes == null || Elements.Count > 10000)
            throw new InvalidDataException("Invalid or oversized gump document.");
        var ids = new HashSet<string>();
        foreach (var e in Elements)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Id) || !ids.Add(e.Id))
                throw new InvalidDataException("Every element needs a unique ID.");
            if (!Enum.IsDefined(e.Kind) || e.Text == null || e.Raw == null || e.Name == null || e.SourcePath == null || e.SourceType == null || e.Binding == null
                || e.Color == null || e.Background == null || e.Anchor is not ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight" or "Stretch")
                || e.Width < 1 || e.Height < 1 || e.Width > 8192 || e.Height > 8192
                || Math.Abs((long)e.X) > 32767 || Math.Abs((long)e.Y) > 32767
                || e.Page < 0 || e.Page > 65535 || e.TargetPage < 0 || e.TargetPage > 65535
                || e.Graphic < 0 || e.Graphic > 65535 || e.GraphicDown < 0 || e.GraphicDown > 65535
                || e.Hue < 0 || e.Hue > 65535 || e.ReplyId < 0 || e.EntryId < 0 || e.EntryId > 65535
                || e.Group < 0 || e.Group > 65535 || e.FontSize < 8 || e.FontSize > 128 || e.MaxLength < 0 || e.MaxLength > 65535)
                throw new InvalidDataException($"Invalid properties on {e.Name}.");
        }
    }
}

public enum GumpElementKind { Panel, Image, TiledImage, Label, Html, Button, CheckBox, Radio, TextEntry, Progress, Raw }

public sealed class GumpElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Element";
    public GumpElementKind Kind { get; set; }
    public int X { get; set; } = 24;
    public int Y { get; set; } = 24;
    public int Width { get; set; } = 160;
    public int Height { get; set; } = 40;
    public int Page { get; set; }
    public int Group { get; set; }
    public int Graphic { get; set; }
    public int GraphicDown { get; set; }
    public int Hue { get; set; }
    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 18;
    public string Color { get; set; } = "eeeade";
    public string Background { get; set; } = "242c3b";
    public int ReplyId { get; set; } = 1;
    public int EntryId { get; set; }
    public int TargetPage { get; set; }
    public bool PageButton { get; set; }
    public bool Checked { get; set; }
    public int MaxLength { get; set; } = 239;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public string Anchor { get; set; } = "TopLeft";
    public string Raw { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Binding { get; set; } = "";
}

/// <summary>Document snapshots make undo atomic across drag, import and inspector edits.</summary>
public sealed class GumpHistory
{
    private readonly List<string> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Push(GumpDocument doc)
    {
        _undo.Add(doc.ToJson());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }
    public GumpDocument Undo(GumpDocument doc) => Move(_undo, _redo, doc);
    public GumpDocument Redo(GumpDocument doc) => Move(_redo, _undo, doc);
    private static GumpDocument Move(List<string> from, List<string> to, GumpDocument current)
    {
        if (from.Count == 0) return current;
        to.Add(current.ToJson());
        var result = GumpDocument.Parse(from[^1]);
        from.RemoveAt(from.Count - 1);
        return result;
    }
}
