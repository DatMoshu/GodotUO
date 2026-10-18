// Film grain: animated hash noise over the frame (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 0.5, 0.01) = 0.1;
uniform float size : hint_range(1.0, 5.0, 0.1) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 cell = SCREEN_UV / SCREEN_PIXEL_SIZE / size + fract(TIME);
	float g = fract(sin(dot(cell, vec2(12.9898, 78.233))) * 43758.5453) * 2.0 - 1.0;
	COLOR = vec4(clamp(c.rgb + g * intensity, 0.0, 1.0), c.a);
}
