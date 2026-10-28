// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

namespace GUO.Renderer.DepthWorld;

/// <summary>Cold-path replay of captured drawing, never gameplay or atlas traversal.</summary>
internal sealed class OrderedWorldCanvas : IDisposable
{
    internal enum Kind { Rect, Triangles, Mesh }
    internal struct Command
    {
        internal Kind Type;
        internal int Group;
        internal int FirstCorner;
        internal Rid Texture;
        internal Vector2 Offset;
        internal Transform2D Local;
        internal Rect2 Destination, Source;
        // Preserve pre-UNORM8 mesh colors: repacking already normalized bytes can truncate twice.
        internal Color Color0, Color1, Color2, Color3;
    }

    private readonly ShaderMaterial _spriteMaterial, _meshMaterial; // Borrowed, strong owners.
    private readonly List<Rid> _items = new();
    private readonly List<ArrayMesh> _meshes = new();
    private readonly Vector2[] _points = new Vector2[4], _uv = new Vector2[4];
    private readonly Color[] _colors = new Color[4];
    private readonly float[] _normals = new float[16];
    private static readonly int[] Indices = { 0, 1, 2, 1, 3, 2 };
    private readonly Dictionary<int, Rid> _groups = new();
    private readonly List<Rid> _groupItems = new();
    private Rid _root;
    private bool _visible;
    private int _activeItems;
    internal static long CommandsDrawn;

    internal OrderedWorldCanvas(ShaderMaterial spriteMaterial, ShaderMaterial meshMaterial)
    {
        _spriteMaterial = spriteMaterial;
        _meshMaterial = meshMaterial;
    }

    internal void Clear()
    {
        // Clearing a pooled parent does NOT clear its private children. Hide before reuse.
        if (_visible) RenderingServer.CanvasItemSetVisible(_root, false);
        _visible = false;
        for (int i = 0; i < _activeItems; i++) RenderingServer.CanvasItemClear(_items[i]);
        _activeItems = 0;
        foreach (Rid group in _groupItems) RenderingServer.FreeRid(group);
        _groupItems.Clear();
        _groups.Clear();
        foreach (ArrayMesh mesh in _meshes) mesh.Dispose();
        _meshes.Clear();
        // Keep a small cold-path pool, not every item from the largest failed world.
        while (_items.Count > 1024)
        {
            RenderingServer.FreeRid(_items[^1]);
            _items.RemoveAt(_items.Count - 1);
        }
    }

    internal void Render(Rid parent, ReadOnlySpan<OrderedWorldResidency.Occurrence> occurrences,
        ReadOnlySpan<OrderedWorldResidency.Quad> dynamic, ReadOnlySpan<Command> commands)
    {
        Begin(parent);
        for (int i = 0; i < commands.Length; i++)
        {
            ref readonly Command command = ref commands[i];
            Append(occurrences[command.FirstCorner / 4].Get(dynamic), command);
        }
    }

    internal void Begin(Rid parent)
    {
        if (!RenderingServer.IsOnRenderThread()) throw new InvalidOperationException("Canvas replay requires the rendering thread.");
        Clear();
        if (!_root.IsValid)
        {
            _root = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetDrawIndex(_root, -1); // Captured prefix precedes the ordinary suffix.
            RenderingServer.CanvasItemSetDefaultTextureFilter(_root, RenderingServer.CanvasItemTextureFilter.Nearest);
        }
        RenderingServer.CanvasItemSetParent(_root, parent);
        RenderingServer.CanvasItemSetVisible(_root, true);
        _visible = true;
    }

    internal Rid Group(int id, int order)
    {
        if (_groups.TryGetValue(id, out Rid group)) return group;
        group = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(group, _root);
        RenderingServer.CanvasItemSetDrawIndex(group, order);
        RenderingServer.CanvasItemSetDefaultTextureFilter(group, RenderingServer.CanvasItemTextureFilter.Nearest);
        _groups.Add(id, group);
        _groupItems.Add(group);
        return group;
    }

