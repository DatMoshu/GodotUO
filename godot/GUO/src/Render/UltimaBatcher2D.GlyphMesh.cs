// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using System;
using GUO.Compat;
using Color = Godot.Color;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    // PORT DEVIATION (GUO): text drawn as recorded meshes (GlyphMesh).
    internal static bool TextMeshes = System.Environment.GetEnvironmentVariable("GUO_TEXT_MESHES") != "0";
    internal static long TextMeshDraws, TextMeshRecords;
    private GlyphMesh _textRecord;

    /// <summary>True when a text may be drawn from, or recorded into, a GlyphMesh now.</summary>
    internal bool CanDrawTextMesh => TextMeshes && _textRecord == null && _capture == null && !_mirroring
        && !OrderedWorldRecording
        ;

    internal void BeginTextRecord(GlyphMesh mesh)
    {
        EnsureStarted();
        _textRecord = mesh;
        mesh.BeginRecord();
    }

    internal void EndTextRecord()
    {
        _textRecord.EndRecord();
        _textRecord = null;
        TextMeshRecords++;
    }

    /// <summary>While recording, takes the sprite instead of the canvas; anything but an upright rect fails the record.</summary>
    private bool RecordText(Texture2D texture, float destinationX, float destinationY, float destinationW, float destinationH,
        float sourceX, float sourceY, float sourceW, float sourceH, float originX, float originY,
        float rotationSin, float rotationCos, byte effects, Vector3 color)
    {
        if (_textRecord == null) return false;
        if (rotationSin != 0f || rotationCos != 1f || effects != 0)
        {
            _textRecord.Fail();
            return true;
        }
        float x0 = destinationX - originX * destinationW, y0 = destinationY - originY * destinationH;
        _textRecord.Add(texture, x0, y0, x0 + destinationW, y0 + destinationH,
            sourceX, sourceY, sourceX + sourceW, sourceY + sourceH, Encode(color));
        return true;
    }

    private readonly System.Collections.Generic.Dictionary<(Texture2D, Rectangle, Rectangle, Vector3), (GlyphMesh Mesh, long Frame)> _tiled = new();
    private readonly System.Collections.Generic.List<(Texture2D, Rectangle, Rectangle, Vector3)> _tiledStale = new();

    /// <summary>DrawTiled's tiles as one recorded mesh, kept while the same tiling is drawn.</summary>
    private bool DrawTiledMesh(Texture2D texture, Rectangle destination, Rectangle source, Vector3 hue)
    {
        if (texture == null || !CanDrawTextMesh || source.Width <= 0 || source.Height <= 0) return false;
        var key = (texture, destination, source, hue);
        if (!_tiled.TryGetValue(key, out var entry))
        {
            if (_tiled.Count >= 256) DropStaleTiles(0);
            entry.Mesh = new GlyphMesh();
            BeginTextRecord(entry.Mesh);
            try { DrawTiled(texture, destination, source, hue, 0f); }
            finally { EndTextRecord(); }
        }
        entry.Frame = FramesBegun;
        _tiled[key] = entry;
        if ((FramesBegun & 127) == 0) DropStaleTiles(120);
        if (!entry.Mesh.Valid) return false;
        DrawTextMesh(entry.Mesh, 0, 0);
        return true;
    }

    private void DropStaleTiles(long age)
    {
        _tiledStale.Clear();
        foreach (var pair in _tiled)
            if (FramesBegun - pair.Value.Frame >= age) _tiledStale.Add(pair.Key);
        foreach (var key in _tiledStale) _tiled.Remove(key);
    }

    private sealed class StringMesh
    {
        public SpriteFont Font;
        public Vector3 Color;
        public char[] Text = new char[64];
        public int Length;
        public GlyphMesh Mesh;
        public long Frame;
    }

    private readonly System.Collections.Generic.List<StringMesh> _strings = new();

    /// <summary>
    /// DrawString's glyphs as one recorded mesh, kept while the same text is
    /// drawn (a debug panel redraws an unchanged text every frame). Recorded
    /// at the origin and drawn at a whole-pixel position, as RenderedText is.
    /// </summary>
    private bool DrawStringMesh(SpriteFont font, System.ReadOnlySpan<char> text, Vector2 position, Vector3 color)
    {
        if (!CanDrawTextMesh || position.X != (int)position.X || position.Y != (int)position.Y) return false;
        StringMesh entry = null, oldest = null;
        foreach (var s in _strings)
        {
            if (s.Font == font && s.Color == color && text.SequenceEqual(s.Text.AsSpan(0, s.Length))) { entry = s; break; }
            if (oldest == null || s.Frame < oldest.Frame) oldest = s;
        }
        if (entry == null)
        {
            if (_strings.Count < 32) _strings.Add(entry = new StringMesh { Mesh = new GlyphMesh() });
            else entry = oldest;
            entry.Font = font; entry.Color = color; entry.Length = text.Length;
            if (entry.Text.Length < text.Length) entry.Text = new char[System.Math.Max(text.Length, entry.Text.Length * 2)];
            text.CopyTo(entry.Text);
            BeginTextRecord(entry.Mesh);
            try { DrawString(font, text, Vector2.Zero, color, 0f); }
            finally { EndTextRecord(); }
        }
        entry.Frame = FramesBegun;
        if (!entry.Mesh.Valid) return false;
        DrawTextMesh(entry.Mesh, (int)position.X, (int)position.Y);
        return true;
    }

    /// <summary>Draws a recorded text at (x, y): one mesh command per texture run.</summary>
    internal void DrawTextMesh(GlyphMesh mesh, int x, int y)
    {
        EnsureStarted();
        var at = new Transform2D(0f, new Vector2(x, y));
        for (int i = 0; i < mesh.RunCount; i++)
        {
            Texture2D texture = mesh.Runs[i].Texture;
            SelectSpriteSampler(texture);
            EnsureSpriteMaterial(texture);
            FlushRun();
            Commands++;
            Count(1, RidOf(texture));
            CanvasMesh(_current, mesh.Runs[i].Mesh.GetRid(), at, mesh.Runs[i].Modulate, RidOf(texture));
            TextMeshDraws++;
        }
    }
}
