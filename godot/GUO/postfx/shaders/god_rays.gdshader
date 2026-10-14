// God rays: sun shafts from a screen-space light position (ADR-0023).
// postfx: heavy   (24 radial taps a pixel; half resolution on the mobile tier, ADR-0023)
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.3;
uniform float light_x : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float light_y : hint_range(0.0, 1.0, 0.01) = 0.15;
uniform float density : hint_range(0.1, 1.0, 0.01) = 0.4;
uniform float decay : hint_range(0.8, 0.999, 0.001) = 0.96;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 light_pos = vec2(light_x, light_y);
	vec2 delta = (SCREEN_UV - light_pos) * density / 24.0;
	vec2 uv = SCREEN_UV;
	float illum = 1.0;
	vec3 acc = vec3(0.0);
	for (int i = 0; i < 24; i++) {
		uv -= delta;
		vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
		vec3 s = texture(source, cuv).rgb;
		float l = dot(s, vec3(0.299, 0.587, 0.114));
		s *= clamp(l - 0.5, 0.0, 1.0) * 2.0 * illum * 0.5;
		acc += s;
		illum *= decay;
	}
	vec3 outc = c.rgb + acc * intensity * 0.4;
	COLOR = vec4(outc, c.a);
}
