// Sharpen (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.5;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 b = (texture(source, SCREEN_UV + vec2(-SCREEN_PIXEL_SIZE.x, 0.0)).rgb + texture(source, SCREEN_UV + vec2(SCREEN_PIXEL_SIZE.x, 0.0)).rgb + texture(source, SCREEN_UV + vec2(0.0, -SCREEN_PIXEL_SIZE.y)).rgb + texture(source, SCREEN_UV + vec2(0.0, SCREEN_PIXEL_SIZE.y)).rgb) * 0.25;
	vec3 s = clamp(c.rgb + (c.rgb - b) * intensity, 0.0, 1.0);
	COLOR = vec4(s, c.a);
}