    internal Rid RawGroup(int id)
    {
        Rid raw = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(raw, Group(id, id));
        RenderingServer.CanvasItemSetDrawIndex(raw, int.MaxValue);
        RenderingServer.CanvasItemSetDefaultTextureFilter(raw, RenderingServer.CanvasItemTextureFilter.Nearest);
        _groupItems.Add(raw);
        return raw;
    }

    internal void Append(in OrderedWorldResidency.Quad quad, in Command command, int? group = null)
    {
        int i = _activeItems;
            // A read-only view of this packet; no expanded frame buffer.
            ReadOnlySpan<OrderedWorldDevice.Vertex> vertices = MemoryMarshal.Cast<OrderedWorldResidency.Quad, OrderedWorldDevice.Vertex>(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in quad), 1));
            Rid item;
            if (i < _items.Count) item = _items[i];
            else
            {
                item = RenderingServer.CanvasItemCreate();
                // Raw RS items start with their own linear filter, even under a nearest parent.
                RenderingServer.CanvasItemSetDefaultTextureFilter(item, RenderingServer.CanvasItemTextureFilter.Nearest);
                RenderingServer.CanvasItemSetDrawIndex(item, i);
                _items.Add(item);
            }
            RenderingServer.CanvasItemSetParent(item, group.HasValue ? Group(group.Value, group.Value) : _root);
            RenderingServer.CanvasItemSetDrawIndex(item, i);
            _activeItems = i + 1;
            RenderingServer.CanvasItemSetTransform(item, new Transform2D(0, command.Offset));
            RenderingServer.CanvasItemSetMaterial(item,
                (command.Type == Kind.Mesh ? _meshMaterial : _spriteMaterial).GetRid());
            if (command.Type == Kind.Rect)
            {
                // Match the original item-offset / command-local transform composition exactly.
                RenderingServer.CanvasItemAddSetTransform(item, command.Local);
                ref readonly var v = ref vertices[0];
                var color = new Color(v.PackedHue.X, v.PackedHue.Y, v.PackedHue.Z, v.PackedHue.W);
                RenderingServer.CanvasItemAddTextureRectRegion(item, command.Destination, command.Texture, command.Source, color, false, false);
            }
            else
            {
                for (int c = 0; c < 4; c++)
                {
                    ref readonly var v = ref vertices[c];
                    _points[c] = new Vector2(v.PositionUv.X, v.PositionUv.Y);
                    _uv[c] = new Vector2(v.PositionUv.Z, v.PositionUv.W);
                    _colors[c] = new Color(v.PackedHue.X, v.PackedHue.Y, v.PackedHue.Z, v.PackedHue.W);
                    _normals[c * 4] = v.Normal.X; _normals[c * 4 + 1] = v.Normal.Y;
                    _normals[c * 4 + 2] = v.Normal.Z; _normals[c * 4 + 3] = 0;
                }
                if (command.Type == Kind.Mesh)
                {
                    _colors[0] = command.Color0; _colors[1] = command.Color1;
                    _colors[2] = command.Color2; _colors[3] = command.Color3;
                    using var arrays = new Godot.Collections.Array();
                    arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = _points;
                    arrays[(int)Mesh.ArrayType.TexUV] = _uv;
                    arrays[(int)Mesh.ArrayType.Color] = _colors;
                    arrays[(int)Mesh.ArrayType.Custom0] = _normals;
                    arrays[(int)Mesh.ArrayType.Index] = Indices;
                    var mesh = new ArrayMesh();
                    _meshes.Add(mesh);
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                        (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
                    RenderingServer.CanvasItemAddMesh(item, mesh.GetRid(), Transform2D.Identity, Colors.White, command.Texture);
                }
                else RenderingServer.CanvasItemAddTriangleArray(item, Indices, _points, _colors, _uv, null, null, command.Texture);
            }
            CommandsDrawn++;
    }

    public void Dispose()
    {
        Clear();
        foreach (Rid item in _items) RenderingServer.FreeRid(item);
        _items.Clear();
        if (_root.IsValid) RenderingServer.FreeRid(_root);
        _root = default;
        // Materials and texture RIDs remain owned by the original batcher / capture ticket.
    }
}
