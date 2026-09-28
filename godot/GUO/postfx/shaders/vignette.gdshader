// Vignette: darken (or tint) toward the edges of the world view (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float radius : hint_range(0.1, 1.5, 0.01) = 0.8;
uniform float softness : hint_range(0.01, 1.0, 0.01) = 0.45;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 0.6;
uniform vec4 color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 d = (SCREEN_UV - 0.5) * vec2(SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x, 1.0);
	float r = length(d) * 1.41421;
	float v = smoothstep(radius - softness, radius, r) * strength;
	COLOR = vec4(mix(c.rgb, color.rgb, v * color.a), c.a);
}
