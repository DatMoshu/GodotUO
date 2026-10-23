// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using GUO.Renderer;

namespace GUO.Labs.GpuRenderer;

/// <summary>Capture once; construct canvas commands only when the GPU transaction cannot be used.</summary>
internal sealed class OrderedWorldCapture : IDisposable
{
    internal static bool Enabled = Array.IndexOf(OS.GetCmdlineUserArgs(), "--rd-world") >= 0;
    internal static long FramesDrawn, FramesFallback, UploadedBytes;
    internal static long DrawCalls, RecordTicks;
    internal static long SuppressedCommands;
    internal static string LastFallback;
    internal static bool LastFrameOnPass;
    internal static int InjectFailure; // Probe only: 1 before upload, 2 after recording.
    private readonly OrderedWorldDevice _device = new();
    private readonly OrderedWorldCanvas _fallback;
    private readonly List<OrderedWorldDevice.Vertex> _vertices = new(32768);
    private readonly List<OrderedWorldDevice.Segment> _segments = new(1024);
    private readonly List<OrderedWorldCanvas.Command> _commands = new(8192);
    private readonly Dictionary<Texture2D, OrderedWorldDevice.Textures> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Callable _render;
    private OrderedWorldDevice.Push _push;
    private Transform2D _view;
    private Transform2D _offsetTransform;
    private Vector2 _lastOffset;
    private bool _haveOffset;
    private Texture2D _hue, _light;
    private Rid _hueRid, _lightRid;
    private Rid _canvas, _presenter;
    private string _unsupported;
    private bool _queued, _disposed;
    private int _pending;
    private readonly string _vertexSource, _fragmentSource;
    internal bool Recording { get; private set; }
    internal bool Deferring => Recording && _unsupported == null;
    internal Texture2Drd Texture => _device.Presenter;

    internal OrderedWorldCapture(ShaderMaterial spriteMaterial, ShaderMaterial meshMaterial)
    {
        _fallback = new OrderedWorldCanvas(spriteMaterial, meshMaterial);
        _vertexSource = FileAccess.GetFileAsString("res://src/Labs/GpuRenderer/ordered_world_vertex.glsl.txt");
        _fragmentSource = FileAccess.GetFileAsString("res://src/Labs/GpuRenderer/ordered_world_fragment.glsl.txt");
        _render = Callable.From(RenderThread);
        RenderingServer.FramePreDraw += BeforeDraw;
    }

    internal bool Begin(Vector2I size, Transform2D transform, Texture2D hue, Texture2D light,
        float brightness, Vector2 circle, float radius)
    {
        LastFrameOnPass = false;
        if (_disposed || Volatile.Read(ref _pending) != 0 || _queued || Recording)
        {
            Fallback("previous frame ticket still pending or capture disposed");
            return false;
        }
        if (RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility")
        {
            Fallback("RenderingDevice unavailable on Compatibility");
            return false;
        }
        if (hue == null) { Fallback("missing hue table"); return false; }
        _vertices.Clear(); _segments.Clear(); _textures.Clear(); _commands.Clear();
        _canvas = default;
        _hue = hue; _light = light ?? hue;
        _hueRid = _hue?.GetRid() ?? default;
        _lightRid = _light?.GetRid() ?? default;
        _unsupported = null;
        _view = transform;
        _haveOffset = false;
        _push = new OrderedWorldDevice.Push
        {
            Row0 = new Vector4(transform.X.X, transform.Y.X, transform.Origin.X, brightness),
            Row1 = new Vector4(transform.X.Y, transform.Y.Y, transform.Origin.Y, radius),
            Screen = new Vector4(size.X, size.Y, circle.X, circle.Y)
        };
        Recording = true;
        return true;
    }

    internal void Reject(string reason)
    {
        if (!Recording || _unsupported != null) return;
        _unsupported = reason;
        // The triggering operation has not emitted canvas work yet. This prefix
        // uses its own arrays and does not overwrite the batcher's current quad.
        _fallback.Render(_canvas, CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_commands));
        Fallback(reason);
    }

