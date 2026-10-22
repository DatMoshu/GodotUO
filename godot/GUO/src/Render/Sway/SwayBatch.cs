// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Renderer.Sway;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    internal SwayInstance StaticSway;
    private readonly ShaderMaterial _swayMaterial;
    private readonly record struct SwayKey(Vector2 Uv, Vector2 Extent, Vector2 Size, byte Kind, byte Phase);
    private sealed class SwayMesh { internal ArrayMesh Mesh; internal int Frame; }
    private readonly Dictionary<SwayKey, SwayMesh> _swayMeshes = new();
    private readonly List<SwayKey> _swayExpired = new();
    private static readonly float[] SwayHeights = { 0, 0.25f, 0.5f, 0.7f, 1 };
    private int _swayFrame;
    private int _swayUniformFrame = -1;
    internal int SwayDraws { get; private set; }
    internal int SwayCacheCount => _swayMeshes.Count;

    private void NextSwayFrame()
    {
        StaticSway = default;
        SwayDraws = 0;
        _swayFrame++;
        // Dispose only after the previous frame's canvas commands are cleared.
        if (_swayMeshes.Count < 2048) return;
        _swayExpired.Clear();
        foreach (var pair in _swayMeshes)
            if (pair.Value.Frame < _swayFrame - 1) _swayExpired.Add(pair.Key);
        foreach (var key in _swayExpired) { _swayMeshes[key].Mesh.Dispose(); _swayMeshes.Remove(key); }
    }

    private void DisposeSway()
    {
        foreach (var entry in _swayMeshes.Values) entry.Mesh.Dispose();
        _swayMeshes.Clear();
        _swayMaterial.Dispose();
    }

    private bool DrawSway(Texture2D texture, Vector2 uv, Vector2 extent, Vector2 position,
        Vector2 size, Vector3 hue, SwayInstance sway)
    {
        var key = new SwayKey(uv, extent, size, sway.Kind, sway.Phase);
        if (!_swayMeshes.TryGetValue(key, out var cached))
        {
            // Pathological custom art catalogues degrade to still art, not unbounded resources.
            if (_swayMeshes.Count >= 4096) return false;
            cached = new SwayMesh { Mesh = MakeSwayMesh(uv, extent, size, sway.Kind, sway.Phase) };
            _swayMeshes.Add(key, cached);
        }
        cached.Frame = _swayFrame;
        if (_swayUniformFrame != _swayFrame)
        {
            _swayUniformFrame = _swayFrame;
            _swayMaterial.SetShaderParameter("sway_wind", sway.Wind);
            _swayMaterial.SetShaderParameter("sway_time", GUO.Time.Ticks / 1000f);
        }
        EnsureMaterial(_swayMaterial);
        FlushRun();
        Commands++;
        Count(2, RidOf(texture));
        RenderingServer.CanvasItemAddMesh(_current, cached.Mesh.GetRid(),
            new Transform2D(0, position), Encode(hue), RidOf(texture));
        // Preserve the original selection/id silhouette, like upstream picking.
        MirrorRect(new Rect2(position, size), texture,
            new Rect2(uv * new Vector2(WidthOf(texture), HeightOf(texture)),
                extent * new Vector2(WidthOf(texture), HeightOf(texture))));
        SwayDraws++;
        return true;
    }

    internal static ArrayMesh MakeSwayMesh(Vector2 uv, Vector2 extent, Vector2 size, byte kind, byte phase)
    {
        const int strips = 4;
        const int count = strips * 6;
        var points = new Vector2[count];
        var uvs = new Vector2[count];
        var custom = new float[count * 4];
        int at = 0;
        for (int row = 0; row < strips; row++)
        {
            float top = SwayHeights[row], bottom = SwayHeights[row + 1];
            Write(0, top); Write(1, top); Write(0, bottom);
            Write(1, top); Write(1, bottom); Write(0, bottom);
        }
        void Write(float x, float y)
        {
            points[at] = new Vector2(x, y) * size;
            uvs[at] = uv + new Vector2(x, y) * extent;
            custom[at * 4] = SwayRender.Weight(y, kind);
            custom[at * 4 + 1] = phase * (MathF.Tau / 32);
            custom[at * 4 + 2] = SwayRender.Pixels(kind);
            at++;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
            (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        return mesh;
    }
}
