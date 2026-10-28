// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps;
using Rectangle = GUO.Compat.Rectangle;
using Color = GUO.Compat.Color;
namespace GUO.Renderer;

internal sealed partial class WorldOverview
{
    // Server-provided city coordinates, replaced on each login; no assumed shard map.
    internal static CityInfo[] Cities = Array.Empty<CityInfo>();
    private readonly List<Rect2> _labelBounds = new(64);
    private readonly HashSet<string> _labelNames = new(StringComparer.OrdinalIgnoreCase);
    private Shader _mapShader;
    private Shader _pinShader;
    private ShaderMaterial _pinMaterial;
    private ShaderMaterial _mapMaterial;
    private int _style = -1;
    private void EnsureStyle()
    {
        if (_mapMaterial == null)
        {
            _mapShader = new Shader { Code = MapShader };
            _mapMaterial = new ShaderMaterial { Shader = _mapShader };
        }
        int style = Math.Clamp(ProfileManager.CurrentProfile?.WorldOverviewStyle ?? 0, 0, 3);
        if (_style != style) { _style = style; _mapMaterial.SetShaderParameter("style", style); }
    }
    private const string MapShader = @"
shader_type canvas_item;
render_mode unshaded;
uniform int style = 0;
void fragment() {
    vec4 c = texture(TEXTURE, UV);
    if (style == 1) {
        float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
        c.rgb = mix(c.rgb, vec3(0.88, 0.76, 0.52) * (0.36 + l * 0.8), 0.72);
    } else if (style == 2) {
        vec2 inset = TEXTURE_PIXEL_SIZE * 0.5;
        vec3 n = texture(TEXTURE, clamp(UV + TEXTURE_PIXEL_SIZE, inset, vec2(1.0)-inset)).rgb;
        float edge = clamp(length(c.rgb-n) * 0.38, 0.0, 0.22);
        c.rgb = clamp(c.rgb * (1.0-edge) + vec3(edge * 0.16), vec3(0.0), vec3(1.0));
    } else if (style == 3) {
        float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
        c.rgb = mix(c.rgb * vec3(0.56, 0.72, 1.0), vec3(0.12, 0.26, 0.40) * l, 0.35);
    }
    COLOR = c;
}";

    internal static bool EligibleCity(CityInfo city, int map, int width, int height) =>
        city != null && city.IsNewCity && city.Map == (uint)map && city.X < width && city.Y < height;

    private void DrawLabels(UltimaBatcher2D batcher, Transform2D transform, int map, int width, int height, Vector2 view, Vector2 player)
    {
        _labelBounds.Clear(); _labelNames.Clear();
        _labelBounds.Add(new Rect2(player.X - 30, player.Y - 52, 60, 64));
        foreach (var city in Cities)
            if (EligibleCity(city, map, width, height))
                Label(batcher, city.City, transform * new Vector2(city.X, city.Y), view);
        // Reuse city marker packs already loaded by the ordinary map. Respect hidden files.
        int inspected = 0;
        foreach (var file in WorldMapGump._markerFiles)
        {
            if (file.Hidden || file.Markers == null) continue;
            foreach (var marker in file.Markers)
            {
                if (++inspected > 2048 || _labelBounds.Count >= 64) return;
                if (marker.MapId == map && marker.X >= 0 && marker.Y >= 0 && marker.X < width && marker.Y < height
                    && (file.Name.Contains("cit", StringComparison.OrdinalIgnoreCase)
                    || file.Name.Contains("town", StringComparison.OrdinalIgnoreCase)
                    || (marker.MarkerIconName?.Contains("town", StringComparison.OrdinalIgnoreCase) ?? false)))
                    Label(batcher, marker.Name, transform * new Vector2(marker.X, marker.Y), view);
            }
        }
    }
    private void Label(UltimaBatcher2D batcher, string name, Vector2 ground, Vector2 view)
    {
        if (string.IsNullOrWhiteSpace(name) || _labelBounds.Count >= 64) return;
        if (name.Length > 48) name = name[..48];
        if (_labelNames.Contains(name)) return;
        var size = Fonts.Map1.MeasureString(name);
        var rect = new Rect2((int)(ground.X-size.X/2)-5, (int)(ground.Y-size.Y)-28, size.X+10, size.Y+8);
        if (rect.Position.X < 3 || rect.Position.Y < 3 || rect.End.X > view.X-3 || ground.Y > view.Y-3) return;
        foreach (var other in _labelBounds) if (rect.Intersects(other)) return;
        _labelNames.Add(name);
        _labelBounds.Add(rect);
        Fill(batcher, (int)ground.X, (int)rect.End.Y, 1, 18, new Color(136, 121, 85));
        Fill(batcher, (int)rect.Position.X+2, (int)rect.Position.Y+3, (int)rect.Size.X, (int)rect.Size.Y, Color.Black);
        Fill(batcher, (int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y, new Color(38, 42, 44));
        Fill(batcher, (int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, 1, new Color(176, 156, 108));
        batcher.DrawString(Fonts.Map1, name, (int)rect.Position.X+5, (int)rect.Position.Y+4, Vector3.Back, 0);
    }
    private static void Fill(UltimaBatcher2D b, int x, int y, int w, int h, Color c) =>
        b.Draw(SolidColorTextureCache.GetTexture(c), new Rectangle(x,y,w,h), Vector3.Back, 0);
    private void DrawPlayerPin(UltimaBatcher2D b, Vector2 p)
    {
        if (_pinMaterial == null)
        {
            _pinShader = new Shader { Code = @"
shader_type canvas_item;
render_mode unshaded;
void fragment() {
    vec2 q = UV * vec2(48.0,64.0);
    float d = length(q-vec2(24.0,22.0));
    float tip = abs(q.x-24.0) - (54.0-q.y)*0.38;
    bool triangle = q.y >= 26.0 && q.y <= 54.0 && tip <= 0.0;
    float glow = (1.0-smoothstep(13.0,23.0,d)) * (0.18+0.07*sin(TIME*2.0));
    vec4 c=vec4(1.0,0.72,0.18,glow);
    if (d<=14.0 || triangle) c=vec4(0.035,0.045,0.055,1.0);
    if (d<=11.0 || (triangle && q.y<50.0 && tip < -2.0)) c=vec4(1.0,0.77,0.22,1.0);
    if (d<=6.0) c=vec4(0.96,0.98,1.0,1.0);
    if (d<=3.5) c=vec4(0.12,0.30,0.40,1.0);
    COLOR=c;
}" };
            _pinMaterial = new ShaderMaterial { Shader = _pinShader };
        }
        b.DrawOverviewTexture(SolidColorTextureCache.GetTexture(Color.White), new Rect2((int)p.X-24,(int)p.Y-54,48,64), _pinMaterial);
    }
}

