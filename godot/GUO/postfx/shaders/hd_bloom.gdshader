// HD bloom (ADR-0023, HD light Phase 1): HDR-aware glow around lit hotspots + bright world.
// Runs AFTER hd_tonemap in the hd_light look (glow on the tonemapped image).
// postfx: heavy   (25 taps a pixel; half resolution on the mobile tier, ADR-0023)
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float threshold : hint_range(0.0, 2.0, 0.01) = 1.0;
uniform float radius : hint_range(1.0, 16.0, 0.5) = 4.5;
uniform float intensity : hint_range(0.0, 3.0, 0.01) = 0.4;
uniform float from_world : hint_range(0.0, 1.0, 0.01) = 0.2;
uniform vec4 glow_tint : source_color = vec4(1.0, 0.82, 0.55, 1.0);
// Phase 5 weather (pushed per-frame from HdWeather; no hint so the menu
// hides it): wet ground reflects torchlight, so the hotspot threshold drops
// a little and damp reflections bloom. 0 is Phase 1-4, bit-identical.
uniform float hd_wet = 0.0;

float bright(vec2 uv) {
	vec3 lt = texture(light_tex, uv).rgb;
	vec3 w = texture(source, uv).rgb;
	float l = max(max(lt.r, lt.g), lt.b);
	float wl = dot(w, vec3(0.299, 0.587, 0.114)) * from_world;
	float thresh = threshold - clamp(hd_wet, 0.0, 1.0) * 0.25;
	return smoothstep(thresh, thresh + 0.4, max(l, wl));
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float acc = 0.0;
	float total = 0.0;
	// 3 rings of 8 taps plus the centre, wider than bloom.gdshader.
	for (int ring = 1; ring <= 3; ring++) {
		float rr = radius * float(ring) / 3.0;
		float w = 1.0 - float(ring - 1) * 0.3;
		for (int k = 0; k < 8; k++) {
			float a = float(k) * 0.785398 + float(ring) * 0.39;
			acc += bright(SCREEN_UV + vec2(cos(a), sin(a)) * rr * SCREEN_PIXEL_SIZE) * w;
			total += w;
		}
	}
	acc = (acc + bright(SCREEN_UV)) / (total + 1.0);
	vec3 glow = glow_tint.rgb * acc * intensity;
	COLOR = vec4(c.rgb + glow * (1.0 - c.rgb * 0.4), c.a);
}
