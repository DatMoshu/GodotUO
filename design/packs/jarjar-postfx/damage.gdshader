// Damage effects: hit flash, poison pulse, paralysis crackle and death fade (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform int mode : hint_enum("Hit flash red", "Poison green pulse", "Paralysis blue crackle", "Death grey fade") = 0;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 o = c.rgb;
	if (mode == 0) {
		vec2 ct = SCREEN_UV - 0.5;
		float edge = clamp(dot(ct, ct) * 2.0, 0.0, 1.0);
		float k = clamp(intensity * (0.4 + 0.6 * edge), 0.0, 1.0);
		o = mix(o, vec3(0.8, 0.05, 0.05), k);
	} else if (mode == 1) {
		float pulse = 0.5 + 0.5 * sin(TIME * 4.0);
		float k = clamp(intensity * (0.5 + 0.5 * pulse), 0.0, 1.0) * 0.7;
		o = mix(o, vec3(0.1, 0.6, 0.1), k);
	} else if (mode == 2) {
		vec2 cell = floor(SCREEN_UV / SCREEN_PIXEL_SIZE / 4.0) + fract(TIME * 2.0);
		float n = fract(sin(dot(cell, vec2(12.9898, 78.233))) * 43758.5453);
		float crackle = step(0.85, n) * intensity;
		float k = clamp(intensity * 0.35 + crackle, 0.0, 1.0);
		o = mix(o, vec3(0.2, 0.4, 1.0), k);
	} else {
		float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
		vec3 grey = vec3(l) * vec3(0.7, 0.7, 0.75);
		vec2 ct = SCREEN_UV - 0.5;
		float v = 1.0 - dot(ct, ct) * intensity;
		o = mix(o, grey * 0.4 * v, clamp(intensity, 0.0, 1.0));
	}
	COLOR = vec4(o, c.a);
}
