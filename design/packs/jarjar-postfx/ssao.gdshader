// Luma-edge contact darkening (ADR-0023).
// Approx: true depth-based SSAO needs the JAR G-buffer on HeightMapSampler (s2), which GUO has no writer for yet; crevices are guessed from local luminance differences only, so flat-shaded corners will not occlude correctly.
// postfx: heavy
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.45;
uniform float radius : hint_range(0.001, 0.05, 0.001) = 0.018;
uniform float bias : hint_range(0.0, 0.02, 0.0005) = 0.001;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float center = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	vec2 offs[8] = vec2[8](vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0),
		vec2(0.7071, 0.7071), vec2(-0.7071, 0.7071), vec2(0.7071, -0.7071), vec2(-0.7071, -0.7071));
	float occ = 0.0;
	float vsum = 0.0;
	float wsum = 0.0;
	for (int i = 0; i < 8; i++) {
		float sl = dot(texture(source, SCREEN_UV + offs[i] * radius).rgb, vec3(0.299, 0.587, 0.114));
		float diff = sl - center;
		float contrib = clamp(diff + bias, 0.0, 1.0) * 0.5 + abs(diff) * 0.5;
		float w = 1.0;
		if (i > 3) {
			w = 0.75;
		}
		occ += contrib * w;
		vsum += abs(diff);
		wsum += w;
	}
	occ /= wsum;
	float boost = clamp(vsum / 8.0 * 2.0, 0.0, 1.0) * 0.3;
	float ao = clamp(1.0 - clamp(occ + boost, 0.0, 1.0) * intensity, 0.0, 1.0);
	ao = smoothstep(0.0, 1.0, ao);
	COLOR = vec4(c.rgb * ao, c.a);
}
