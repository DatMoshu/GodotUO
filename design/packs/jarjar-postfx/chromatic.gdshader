// Chromatic aberration: radial RGB fringing from the frame centre (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 0.03, 0.001) = 0.005;

void fragment() {
	vec2 d = normalize(SCREEN_UV - 0.5) * amount;
	float r = texture(source, SCREEN_UV + d).r;
	vec4 c = texture(source, SCREEN_UV);
	float b = texture(source, SCREEN_UV - d).b;
	COLOR = vec4(vec3(r, c.g, b), c.a);
}
