// Underwater: sine wobble with a blue-green tint (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float distortion : hint_range(0.0, 0.05, 0.001) = 0.02;
uniform float tint : hint_range(0.0, 1.0, 0.01) = 0.3;

void fragment() {
	vec2 uv = SCREEN_UV;
	uv.x += sin(uv.y * 10.0 + TIME * 2.0) * distortion;
	uv.y += cos(uv.x * 10.0 + TIME * 2.0) * distortion * 0.5;
	vec4 c = texture(source, uv);
	vec3 u = vec3(c.r * (1.0 - tint * 0.5), c.g * (1.0 - tint * 0.2), c.b * (1.0 + tint * 0.3));
	COLOR = vec4(u, c.a);
}
