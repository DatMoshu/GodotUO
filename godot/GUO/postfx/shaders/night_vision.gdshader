// Night vision: green phosphor with scanline shimmer, animated noise and edge falloff (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float s = sin(SCREEN_UV.y * 800.0) * 0.05;
	float n = fract(sin(dot(SCREEN_UV + fract(TIME * 0.1), vec2(12.9898, 78.233))) * 43758.5453) * 0.1;
	vec2 ct = SCREEN_UV - 0.5;
	float v = 1.0 - dot(ct, ct) * 2.0;
	vec3 nv = vec3(0.1, l * 1.5 + s + n, 0.1) * v;
	COLOR = vec4(mix(c.rgb, nv, intensity), c.a);
}
