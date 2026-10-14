// Lightning flash: full-screen tint driven by weather intensity (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform vec4 bolt_color : source_color = vec4(0.9, 0.95, 1.0, 1.0);
uniform float speed : hint_range(0.0, 12.0, 0.1) = 6.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float flicker = 0.9 + 0.1 * sin(TIME * speed * 6.2831 + sin(TIME * speed * 3.7) * 1.3);
	vec3 outc = mix(c.rgb, bolt_color.rgb, clamp(intensity * flicker, 0.0, 1.0));
	COLOR = vec4(outc, c.a);
}
