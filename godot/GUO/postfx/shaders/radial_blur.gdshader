// Radial blur (ADR-0023).
// postfx: heavy
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 0.05, 0.001) = 0.01;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 center = vec2(0.5, 0.5);
	vec2 d = SCREEN_UV - center;
	vec3 acc = vec3(0.0);
	for (int i = 0; i < 8; i++) {
		acc += texture(source, center + d * (1.0 - amount * (float(i) / 8.0))).rgb;
	}
	COLOR = vec4(acc / 8.0, c.a);
}
