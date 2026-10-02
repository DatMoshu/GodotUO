// PSX post pass for the 3D pregame's SubViewport, drawn by the TextureRect
// that upscales it (nearest). GUO, BSD-2-Clause.
// 15-bit colour (5 bits a channel) with a 4x4 ordered (Bayer) dither in the
// internal-resolution pixel grid. One texture read; no branches.
// Target: any renderer (canvas_item).
shader_type canvas_item;

// The SubViewport's size: the dither pattern is laid on its pixels, not the window's.
uniform vec2 source_size = vec2(640.0, 400.0);
// Steps per channel minus one: 31 = 5 bits.
uniform float levels = 31.0;
// 1 = a full step of dither, 0 = plain banding.
uniform float dither_strength : hint_range(0.0, 1.0) = 1.0;

const float BAYER[16] = float[16](
	0.0, 8.0, 2.0, 10.0,
	12.0, 4.0, 14.0, 6.0,
	3.0, 11.0, 1.0, 9.0,
	15.0, 7.0, 13.0, 5.0);

void fragment() {
	vec4 c = texture(TEXTURE, UV);
	ivec2 p = ivec2(floor(UV * source_size));
	float threshold = (BAYER[(p.x & 3) + (p.y & 3) * 4] + 0.5) / 16.0 - 0.5;
	vec3 q = floor(c.rgb * levels + 0.5 + threshold * dither_strength) / levels;
	COLOR = vec4(clamp(q, 0.0, 1.0), 1.0);
}
