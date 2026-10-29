// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Renderer.DepthWorld;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    private OrderedWorldCapture _orderedWorld;
    internal SpeedMode.SurfaceRow OrderedSurface;
    internal Vector2I OrderedSurfaceOrigin;
    private Rid _orderedCanvas, _orderedPresenter;
    private int _orderedStackDepth;
    private ShaderMaterial _orderedPresentMaterial;
    private bool _orderedSurfaceDebugShown;
    private static readonly StringName ParamSurfaceDebug = "surface_debug";
    private static readonly StringName ParamSurfaceBuffer = "surface_buffer";
    internal static long OrderedCanvasItemsCleared;
    internal bool HasOrderedRecovery => OrderedWorldRecording && _orderedWorld.RecoverCanvas != null;
    internal void RejectOrderedWorld(string reason) => _orderedWorld?.Reject(reason);
    internal bool OrderedWorldRecording => _orderedWorld?.Recording == true;

    // Built once: a string argument makes a new StringName every frame.
    private static readonly StringName ParamWaterModel = "water_model";
    private static readonly StringName ParamHueTexture = "hue_texture";
    private static readonly StringName ParamLightTexture = "light_texture";
    private static readonly StringName ParamBrightlight = "brightlight";
    private static readonly StringName ParamCircleOfTransparencyCenter = "circle_of_transparency_center";
    private static readonly StringName ParamCircleOfTransparencyRadius = "circle_of_transparency_radius";

    // Capability is established before the gather can omit resident statics.
    internal bool CanAdmitDepthWorld(bool supported) => DepthWorldProbe.Enabled && OrderedWorldCapture.Enabled && supported
        && RenderingServer.IsOnRenderThread() && !_mirroring && !GpuWorld.Enabled && !BatchedWorld
        && _currentTarget != null && _currentTarget.Width > 0 && _currentTarget.Height > 0
        && _clipStack.Count == 0 && (_orderedWorld == null || _orderedWorld.CanBegin);

    private bool _orderedRawGroup;
    private int _orderedRawDepth;
    internal void BeginOrderedGroup(int group)
    {
        EndOrderedGroup();
        if (!OrderedWorldRecording) return;
        _orderedWorld.Group = group;
        OpenOrderedRawGroup();
    }

    private void OpenOrderedRawGroup()
    {
        if (!OrderedWorldRecording || !_orderedWorld.HasRejected || _orderedWorld.RecoverCanvas == null || _orderedRawGroup) return;
        FlushRun();
        _orderedRawDepth = _clipStack.Count;
        _clipStack.Add(_orderedWorld.RawGroup(_orderedWorld.Group));
        _orderedRawGroup = true;
        Cut();
    }

    internal void EndOrderedGroup()
    {
        if (!_orderedRawGroup) return;
        FlushRun();
        while (_clipStack.Count > _orderedRawDepth) _clipStack.RemoveAt(_clipStack.Count - 1);
        _orderedRawGroup = false;
        Cut();
    }

    internal void EndOrderedLand() { if (OrderedWorldRecording) _orderedWorld.EndLand(); }

    internal void ReplayOrderedFlatLand(Texture2D texture, Vector2 position, GUO.Compat.Rectangle source,
        Vector3 hue, Vector2 scale, int group)
    {
        int width = WidthOf(texture), height = HeightOf(texture);
        CalculateUVsSafe(source, width, height, out float sx, out float sy, out float sw, out float sh);
        scale.X *= source.Width; scale.Y *= source.Height;
        float ox = 0f / sw / width, oy = 0f / sh / height;
        _orderedWorld.ReplayRect(texture,
            new Rect2(position.X - ox * scale.X, position.Y - oy * scale.Y, scale.X, scale.Y),
            new Rect2(sx * width, sy * height, sw * width, sh * height), hue, group);
    }

    internal void SetOrderedRecovery(Action recover) { if (OrderedWorldRecording) _orderedWorld.RecoverCanvas = recover; }
    internal void BeginOrderedRecovery() => _orderedWorld.BeginRecovery();
    internal void OrderedRecoveryGroup(int id, int order) => _orderedWorld.RecoveryGroup(id, order);
    internal void ReplayOrderedGroup(int id) => _orderedWorld.ReplayGroup(id);
    internal void ReplayOrderedMesh(MeshLayer layer, int index, bool isStatic, int x, int y, int group)
        => _orderedWorld.ReplayMesh(layer, index, isStatic, x, y, group);

    internal void BeginOrderedWorld(bool supported, Vector2I probeSize = default)
    {
        OrderedWorldCapture.LastFrameOnPass = false;
        _orderedWorld?.ClearSurfaceBinding();
        if (!OrderedWorldCapture.Enabled) return;
        if (_retainCanvasFrame)
        {
            OrderedWorldCapture.Fallback("retained canvas was enabled at frame start");
            return;
        }
        if (!RenderingServer.IsOnRenderThread())
        {
            OrderedWorldCapture.Fallback("separate render thread unsupported by deferred canvas replay");
            return;
        }
        if (!supported || _mirroring || GpuWorld.Enabled || BatchedWorld || _sampler != SamplerState.PointClamp)
        {
            OrderedWorldCapture.Fallback("unsupported underlay, mirroring, sampler, batched world or legacy GPU pass");
            return;
        }
        Vector2I size = _currentTarget != null
            ? new Vector2I(_currentTarget.Width, _currentTarget.Height) : probeSize;
        if (size.X <= 0 || size.Y <= 0 || _clipStack.Count != 0)
        {
            OrderedWorldCapture.Fallback("invalid target or inherited clip");
            return;
        }
        _orderedProbeSize = size;
        if (_material.GetShaderParameter(ParamWaterModel).AsInt32() != 0)
        {
            OrderedWorldCapture.Fallback("styled water");
            return;
        }
        try { _orderedWorld ??= new OrderedWorldCapture(_material, _meshMaterial); }
        catch (InvalidOperationException error) { OrderedWorldCapture.Fallback(error.Message); return; }
        _orderedWorld.Rejected = OpenOrderedRawGroup;
        if (!_orderedWorld.Begin(size, _viewTransform,
            _material.GetShaderParameter(ParamHueTexture).AsGodotObject() as Texture2D,
            _material.GetShaderParameter(ParamLightTexture).AsGodotObject() as Texture2D,
            _material.GetShaderParameter(ParamBrightlight).AsSingle(),
            _material.GetShaderParameter(ParamCircleOfTransparencyCenter).AsVector2(),
            _material.GetShaderParameter(ParamCircleOfTransparencyRadius).AsSingle())) return;
        _orderedWorld.SurfaceOrigin=OrderedSurfaceOrigin;
        FlushRun();
        _orderedCanvas = NewItem(CurrentParent, Transform2D.Identity);
        _orderedWorld.SetCanvas(_orderedCanvas);
        // This item is only a container; no commands inherit its material.
        RenderingServer.CanvasItemSetVisible(_orderedCanvas, true);
        _orderedStackDepth = _clipStack.Count;
        _clipStack.Add(_orderedCanvas);
        Cut();
    }

    internal void EndOrderedWorld()
    {
        EndOrderedGroup();
        if (!OrderedWorldRecording) return;
        FlushRun();
        if (_clipStack.Count != _orderedStackDepth + 1)
            _orderedWorld.Reject("unbalanced clip stack");
        while (_clipStack.Count > _orderedStackDepth) _clipStack.RemoveAt(_clipStack.Count - 1);
        _orderedPresentMaterial ??= new ShaderMaterial
        {
            Shader = new Shader { Code = "shader_type canvas_item; render_mode unshaded, blend_disabled; uniform bool surface_debug = false; uniform sampler2D surface_buffer : filter_nearest; void fragment() {             COLOR = texture(TEXTURE, UV);             if (surface_debug) { int data = int(texture(surface_buffer, UV).w); int k = data & 15;                 vec3 c = k == 1 ? vec3(1,0,0) : k == 2 ? vec3(0,1,0) : k == 3 ? vec3(0,0,1) :                     k == 4 ? vec3(1) : k == 5 ? vec3(1,0,1) : k == 6 ? vec3(0,1,1) : vec3(0);                 if ((data & 16) != 0 && (int((FRAGCOORD.x + FRAGCOORD.y)/4.0) % 2) == 0) c = vec3(0.2);                 COLOR = vec4(c,1); } }" }
        };
        bool surfaceDebug = SpeedMode.SpeedModeDebug.Active && SpeedMode.SpeedModeDebug.GBuffer;
        if (surfaceDebug != _orderedSurfaceDebugShown)
        {
            _orderedPresentMaterial.SetShaderParameter(ParamSurfaceDebug, surfaceDebug);
            if (surfaceDebug) _orderedPresentMaterial.SetShaderParameter(ParamSurfaceBuffer, _orderedWorld.SurfaceTexture);
            else _orderedPresentMaterial.SetShaderParameter(ParamSurfaceBuffer, default);
            _orderedSurfaceDebugShown = surfaceDebug;
        }
        _orderedPresenter = NewItem(CurrentParent, _viewTransform.AffineInverse());
        ItemSetMaterial(_orderedPresenter, _orderedPresentMaterial);
        RenderingServer.CanvasItemSetVisible(_orderedPresenter, false);
        // Texture2Drd has its RID before initialization; the RT transaction binds its image.
        Vector2I size = _currentTarget != null ? new Vector2I(_currentTarget.Width, _currentTarget.Height) : _orderedProbeSize;
        RenderingServer.CanvasItemAddTextureRect(_orderedPresenter, new Rect2(Vector2.Zero, size), _orderedWorld.Texture.GetRid());
        _orderedWorld.End(_orderedCanvas, _orderedPresenter);
        Cut();
    }

    private Vector2I _orderedProbeSize;
    private void AbortOrderedWorldFrame()
    {
        // The pool clear that follows empties and hides these items.
        _orderedCanvas = _orderedPresenter = default;
        _orderedRawGroup = false;
        _orderedStackDepth = 0;
        _orderedWorld?.AbortFrame();
    }

    internal OrderedWorldCapture OrderedWorldProbe => _orderedWorld; // Probe only.

    private void ResetOrderedWorldItems()
    {
        ResetSpeedComposeItems();
        _orderedWorld?.ClearSurfaceBinding(true);
        _orderedWorld?.CancelUnsubmittedFrame();
        _orderedWorld?.ClearCanvas();
        // Pool roles can change next frame: undo the previous late visibility election.
        if (_orderedCanvas.IsValid) RenderingServer.CanvasItemSetVisible(_orderedCanvas, true);
        if (_orderedPresenter.IsValid) RenderingServer.CanvasItemSetVisible(_orderedPresenter, true);
        _orderedCanvas = _orderedPresenter = default;
    }

    /// <summary>ADR-0044: chunk land and statics from resident GPU buffers; nothing per quad here.</summary>
    internal int DrawDepthWorldResident(System.Collections.Generic.List<GUO.Game.Map.Chunk> chunks, int offsetX, int offsetY, sbyte maxGroundZ)
    {
        if (!OrderedWorldRecording) return 0;
        EnsureStarted();
        return _orderedWorld.ResidentChunks(chunks, offsetX, offsetY, maxGroundZ) ? _orderedWorld.Resident.Draws.Count : 0;
    }

    internal int DrawOrderedLand(MeshLayer layer)
    {
        int count = 0;
        for (int i = 0; i < layer.Count; i++)
        {
            if (!layer.Visible[i]) continue;
            if (OrderedWorldRecording && _orderedWorld.MeshRecord(layer, i, _worldOffset)) count++;
            else count += DrawMeshSprite(layer, i, (int)-_worldOffset.X, (int)-_worldOffset.Y);
        }
        return count;
    }

    private bool CaptureOrderedQuad(Texture2D texture, Vector3 hue, bool rectangle = false,
        Rect2 destination = default, Rect2 source = default)
    {
        if (!OrderedWorldRecording) return false;
        MeshQuad q = OrderedScratchQuad(hue);
        _orderedWorld.Depth = OrderedDepth;
        _orderedWorld.Surface = OrderedSurface;
        return _orderedWorld.Quad(texture, q, _worldOffset, false, rectangle, destination: destination, source: source);
    }

    private static readonly Vector3 ScratchNormal = new(0, 0, 1);

    private MeshQuad OrderedScratchQuad(Vector3 hue) => new()
        {
            Position0 = _quadPoints[0], Position1 = _quadPoints[1], Position2 = _quadPoints[2], Position3 = _quadPoints[3],
            TextureCoordinate0 = _quadUVs[0], TextureCoordinate1 = _quadUVs[1], TextureCoordinate2 = _quadUVs[2], TextureCoordinate3 = _quadUVs[3],
            Hue0 = hue, Hue1 = hue, Hue2 = hue, Hue3 = hue,
            Normal0 = ScratchNormal, Normal1 = ScratchNormal, Normal2 = ScratchNormal, Normal3 = ScratchNormal
        };

    private bool CaptureOrderedStatic(MeshLayer layer, int index, int offsetX, int offsetY)
    {
        if (!_orderedWorld.Deferring || !OrderedWorldRecords.Enabled) return false;
        EnsureStarted();
        Texture2D texture = layer.Textures[index];
        int width = WidthOf(texture), height = HeightOf(texture);
        var record = _orderedWorld.Records.Find(layer, index, true, offsetX, offsetY, width, height);
        MeshQuad q = default;
        Rect2 destination, source;
        if (record != null)
        {
            destination = record.Destination; source = record.Source;
        }
        else
        {
            ref var original = ref layer.Vertices[index];
            var uvSize = original.TextureCoordinate3 - original.TextureCoordinate0;
            var size = original.Position3 - original.Position0;
            float x = original.Position0.X - offsetX, y = original.Position0.Y - offsetY;
            // Keep AddSprite's exact expressions and rectangle diagonal. Moving
            // the camera subtraction into a matrix could change rasterization.
            SetQuadPoints(x, y, size.X, size.Y, 0f, 0f, 0f, 1f);
            SetQuadUVs(original.TextureCoordinate0.X, original.TextureCoordinate0.Y, uvSize.X, uvSize.Y, uvSize.Y, 0);
            q = OrderedScratchQuad(original.Hue0);
            destination = new Rect2(x - 0f * size.X, y - 0f * size.Y, size.X, size.Y);
            source = new Rect2(original.TextureCoordinate0.X * width, original.TextureCoordinate0.Y * height, uvSize.X * width, uvSize.Y * height);
            record = _orderedWorld.Records.Store(layer, index, true, q, offsetX, offsetY, width, height, destination, source);
        }
        _orderedWorld.Depth = OrderedDepth;
        _orderedWorld.Surface = OrderedSurface;
        return _orderedWorld.Quad(texture, q, _worldOffset, false, true, destination: destination, source: source, record: record);
    }

    private bool CaptureOrderedAffine(Texture2D texture, Vector3 hue, Rect2 rect, Vector2 textureSize, Transform2D local)
    {
        if (!OrderedWorldRecording) return false;
        Vector2 tl = rect.Position, br = rect.End;
        var q = new MeshQuad
        {
            Position0 = tl, Position1 = new Vector2(br.X,tl.Y), Position2 = new Vector2(tl.X,br.Y), Position3 = br,
            TextureCoordinate0 = tl / textureSize, TextureCoordinate1 = new Vector2(br.X,tl.Y) / textureSize,
            TextureCoordinate2 = new Vector2(tl.X,br.Y) / textureSize, TextureCoordinate3 = br / textureSize,
            Hue0=hue,Hue1=hue,Hue2=hue,Hue3=hue,
            Normal0=new Vector3(0,0,1),Normal1=new Vector3(0,0,1),Normal2=new Vector3(0,0,1),Normal3=new Vector3(0,0,1)
        };
        _orderedWorld.Depth = OrderedDepth;
        _orderedWorld.Surface = OrderedSurface;
        return _orderedWorld.Quad(texture,q,_worldOffset,false,true,local,rect,rect);
    }
}
