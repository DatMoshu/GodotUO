// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

// Lead-owned camera code explicitly opts in; default is the unchanged world view.
internal readonly record struct SpeedModeCloudView(bool Enabled, float Zoom, float Height);

/// <summary>Procedural cloud plane for the experimental above-cloud camera.</summary>
public partial class SpeedModeCloudPass : ColorRect
{
    private ShaderMaterial _cloudMaterial;
    private CloudPreset _preset;
    private readonly Texture2D[] _sprites = new Texture2D[2];
    private readonly bool[] _ownsSprite = new bool[2];

    // Called once at cloud activation, never from a per-frame file watcher.
    // Invalid input is atomic: retain the unchanged legacy plane.
    internal bool Configure(string path)
    {
        if (_cloudMaterial != null) throw new InvalidOperationException("Configure clouds before Bind");
        ClearSprites();
        _preset = null;
        if (!File.Exists(path)) return false;
        try
        {
            var preset = CloudPreset.Load(path);
            for (int i=0;i<preset.Layers.Length;i++)
            {
                string sprite = preset.Layers[i].Sprite;
                if (sprite == "builtin:billow")
                    _sprites[i] = GD.Load<Texture2D>("res://src/Render/SpeedMode/Weather/Clouds/billow.svg");
                else if (sprite.Length > 0)
                {
                    string file = CloudPreset.ResolveSprite(Path.GetDirectoryName(Path.GetFullPath(path)),sprite);
                    byte[] bytes=CloudPreset.ReadSprite(file);
                    using var image = new Image();
                    Error decoded=System.IO.Path.GetExtension(file)==".png" ? image.LoadPngFromBuffer(bytes) : image.LoadWebpFromBuffer(bytes);
                    if (decoded!=Error.Ok || image.IsEmpty() || image.GetWidth()>1024 || image.GetHeight()>1024)
                        throw new InvalidDataException("Cloud sprite must be a valid image at most 1024x1024");
                    _sprites[i] = ImageTexture.CreateFromImage(image);
                    _ownsSprite[i] = true;
                }
                if (sprite.Length>0 && _sprites[i]==null) throw new InvalidDataException("Cloud sprite could not load");
            }
            _preset = preset;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ClearSprites();
            _preset = null;
            GD.PushWarning("Cloud preset ignored; using legacy clouds. " + e.Message);
            return false;
        }
    }

    internal void Bind(Vector2I size, Vector3 camera, SpeedModeCloudView view, float wind, double seconds, float daylight = 1)
    {
        if (!view.Enabled || !float.IsFinite(view.Zoom) || view.Zoom <= 4
            || !float.IsFinite(view.Height) || view.Height <= 0 || size.X <= 0 || size.Y <= 0)
        {
            HideForFallback();
            return;
        }
        if (_cloudMaterial == null)
        {
            _cloudMaterial = new ShaderMaterial {
                Shader = GD.Load<Shader>(_preset == null
                    ? "res://src/Render/SpeedMode/Weather/speed_cloud.gdshader"
                    : "res://src/Render/SpeedMode/Weather/Clouds/cloud_variants.gdshader") };
            ApplyPreset();
        }
        Material = _cloudMaterial;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        Size = size;
        _cloudMaterial.SetShaderParameter("target_size", new Vector2(size.X, size.Y));
        _cloudMaterial.SetShaderParameter("camera_tile", new Vector2(camera.X, camera.Y));
        _cloudMaterial.SetShaderParameter("camera_zoom", view.Zoom);
        _cloudMaterial.SetShaderParameter("camera_height", view.Height);
        _cloudMaterial.SetShaderParameter("wind", float.IsFinite(wind) ? Math.Clamp(wind, -2, 2) : 0);
        _cloudMaterial.SetShaderParameter("seconds", double.IsFinite(seconds) ? (float)(_preset == null ? seconds % 3600 : seconds) : 0);
        _cloudMaterial.SetShaderParameter("daylight", float.IsFinite(daylight) ? Math.Clamp(daylight, 0, 1) : _preset == null ? 1 : 0);
        Visible = true;
    }

    private void ApplyPreset()
    {
        if (_preset == null) return;
        _cloudMaterial.SetShaderParameter("zoom_blend",new Vector2(_preset.ZoomStart,_preset.ZoomEnd));
        _cloudMaterial.SetShaderParameter("layer_count",_preset.Layers.Length);
        for (int i=0;i<_preset.Layers.Length;i++)
        {
            var layer = _preset.Layers[i];
            int algorithm = layer.Algorithm == "procedural" ? 0 : layer.Algorithm == "sprite-layered" ? 1 : 2;
            int mask = layer.Mask == "billow" ? 0 : layer.Mask == "haze" ? 1 : 2;
            _cloudMaterial.SetShaderParameter("shape"+i,new Vector4(algorithm,mask,layer.Density,layer.Coverage));
            _cloudMaterial.SetShaderParameter("motion"+i,new Vector4(layer.Scale,layer.WindDegrees*MathF.PI/180,layer.WindSpeed,layer.Opacity));
            _cloudMaterial.SetShaderParameter("altitude"+i,new Vector3(layer.Height,layer.Parallax,layer.DaynightResponse));
            _cloudMaterial.SetShaderParameter("day"+i,new Vector3(layer.DayColor[0],layer.DayColor[1],layer.DayColor[2]));
            _cloudMaterial.SetShaderParameter("night"+i,new Vector3(layer.NightColor[0],layer.NightColor[1],layer.NightColor[2]));
            if (_sprites[i]!=null) _cloudMaterial.SetShaderParameter("sprite"+i,_sprites[i]);
        }
    }

    private void ClearSprites()
    {
        for (int i=0;i<2;i++) {
            if (_ownsSprite[i]) _sprites[i]?.Dispose();
            _sprites[i]=null; _ownsSprite[i]=false;
        }
    }
    internal void ProtectPin(Vector2 pixel,bool overview)
    {
        _cloudMaterial?.SetShaderParameter("pin_pixel",pixel);
        _cloudMaterial?.SetShaderParameter("opacity_cap",overview ? 0.32f : 0.82f);
    }
    internal void HideForFallback() => Visible = false;
    public override void _ExitTree()
    {
        Material = null;
        _cloudMaterial?.Dispose();
        _cloudMaterial = null;
        ClearSprites();
        _preset = null;
    }
}
