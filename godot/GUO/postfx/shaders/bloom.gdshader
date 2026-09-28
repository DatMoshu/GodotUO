// Bloom from light (ADR-0023): a soft glow around what the light target lights
// (torches, spells, lamps: the client's own light sprites), plus, optionally,
// around the world's brightest pixels. One stateless pass: a sparse disc of
// taps, no blur carried between frames.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float threshold : hint_range(0.0, 1.0, 0.01) = 0.55;
uniform float radius : hint_range(1.0, 12.0, 0.5) = 6.0;
uniform float intensity : hint_range(0.0, 2.0, 0.01) = 0.7;
uniform float from_world : hint_range(0.0, 1.0, 0.01) = 0.25;   // how much bright world pixels glow too
uniform vec4 glow_tint : source_color = vec4(1.0, 0.86, 0.6, 1.0);

float bright(vec2 uv) {
	vec3 lt = texture(light_tex, uv).rgb;
	vec3 w = texture(source, uv).rgb;
	float l = max(max(lt.r, lt.g), lt.b);
	float wl = dot(w, vec3(0.299, 0.587, 0.114)) * from_world;
	return smoothstep(threshold, 1.0, max(l, wl));
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float acc = 0.0;
	float total = 0.0;
	// 3 rings of 8 taps plus the centre.
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
	COLOR = vec4(c.rgb + glow * (1.0 - c.rgb * 0.5), c.a);
}
