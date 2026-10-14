// HD shafts + fog (ADR-0023, HD light Phase 1): volumetric-looking vertical streaks from bright spots.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float intensity : hint_range(0.0, 2.0, 0.01) = 0.15;
uniform float threshold : hint_range(0.0, 2.0, 0.01) = 0.6;
uniform float length : hint_range(0.0, 0.2, 0.005) = 0.08;
uniform float decay : hint_range(0.8, 0.999, 0.001) = 0.94;
uniform vec4 shaft_tint : source_color = vec4(1.0, 0.85, 0.6, 1.0);

float spot(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	vec3 lt = texture(light_tex, cuv).rgb;
	float l = max(max(lt.r, lt.g), lt.b);
	return clamp((l - threshold) / 0.4, 0.0, 1.0);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	// Column march: 12 taps upward with exponential falloff, so a torch or
	// lava pool throws a soft shaft that fades with height; a second shorter
	// downward march grounds the fog around the flame. Stateless, no TIME.
	vec3 acc = vec3(0.0);
	float illum = 1.0;
	vec2 step_uv = vec2(0.0, length / 12.0);
	for (int i = 1; i <= 12; i++) {
		acc += vec3(spot(SCREEN_UV + step_uv * float(i))) * illum;
		illum *= decay;
	}
	illum = 0.6;
	for (int j = 1; j <= 6; j++) {
		acc += vec3(spot(SCREEN_UV - step_uv * 0.5 * float(j))) * illum * 0.5;
		illum *= decay;
	}
	vec3 shafts = shaft_tint.rgb * acc * intensity * 0.12;
	COLOR = vec4(c.rgb + shafts * (1.0 - c.rgb * 0.4), c.a);
}
