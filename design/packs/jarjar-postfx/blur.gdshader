// Blur (ADR-0023).
// postfx: heavy
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 10.0, 0.1) = 2.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 acc = vec3(0.0);
	for (int x = -2; x <= 2; x++) {
		for (int y = -2; y <= 2; y++) {
			acc += texture(source, SCREEN_UV + vec2(float(x), float(y)) * amount * SCREEN_PIXEL_SIZE).rgb;
		}
	}
	COLOR = vec4(acc / 25.0, c.a);
}
