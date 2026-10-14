// Pixelate (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float size : hint_range(1.0, 20.0, 1.0) = 4.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 ps = size * SCREEN_PIXEL_SIZE;
	vec2 uv = floor(SCREEN_UV / ps) * ps;
	vec3 b = texture(source, uv).rgb;
	COLOR = vec4(b, c.a);
}
