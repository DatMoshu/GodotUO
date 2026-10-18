// Y-driven depth-of-field circle-of-confusion blur (ADR-0023).
// Approx: focus is a horizontal screen-Y band only, fixed 9-tap kernel; JAR read the G-buffer depth map and used a 13-tap kernel with separate near/far perspective, neither of which exists here.
// postfx: heavy
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float focus_y : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float focus_range : hint_range(0.01, 0.5, 0.01) = 0.12;
uniform float max_blur : hint_range(0.0, 12.0, 0.1) = 5.0;
uniform float near_scale : hint_range(0.0, 1.0, 0.01) = 0.7;
uniform float far_scale : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float dist = abs(SCREEN_UV.y - focus_y);
	float in_focus = clamp((focus_range - dist) / max(focus_range, 0.001), 0.0, 1.0);
	float coc = 1.0 - in_focus;
	float scale = far_scale;
	if (SCREEN_UV.y < focus_y) {
		scale = near_scale;
	}
	float radius = coc * max_blur * scale;
	vec2 px = SCREEN_PIXEL_SIZE * radius;
	vec4 b = texture(source, SCREEN_UV) * 0.2;
	b += texture(source, SCREEN_UV + vec2(px.x, 0.0)) * 0.12;
	b += texture(source, SCREEN_UV - vec2(px.x, 0.0)) * 0.12;
	b += texture(source, SCREEN_UV + vec2(0.0, px.y)) * 0.12;
	b += texture(source, SCREEN_UV - vec2(0.0, px.y)) * 0.12;
	b += texture(source, SCREEN_UV + px) * 0.08;
	b += texture(source, SCREEN_UV - px) * 0.08;
	b += texture(source, SCREEN_UV + vec2(px.x, -px.y)) * 0.08;
	b += texture(source, SCREEN_UV - vec2(px.x, -px.y)) * 0.08;
	float k = clamp(coc * 2.0, 0.0, 1.0);
	COLOR = vec4(mix(c.rgb, b.rgb, k), c.a);
}
