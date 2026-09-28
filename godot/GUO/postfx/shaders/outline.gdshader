// Outline: ink along edges, found from brightness (luma) or colour differences
// between neighbouring pixels, at the art's own pixel size (ADR-0023). Object-id
// edges (only between different objects) come later with an id buffer.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform int mode : hint_enum("Brightness edges", "Colour edges") = 0;
uniform float threshold : hint_range(0.02, 1.0, 0.01) = 0.22;
uniform float thickness : hint_range(1.0, 3.0, 1.0) = 1.0;
uniform vec4 ink : source_color = vec4(0.05, 0.04, 0.06, 1.0);
uniform float strength : hint_range(0.0, 1.0, 0.01) = 0.85;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

float diff(vec4 a, vec4 b) {
	if (mode == 0) {
		return abs(luma(a.rgb) - luma(b.rgb)) + abs(a.a - b.a);
	}
	vec3 d = abs(a.rgb - b.rgb);
	return max(max(d.r, d.g), d.b) + abs(a.a - b.a);
}

void fragment() {
	vec2 px = SCREEN_PIXEL_SIZE * thickness;
	vec4 c = texture(source, SCREEN_UV);
	// Sobel on the chosen measure.
	vec4 tl = texture(source, SCREEN_UV + vec2(-px.x, -px.y));
	vec4 t  = texture(source, SCREEN_UV + vec2(0.0, -px.y));
	vec4 tr = texture(source, SCREEN_UV + vec2(px.x, -px.y));
	vec4 l  = texture(source, SCREEN_UV + vec2(-px.x, 0.0));
	vec4 r  = texture(source, SCREEN_UV + vec2(px.x, 0.0));
	vec4 bl = texture(source, SCREEN_UV + vec2(-px.x, px.y));
	vec4 b  = texture(source, SCREEN_UV + vec2(0.0, px.y));
	vec4 br = texture(source, SCREEN_UV + vec2(px.x, px.y));
	float gx = diff(tr, tl) + 2.0 * diff(r, l) + diff(br, bl);
	float gy = diff(bl, tl) + 2.0 * diff(b, t) + diff(br, tr);
	float edge = step(threshold, sqrt(gx * gx + gy * gy) * 0.25);
	vec3 outc = mix(c.rgb, ink.rgb, edge * strength * ink.a);
	COLOR = vec4(outc, max(c.a, edge * strength * ink.a));
}
