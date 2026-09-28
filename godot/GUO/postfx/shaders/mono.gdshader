// Black & white, sepia and noir (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform int style : hint_range(0, 2, 1) = 0;          // 0 black & white, 1 sepia, 2 noir
uniform float contrast : hint_range(0.5, 2.5, 0.01) = 1.0;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float k = contrast;
	if (style == 2) {
		// Noir: steep contrast, crushed shadows, a cold cast in the lights.
		k = contrast * 1.8;
	}
	l = clamp((l - 0.5) * k + 0.5, 0.0, 1.0);
	vec3 m = vec3(l);
	if (style == 1) {
		m = vec3(l * 1.07, l * 0.87, l * 0.62) + vec3(0.04, 0.02, 0.0);
	} else if (style == 2) {
		l = smoothstep(0.08, 0.92, l);
		m = vec3(l * 0.96, l * 0.98, l * 1.04);
	}
	COLOR = vec4(mix(c.rgb, clamp(m, 0.0, 1.0), strength), c.a);
}
