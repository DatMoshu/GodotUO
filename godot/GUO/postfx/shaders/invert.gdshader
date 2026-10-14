// Invert (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	COLOR = vec4(mix(c.rgb, vec3(1.0) - c.rgb, intensity), c.a);
}
