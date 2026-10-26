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
    internal static bool Enabled = Array.IndexOf(OS.GetCmdlineUserArgs(), "--rd-world") >= 0 || DepthWorldProbe.Enabled;
    internal static long FramesDrawn, FramesFallback, UploadedBytes;
    internal static long DrawCalls, RecordTicks;
    internal static long SuppressedCommands;
    internal static long ReferencedQuads, DynamicCaptureBytes, ReplayedQuads;
    internal static string LastFallback;
    internal static bool LastFrameOnPass;
    internal static int InjectFailure; // Probe only: 1 before upload, 2 after recording, 3/4 geometry/order, 5/6 before/after state upload.
    internal static bool InjectStateIndexFault; // Probe-only oracle negative control; no runtime option.
    internal static bool InjectTextureSlotFault; // Probe-only wrong-slot oracle.
    internal static bool InjectInvalidTextureSlot; // Probe-only fail-closed range control.
    private readonly OrderedWorldDevice _device = new();
    private readonly OrderedWorldCanvas _fallback;
    private readonly List<OrderedWorldResidency.Occurrence> _occurrences = new(8192);
    private readonly List<OrderedWorldResidency.Quad> _dynamic = new(256);
    internal readonly OrderedWorldRecords Records = new();
    private readonly List<OrderedWorldDevice.State> _states = new(8192);
    private readonly List<OrderedWorldDevice.Segment> _segments = new(1024);
    private readonly List<OrderedWorldCanvas.Command> _commands = new(8192);
    private readonly Dictionary<Texture2D, OrderedWorldDevice.Textures> _textures = new(ReferenceEqualityComparer.Instance);
    // A texture's RID is fixed for its life; asking Godot is an interop call.
    // Kept across frames, emptied when it grows past what a few frames use.
    private readonly Dictionary<Texture2D, Rid> _rids = new(ReferenceEqualityComparer.Instance);
    private readonly Callable _render;
    private OrderedWorldDevice.Push _push;
    private Transform2D _view;
    private Transform2D _offsetTransform;
    private Vector2 _lastOffset;
    private bool _haveOffset;
    private int _batchArtCount, _textureRuns;
    private OrderedWorldDevice.Textures _lastTextures;
    private Texture2D _hue, _light;
    private Rid _hueRid, _lightRid;
    private Rid _canvas, _presenter;
    private string _unsupported;
    private bool _queued, _disposed;
    private int _pending;
    private readonly string _vertexSource, _fragmentSource, _residentVertexSource;
    // ADR-0044 Depth World: upstream's depth for the next captured quad, and
    // the chunk land/statics drawn from resident buffers this frame.
    internal float Depth;
    internal readonly DepthWorldResident Resident = DepthWorldProbe.Enabled ? new DepthWorldResident() : null;
    private int _cameraX, _cameraY, _maxGroundZ = 127;
    internal bool Recording { get; private set; }
    internal bool Deferring => Recording && _unsupported == null;
    internal Texture2Drd Texture => _device.Presenter;

    internal OrderedWorldCapture(ShaderMaterial spriteMaterial, ShaderMaterial meshMaterial)
    {
        _fallback = new OrderedWorldCanvas(spriteMaterial, meshMaterial);
        _vertexSource = FileAccess.GetFileAsString("res://src/Labs/GpuRenderer/resident_world_vertex.glsl.txt");
        _fragmentSource = FileAccess.GetFileAsString("res://src/Labs/GpuRenderer/ordered_world_fragment.glsl.txt");
        if (Resident != null)
            _residentVertexSource = FileAccess.GetFileAsString("res://src/Labs/GpuRenderer/depth_world_resident_vertex.glsl.txt");
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
        _occurrences.Clear(); _dynamic.Clear(); _states.Clear(); _segments.Clear(); _textures.Clear(); _commands.Clear();
        Records.Begin();
        _batchArtCount = _textureRuns = 0;
        _canvas = default;
        Depth = 0f;
        Resident?.BeginFrame();
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
        ReplayCanvas();
        Fallback(reason);
    }

    internal bool MeshRecord(MeshLayer layer, int index, Vector2 offset)
    {
        if (!Deferring) return false;
        if (!OrderedWorldRecords.Enabled) return Quad(layer.Textures[index], layer.Vertices[index], offset, true);
        var record = Records.Find(layer, index, false)
            ?? Records.Store(layer, index, false, layer.Vertices[index]);
        return Quad(layer.Textures[index], layer.Vertices[index], offset, true, record: record);
    }

    internal bool Quad(Texture2D texture, in MeshQuad q, Vector2 offset, bool meshColor, bool rectangle = false,
        Transform2D? local = null, Rect2 destination = default, Rect2 source = default,
        OrderedWorldRecords.Record record = null)
    {
        if (!Recording || _unsupported != null) return false;
        if (texture == null) { Reject("missing texture"); return false; }
        if (record != null ? !record.Supported
            : !SupportedMode(q.Hue0) || !SupportedMode(q.Hue1) || !SupportedMode(q.Hue2) || !SupportedMode(q.Hue3))
        { Reject("unsupported shader mode"); return false; }
        if (_states.Count == OrderedWorldDevice.MaxOccurrences)
        { Reject("ordered occurrence capacity exceeded"); return false; }
        if (!_textures.TryGetValue(texture, out var textures))
        {
            if (GUO.Renderer.Arts.Art.IsHdTexture(texture)) { Reject("HD texture"); return false; }
            if (!_rids.TryGetValue(texture, out Rid rid))
            {
                if (_rids.Count >= 8192) _rids.Clear();
                _rids.Add(texture, rid = texture.GetRid());
            }
            textures = new OrderedWorldDevice.Textures(rid, _hueRid, _lightRid);
            _textures.Add(texture, textures); // Strong owner reference held until the ticket retires.
        }
        // Reserve before mutating any stream. A rejected command leaves the
        // captured geometry, original replay commands and occurrence rows aligned.
        _occurrences.EnsureCapacity(checked(_occurrences.Count + 1));
        if (record == null) _dynamic.EnsureCapacity(checked(_dynamic.Count + 1));
        _states.EnsureCapacity(checked(_states.Count + 1));
        _commands.EnsureCapacity(checked(_commands.Count + 1));
        _segments.EnsureCapacity(checked(_segments.Count + 1));
        uint first = checked((uint)_occurrences.Count * 6);
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
        // Reuse each original encoded color for both complete canvas replay and
        // GPU packing. Mesh replay must retain the value before UNORM8 conversion.
        Color color0 = record?.Color0 ?? UltimaBatcher2D.Encode(q.Hue0), color1 = record?.Color1 ?? UltimaBatcher2D.Encode(q.Hue1);
        Color color2 = record?.Color2 ?? UltimaBatcher2D.Encode(q.Hue2), color3 = record?.Color3 ?? UltimaBatcher2D.Encode(q.Hue3);
        _commands.Add(new OrderedWorldCanvas.Command
        {
            Type = meshColor ? OrderedWorldCanvas.Kind.Mesh : rectangle ? OrderedWorldCanvas.Kind.Rect : OrderedWorldCanvas.Kind.Triangles,
            FirstCorner = checked(_occurrences.Count * 4), Texture = textures.ArtRs, Offset = offset,
            Local = local ?? Transform2D.Identity, Destination = destination, Source = source,
            Color0 = meshColor ? color0 : default,
            Color1 = meshColor ? color1 : default,
            Color2 = meshColor ? color2 : default,
            Color3 = meshColor ? color3 : default,
        });
        if (_textureRuns == 0 || _lastTextures != textures) _textureRuns++;
        _lastTextures = textures;
        uint artSlot = AppendTextureBatch(first, textures);
        if (record != null)
        {
            _occurrences.Add(new OrderedWorldResidency.Occurrence(Records.Capture(record)));
            ReferencedQuads++;
        }
        else
        {
            _occurrences.Add(new OrderedWorldResidency.Occurrence(_dynamic.Count));
            _dynamic.Add(new OrderedWorldResidency.Quad
            {
                A = PackVertex(q.Position0, q.TextureCoordinate0, color0, q.Normal0, meshColor, rectangle),
                B = PackVertex(q.Position1, q.TextureCoordinate1, color1, q.Normal1, meshColor, rectangle),
                C = PackVertex(q.Position2, q.TextureCoordinate2, color2, q.Normal2, meshColor, rectangle),
                D = PackVertex(q.Position3, q.TextureCoordinate3, color3, q.Normal3, meshColor, rectangle),
            });
            DynamicCaptureBytes += 192;
        }
        _states.Add(new OrderedWorldDevice.State
        {
            Xx = transform.X.X, Yx = transform.Y.X, OriginX = transform.Origin.X,
            Xy = transform.X.Y, Yy = transform.Y.Y, OriginY = transform.Origin.Y,
            ArtSlot = InjectInvalidTextureSlot ? 4 : InjectTextureSlotFault ? 0 : artSlot, Pad1 = Depth,
        });
        SuppressedCommands++;
        return true;
    }

    /// <summary>ADR-0044: queue the visible chunks' land and statics from resident buffers.</summary>
    internal bool ResidentChunks(System.Collections.Generic.List<GUO.Game.Map.Chunk> chunks, int cameraX, int cameraY, sbyte maxGroundZ)
    {
        if (!Deferring || Resident == null) return false;
        _cameraX = cameraX; _cameraY = cameraY; _maxGroundZ = maxGroundZ;
        foreach (var chunk in chunks)
        {
            var mesh = chunk.Mesh;
            if (mesh == null) continue;
            if (!Resident.Add(mesh.Land, true, _hueRid, _lightRid, out string reason)
                || !Resident.Add(mesh.Statics, false, _hueRid, _lightRid, out reason))
            {
                Reject(reason);
                return false;
            }
        }
        Resident.Merge();
        return true;
    }


    private void ReplayCanvas()
    {
        // Read the same immutable revisions directly on rejection. No second
        // full vertex buffer is retained after a large failed frame.
        ReplayedQuads += _occurrences.Count;
        _fallback.Render(_canvas, CollectionsMarshal.AsSpan(_occurrences),
            CollectionsMarshal.AsSpan(_dynamic), CollectionsMarshal.AsSpan(_commands));
    }

    private uint AppendTextureBatch(uint first, OrderedWorldDevice.Textures textures)
    {
        if (_segments.Count != 0)
        {
            ref var last = ref CollectionsMarshal.AsSpan(_segments)[^1];
            if (last.Textures.Hue == textures.HueRs && last.Textures.Light == textures.LightRs)
            {
                int slot = last.Textures.FindArt(textures.ArtRs, _batchArtCount);
                if (slot < 0 && _batchArtCount < OrderedWorldDevice.ArtSlots)
                {
                    slot = _batchArtCount++;
                    last.Textures = last.Textures.WithArt(slot, textures.ArtRs);
                }
                if (slot >= 0)
                {
                    last.VertexCount = checked(last.VertexCount + 6);
                    return (uint)slot;
                }
            }
        }
        // Never recycle a slot inside an existing batch: earlier occurrences
        // still reference it. A fifth art view or palette change starts a draw.
        _segments.Add(new OrderedWorldDevice.Segment(first, 6, new OrderedWorldDevice.TextureSet(textures)));
        _batchArtCount = 1;
        return 0;
    }

    internal static bool SupportedMode(Vector3 hue)
    {
        int mode = ((int)hue.Y) & 0x3f;
        return mode <= 10 || mode is >= 20 and <= 30;
    }

    internal static OrderedWorldDevice.Vertex PackVertex(Vector2 p, Vector2 uv, Color color,
        Vector3 normal, bool meshColor, bool rectangle)
    {
        // ArrayMesh colors travel through UNORM8; rect/triangle canvas colors are floats.
        if (meshColor)
        {
            color.R = ByteColor(color.R); color.G = ByteColor(color.G);
            color.B = ByteColor(color.B); color.A = ByteColor(color.A);
        }
        // Keep the original addition of positive zero, including signed-zero/NaN
        // arithmetic, without calling the Debug GodotSharp Vector2 operator.
        return new OrderedWorldDevice.Vertex
        {
            PositionUv = new OrderedWorldDevice.Float4(p.X + 0f, p.Y + 0f, uv.X, uv.Y),
            PackedHue = new OrderedWorldDevice.Float4(color.R, color.G, color.B, color.A),
            Normal = new OrderedWorldDevice.Float4(normal.X, normal.Y, normal.Z, rectangle ? 1 : 0)
        };
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
                _device.InitializeRT(size, _vertexSource, _fragmentSource, _residentVertexSource);
            }
            if (InjectFailure == 1) throw new InvalidOperationException("injected before upload");
            _device.RenderRT(CollectionsMarshal.AsSpan(_occurrences), CollectionsMarshal.AsSpan(_dynamic),
                CollectionsMarshal.AsSpan(_states), CollectionsMarshal.AsSpan(_segments), _textureRuns, _push, true,
                Resident, _cameraX, _cameraY, _maxGroundZ);
            if (InjectFailure == 2) throw new InvalidOperationException("injected after recording");
            FramesDrawn++;
            success = true;
        }
        catch (Exception e)
        {
            Fallback(e.Message);
            ReplayCanvas();
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
            Records.Clear();
        }));
    }
}
#endif
