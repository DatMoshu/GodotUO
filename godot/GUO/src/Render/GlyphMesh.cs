// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer;

/// <summary>
/// PORT DEVIATION (GUO), for speed alone: one text's glyph quads, recorded
/// once from the batcher's upright-rect path and kept as a mesh per texture
/// run, drawn at the text's origin. Upstream appends each glyph to
/// SpriteBatch's managed array every frame; Godot's canvas costs an engine
/// call per command, so a gump full of text cost one call per glyph.
/// The same quads, UVs and colours as the rect commands, so the picture is
/// meant to be identical. GUO_TEXT_MESHES=0 turns it off.
/// </summary>
internal sealed class GlyphMesh
{
    internal struct Run
    {
        public Texture2D Texture;
        public ArrayMesh Mesh;
        // Every quad's colour: vertex colours are 8-bit in a mesh, so the
        // encoded hue goes in the command's float modulate, as a rect has it.
        public Color Modulate;
    }

    internal readonly List<Run> Runs = new();
    internal int RunCount;
    internal bool Valid;
    // The recorded draw's inputs; any change re-records.
    internal object Info, Atlas;
    internal long Key0, Key1, Key2;

    private readonly List<Texture2D> _textures = new();
    private readonly List<Vector2> _points = new(), _uvs = new();
    private readonly List<Color> _colors = new();
    private bool _failed;

    internal void BeginRecord()
    {
        _textures.Clear();
        _points.Clear();
        _uvs.Clear();
        _colors.Clear();
        _failed = false;
        Valid = false;
    }

    internal void Fail() => _failed = true;

    internal void Add(Texture2D texture, float x0, float y0, float x1, float y1,
        float u0, float v0, float u1, float v1, Color color)
    {
        _textures.Add(texture);
        _points.Add(new Vector2(x0, y0)); _points.Add(new Vector2(x1, y0)); _points.Add(new Vector2(x0, y1));
        _points.Add(new Vector2(x1, y0)); _points.Add(new Vector2(x1, y1)); _points.Add(new Vector2(x0, y1));
        _uvs.Add(new Vector2(u0, v0)); _uvs.Add(new Vector2(u1, v0)); _uvs.Add(new Vector2(u0, v1));
        _uvs.Add(new Vector2(u1, v0)); _uvs.Add(new Vector2(u1, v1)); _uvs.Add(new Vector2(u0, v1));
        for (int i = 0; i < 6; i++) _colors.Add(color);
    }

    internal void EndRecord()
    {
        RunCount = 0;
        if (_failed || _textures.Count == 0) return;
        int start = 0;
        for (int q = 1; q <= _textures.Count; q++)
        {
            if (q < _textures.Count && ReferenceEquals(_textures[q], _textures[start])) continue;
            for (int c = start * 6; c < q * 6; c++)
                if (_colors[c] != _colors[start * 6]) { RunCount = 0; return; }
            Build(_textures[start], start, q - start);
            start = q;
        }
        Valid = true;
    }

    private void Build(Texture2D texture, int quad, int quads)
    {
        if (RunCount == Runs.Count) Runs.Add(new Run { Mesh = new ArrayMesh() });
        Run run = Runs[RunCount];
        run.Texture = texture;
        run.Modulate = _colors[quad * 6];
        int v = quad * 6, n = quads * 6;
        var points = _points.GetRange(v, n).ToArray();
        var uvs = _uvs.GetRange(v, n).ToArray();
        var colors = new Color[n];
        System.Array.Fill(colors, Colors.White);
        // The sprite shaders take CUSTOM0 on a mesh; zeros, as a rect has none.
        var custom = new float[n * 4];
        run.Mesh.ClearSurfaces();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        run.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
            (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        Runs[RunCount++] = run;
    }
}
