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
    internal bool OrderedWorldRecording => _orderedWorld?.Recording == true;

    internal void BeginOrderedWorld(bool supported, Vector2I probeSize = default)
    {
        OrderedWorldCapture.LastFrameOnPass = false;
        if (!OrderedWorldCapture.Enabled) return;
        if (!supported || _mirroring || GpuWorld.Enabled)
        {
            OrderedWorldCapture.Fallback("unsupported underlay, mirroring or legacy GPU pass");
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
        _orderedWorld ??= new OrderedWorldCapture();
        if (!_orderedWorld.Begin(size, _viewTransform,
            _material.GetShaderParameter("hue_texture").AsGodotObject() as Texture2D,
            _material.GetShaderParameter("light_texture").AsGodotObject() as Texture2D,
            _material.GetShaderParameter("brightlight").AsSingle(),
            _material.GetShaderParameter("circle_of_transparency_center").AsVector2(),
            _material.GetShaderParameter("circle_of_transparency_radius").AsSingle())) return;
        if (_material.GetShaderParameter("water_model").AsInt32() != 0)
            _orderedWorld.Reject("styled water");
        FlushRun();
        _orderedCanvas = NewItem(CurrentParent, Transform2D.Identity);
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
        // Pool roles can change next frame: undo the previous late visibility election.
        if (_orderedCanvas.IsValid) RenderingServer.CanvasItemSetVisible(_orderedCanvas, true);
        if (_orderedPresenter.IsValid) RenderingServer.CanvasItemSetVisible(_orderedPresenter, true);
        _orderedCanvas = _orderedPresenter = default;
    }

    internal void CaptureOrderedLand(MeshLayer layer)
    {
        if (!OrderedWorldRecording) return;
        for (int i = 0; i < layer.Count; i++)
            if (layer.Visible[i]) _orderedWorld.Quad(layer.Textures[i], layer.Vertices[i], _worldOffset, true);
    }

    private void CaptureOrderedQuad(Texture2D texture, Vector3 hue, bool rectangle = false)
    {
        if (!OrderedWorldRecording) return;
        var q = new MeshQuad
        {
            Position0 = _quadPoints[0], Position1 = _quadPoints[1], Position2 = _quadPoints[2], Position3 = _quadPoints[3],
            TextureCoordinate0 = _quadUVs[0], TextureCoordinate1 = _quadUVs[1], TextureCoordinate2 = _quadUVs[2], TextureCoordinate3 = _quadUVs[3],
            Hue0 = hue, Hue1 = hue, Hue2 = hue, Hue3 = hue,
            Normal0 = new Vector3(0,0,1), Normal1 = new Vector3(0,0,1), Normal2 = new Vector3(0,0,1), Normal3 = new Vector3(0,0,1)
        };
        _orderedWorld.Quad(texture, q, _worldOffset, false, rectangle);
    }

    private void CaptureOrderedAffine(Texture2D texture, Vector3 hue, Rect2 rect, Vector2 textureSize, Transform2D local)
    {
        if (!OrderedWorldRecording) return;
        Vector2 tl = rect.Position, br = rect.End;
        var q = new MeshQuad
        {
            Position0 = tl, Position1 = new Vector2(br.X,tl.Y), Position2 = new Vector2(tl.X,br.Y), Position3 = br,
            TextureCoordinate0 = tl / textureSize, TextureCoordinate1 = new Vector2(br.X,tl.Y) / textureSize,
            TextureCoordinate2 = new Vector2(tl.X,br.Y) / textureSize, TextureCoordinate3 = br / textureSize,
            Hue0=hue,Hue1=hue,Hue2=hue,Hue3=hue,
            Normal0=new Vector3(0,0,1),Normal1=new Vector3(0,0,1),Normal2=new Vector3(0,0,1),Normal3=new Vector3(0,0,1)
        };
        _orderedWorld.Quad(texture,q,_worldOffset,false,true,local);
    }
}
#endif
