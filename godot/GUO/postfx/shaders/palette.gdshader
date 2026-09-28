// Palette remap with ordered dithering (ADR-0023): every pixel becomes the
// nearest colour of a small palette, with a 4x4 Bayer threshold to dither the
// in-betweens, at the art's own pixels. Palettes: 16-colour EGA, 4-shade
// handheld green, 4-shade handheld grey; or `palette_tex`, a 1-pixel-high strip
// of up to 32 colours (palette 3).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D palette_tex : filter_nearest;
uniform int palette : hint_range(0, 3, 1) = 0;   // 0 EGA, 1 handheld green, 2 handheld grey, 3 palette_tex
uniform int palette_tex_colors : hint_range(2, 32, 1) = 16;
uniform float dither : hint_range(0.0, 1.0, 0.01) = 0.6;
uniform float pixel : hint_range(1.0, 4.0, 1.0) = 1.0;   // chunkier pixels, still nearest

const vec3 EGA[16] = vec3[16](
	vec3(0.0), vec3(0.0, 0.0, 0.667), vec3(0.0, 0.667, 0.0), vec3(0.0, 0.667, 0.667),
	vec3(0.667, 0.0, 0.0), vec3(0.667, 0.0, 0.667), vec3(0.667, 0.333, 0.0), vec3(0.667),
	vec3(0.333), vec3(0.333, 0.333, 1.0), vec3(0.333, 1.0, 0.333), vec3(0.333, 1.0, 1.0),
	vec3(1.0, 0.333, 0.333), vec3(1.0, 0.333, 1.0), vec3(1.0, 1.0, 0.333), vec3(1.0));
const vec3 GREEN[4] = vec3[4](vec3(0.059, 0.220, 0.059), vec3(0.188, 0.384, 0.188),
	vec3(0.545, 0.675, 0.059), vec3(0.608, 0.737, 0.059));
const vec3 GREY[4] = vec3[4](vec3(0.07), vec3(0.33), vec3(0.64), vec3(0.93));
const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0,
	3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

vec3 nearest(vec3 c) {
	vec3 best = vec3(0.0);
	float bd = 1e9;
	int n = palette == 0 ? 16 : palette == 3 ? palette_tex_colors : 4;
	for (int i = 0; i < 32; i++) {
		if (i >= n) { break; }
		vec3 p;
		if (palette == 0) { p = EGA[i]; }
		else if (palette == 1) { p = GREEN[i]; }
		else if (palette == 2) { p = GREY[i]; }
		else { p = texture(palette_tex, vec2((float(i) + 0.5) / float(n), 0.5)).rgb; }
		vec3 d = c - p;
		float dd = dot(d * vec3(0.30, 0.59, 0.11), d);
		if (dd < bd) { bd = dd; best = p; }
	}
	return best;
}

void fragment() {
	vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
	vec2 cell = floor(SCREEN_UV * size / pixel);
	vec2 uv = (cell * pixel + 0.5) / size;
	vec4 c = texture(source, uv);
	ivec2 b = ivec2(mod(cell, 4.0));
	float t = (BAYER[b.y * 4 + b.x] + 0.5) / 16.0 - 0.5;
	float spread = palette == 0 ? 0.33 : 0.25;
	vec3 q = nearest(clamp(c.rgb + t * spread * dither, 0.0, 1.0));
	COLOR = vec4(q, c.a);
}
