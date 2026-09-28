// Colour grading with a LUT (ADR-0023): `lut_tex` is a size x size x size cube
// laid out as a strip, size*size wide and size high (blue picks the tile,
// red the column, green the row). Built-ins are in res://postfx/luts/; a preset
// can name its own PNG beside it in the user folder.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D lut_tex : filter_linear;
uniform float lut_size : hint_range(8.0, 64.0, 1.0) = 16.0;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 1.0;

vec3 lookup(vec3 c) {
	float n = lut_size;
	float b = c.b * (n - 1.0);
	float b0 = floor(b);
	float b1 = min(b0 + 1.0, n - 1.0);
	// Sample texel centres so the linear filter only mixes along red and green.
	vec2 rg = (c.rg * (n - 1.0) + 0.5) / vec2(n * n, n);
	vec3 s0 = texture(lut_tex, rg + vec2(b0 / n, 0.0)).rgb;
	vec3 s1 = texture(lut_tex, rg + vec2(b1 / n, 0.0)).rgb;
	return mix(s0, s1, b - b0);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	COLOR = vec4(mix(c.rgb, lookup(clamp(c.rgb, 0.0, 1.0)), strength), c.a);
}
