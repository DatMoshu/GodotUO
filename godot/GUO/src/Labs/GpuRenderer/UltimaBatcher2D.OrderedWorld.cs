// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using Godot;
using GUO.Labs.GpuRenderer;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    private OrderedWorldCapture _orderedWorld;
    private Rid _orderedCanvas, _orderedPresenter;
    private int _orderedStackDepth;
    private ShaderMaterial _orderedPresentMaterial;
    internal static long OrderedCanvasItemsCleared;
    internal bool OrderedWorldRecording => _orderedWorld?.Recording == true;

    internal void BeginOrderedWorld(bool supported, Vector2I probeSize = default)
    {
        OrderedWorldCapture.LastFrameOnPass = false;
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
        if (_material.GetShaderParameter("water_model").AsInt32() != 0)
        {
            OrderedWorldCapture.Fallback("styled water");
            return;
        }
        _orderedWorld ??= new OrderedWorldCapture(_material, _meshMaterial);
        if (!_orderedWorld.Begin(size, _viewTransform,
            _material.GetShaderParameter("hue_texture").AsGodotObject() as Texture2D,
            _material.GetShaderParameter("light_texture").AsGodotObject() as Texture2D,
            _material.GetShaderParameter("brightlight").AsSingle(),
            _material.GetShaderParameter("circle_of_transparency_center").AsVector2(),
            _material.GetShaderParameter("circle_of_transparency_radius").AsSingle())) return;
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
        if (!OrderedWorldRecording) return;
        FlushRun();
        if (_clipStack.Count != _orderedStackDepth + 1)
            _orderedWorld.Reject("unbalanced clip stack");
        while (_clipStack.Count > _orderedStackDepth) _clipStack.RemoveAt(_clipStack.Count - 1);
        _orderedPresentMaterial ??= new ShaderMaterial
        {
            Shader = new Shader { Code = "shader_type canvas_item; render_mode unshaded, blend_disabled; void fragment() { COLOR = texture(TEXTURE, UV); }" }
        };
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
    private void ResetOrderedWorldItems()
    {
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
        return _orderedWorld.Quad(texture, q, _worldOffset, false, rectangle, destination: destination, source: source);
    }

    private MeshQuad OrderedScratchQuad(Vector3 hue) => new()
        {
            Position0 = _quadPoints[0], Position1 = _quadPoints[1], Position2 = _quadPoints[2], Position3 = _quadPoints[3],
            TextureCoordinate0 = _quadUVs[0], TextureCoordinate1 = _quadUVs[1], TextureCoordinate2 = _quadUVs[2], TextureCoordinate3 = _quadUVs[3],
            Hue0 = hue, Hue1 = hue, Hue2 = hue, Hue3 = hue,
            Normal0 = new Vector3(0,0,1), Normal1 = new Vector3(0,0,1), Normal2 = new Vector3(0,0,1), Normal3 = new Vector3(0,0,1)
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
        return _orderedWorld.Quad(texture,q,_worldOffset,false,true,local,rect,rect);
    }
}
#endif