    internal bool Quad(Texture2D texture, in MeshQuad q, Vector2 offset, bool meshColor, bool rectangle = false,
        Transform2D? local = null, Rect2 destination = default, Rect2 source = default)
    {
        if (!Recording || _unsupported != null) return false;
        if (texture == null) { Reject("missing texture"); return false; }
        if (!SupportedMode(q.Hue0) || !SupportedMode(q.Hue1) || !SupportedMode(q.Hue2) || !SupportedMode(q.Hue3))
        { Reject("unsupported shader mode"); return false; }
        if (!_textures.TryGetValue(texture, out var textures))
        {
            if (GUO.Renderer.Arts.Art.IsHdTexture(texture)) { Reject("HD texture"); return false; }
            textures = new OrderedWorldDevice.Textures(texture.GetRid(), _hueRid, _lightRid);
            _textures.Add(texture, textures); // Strong owner reference held until the ticket retires.
        }
        uint first = (uint)(_vertices.Count / 4 * 6);
        // Almost every quad in a world frame shares this transform. The sampled
        // prototype spent substantial CPU time in Godot's matrix operators.
        if (!_haveOffset || _lastOffset != offset)
        {
            _offsetTransform = _view * new Transform2D(0, offset);
            _lastOffset = offset;
            _haveOffset = true;
        }
        Transform2D transform = _offsetTransform;
        if (local.HasValue) transform *= local.Value;
        _commands.Add(new OrderedWorldCanvas.Command
        {
            Type = meshColor ? OrderedWorldCanvas.Kind.Mesh : rectangle ? OrderedWorldCanvas.Kind.Rect : OrderedWorldCanvas.Kind.Triangles,
            FirstCorner = _vertices.Count, Texture = textures.ArtRs, Offset = offset,
            Local = local ?? Transform2D.Identity, Destination = destination, Source = source,
            Color0 = meshColor ? UltimaBatcher2D.Encode(q.Hue0) : default,
            Color1 = meshColor ? UltimaBatcher2D.Encode(q.Hue1) : default,
            Color2 = meshColor ? UltimaBatcher2D.Encode(q.Hue2) : default,
            Color3 = meshColor ? UltimaBatcher2D.Encode(q.Hue3) : default,
        });
        offset = Vector2.Zero; // Match the canvas's composed model transform, not CPU-translated vertices.
        if (_segments.Count != 0 && _segments[^1].Textures == textures && _segments[^1].Transform == transform)
        {
            var last = _segments[^1];
            _segments[^1] = last with { VertexCount = last.VertexCount + 6 };
        }
        else _segments.Add(new OrderedWorldDevice.Segment(first, 6, textures, transform));
        // Four corners per quad; the GPU emits the six triangle vertices in the correct order.
        Vertex(q.Position0, q.TextureCoordinate0, q.Hue0, q.Normal0, offset, meshColor, rectangle);
        Vertex(q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1, offset, meshColor, rectangle);
        Vertex(q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2, offset, meshColor, rectangle);
        Vertex(q.Position3, q.TextureCoordinate3, q.Hue3, q.Normal3, offset, meshColor, rectangle);
        SuppressedCommands++;
        return true;
    }

    private static bool SupportedMode(Vector3 hue)
    {
        int mode = ((int)hue.Y) & 0x3f;
        return mode <= 10 || mode is >= 20 and <= 30;
    }

    private void Vertex(Vector2 p, Vector2 uv, Vector3 hue, Vector3 normal, Vector2 offset, bool meshColor, bool rectangle)
    {
        Color c = UltimaBatcher2D.Encode(hue);
        // ArrayMesh colors travel through UNORM8; rect/triangle canvas colors are floats.
        if (meshColor) c = new Color(ByteColor(c.R), ByteColor(c.G), ByteColor(c.B), ByteColor(c.A));
        p += offset;
        _vertices.Add(new OrderedWorldDevice.Vertex
        {
            PositionUv = new Vector4(p.X, p.Y, uv.X, uv.Y),
            PackedHue = new Vector4(c.R, c.G, c.B, c.A),
            Normal = new Vector4(normal.X, normal.Y, normal.Z, rectangle ? 1 : 0)
        });
    }

    // Godot's ArrayMesh packer truncates channel*255; Color.ToRgba32 rounds instead.
    private static float ByteColor(float value) => Math.Clamp((int)((double)value * 255.0), 0, 255) / 255f;

    internal void End(Rid canvas, Rid presenter)
    {
        Recording = false;
        _canvas = canvas; _presenter = presenter;
        _queued = _unsupported == null;
    }

    internal void SetCanvas(Rid canvas) => _canvas = canvas;
    internal void ClearCanvas() => _fallback.Clear();

    internal void CancelUnsubmittedFrame()
    {
        // DrawFrame can run again without a native FramePreDraw in between.
        // Retire that ticket before its canvas items return to the batcher's pool.
        // A submitted RT callback has _queued=false: leave its lists and owners intact.
        _queued = false;
    }

    private void BeforeDraw()
    {
        if (!_queued || _disposed) return;
        _queued = false;
        Volatile.Write(ref _pending, 1);
        RenderingServer.CallOnRenderThread(_render);
    }

    private void RenderThread()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool success = false;
        try
        {
            if (_unsupported != null) throw new NotSupportedException(_unsupported);
            var size = new Vector2I((int)_push.Screen.X, (int)_push.Screen.Y);
            if (_device.Size != size)
            {
                _device.DisposeRT(false);
                _device.InitializeRT(size, _vertexSource, _fragmentSource);
            }
            if (InjectFailure == 1) throw new InvalidOperationException("injected before upload");
            _device.RenderRT(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_segments), _push, true);
            UploadedBytes += _vertices.Count * 48L;
            DrawCalls += _segments.Count;
            if (InjectFailure == 2) throw new InvalidOperationException("injected after recording");
            FramesDrawn++;
            success = true;
        }
        catch (Exception e)
        {
            Fallback(e.Message);
            _fallback.Render(_canvas, CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_commands));
        }
        finally
        {
            // These branches contain this frame only. GameObject.Draw is never replayed.
            RenderingServer.CanvasItemSetVisible(_canvas, !success);
            RenderingServer.CanvasItemSetVisible(_presenter, success);
            LastFrameOnPass = success;
            RecordTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Volatile.Write(ref _pending, 0);
        }
    }

    internal static void Fallback(string reason)
    {
        LastFrameOnPass = false;
        FramesFallback++;
        if (LastFallback != reason) GD.Print("[rd-world] canvas fallback: " + reason);
        LastFallback = reason;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RenderingServer.FramePreDraw -= BeforeDraw;
        // Queued after outstanding callbacks; their immutable lists remain alive until then.
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _device.DisposeRT();
            _device.Presenter.Dispose();
            _fallback.Dispose();
        }));
    }
}
#endif
