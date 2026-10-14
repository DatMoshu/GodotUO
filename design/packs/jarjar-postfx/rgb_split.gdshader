// RGB split: horizontal channel separation (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 0.03, 0.001) = 0.005;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float r = texture(source, SCREEN_UV + vec2(amount, 0.0)).r;
	float b = texture(source, SCREEN_UV - vec2(amount, 0.0)).b;
	COLOR = vec4(vec3(r, c.g, b), c.a);
}
